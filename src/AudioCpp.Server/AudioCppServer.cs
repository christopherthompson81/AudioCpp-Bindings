using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioCpp.Server;

/// <summary>What the server is doing, for something watching it.</summary>
public enum ServerState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>
/// audio.cpp's HTTP API, served from these bindings.
/// </summary>
/// <remarks>
/// Hosted in the desktop process rather than supervising the audiocpp_server
/// binary: the decision and its cost are recorded on the epic. The practical
/// consequence is that every route here has to be written and kept in step with
/// upstream's, so each one is checked against a real request rather than
/// assumed from the README.
/// </remarks>
public sealed class AudioCppServer : IAsyncDisposable
{
    private WebApplication? _app;
    private InstallJobs? _jobs;
    private ModelPool? _pool;
    private readonly List<string> _log = [];
    private readonly Lock _logGate = new();

    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>Why the last start failed, when it did.</summary>
    public string Problem { get; private set; } = "";

    public ServerConfig Config { get; private set; } = new();

    /// <summary>Raised for every line the server writes, on a threadpool thread.</summary>
    public event Action<string>? Logged;

    /// <summary>The address a client would use, once running.</summary>
    public string Address => $"http://{Config.Host}:{Config.Port}";

    public IReadOnlyList<string> Log
    {
        get { lock (_logGate) return [.. _log]; }
    }

    private void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_logGate)
        {
            _log.Add(stamped);
            // A server left running for a day should not grow without bound.
            if (_log.Count > 2000) _log.RemoveRange(0, 500);
        }
        Logged?.Invoke(stamped);
    }

    public async Task StartAsync(ServerConfig config, CancellationToken cancel = default)
    {
        if (State is ServerState.Running or ServerState.Starting) return;

        Config = config;
        Problem = "";
        State = ServerState.Starting;
        Write($"starting on {Address}, backend {config.Backend}, "
              + $"{config.Models.Count} model(s)");

        try
        {
            _pool = new ModelPool(config, Write);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // Bound through Kestrel's own options rather than a URL string, so
            // "host" means an address to bind and a loopback default really is
            // loopback-only rather than a hostname that might resolve outward.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(System.Net.IPAddress.Parse(config.Host), config.Port));
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Limits.MaxRequestBodySize = config.MaxRequestBodyBytes);
            var app = builder.Build();

            // One place to turn a busy model into the status that says so.
            // Every route can raise it -- it comes from the pool, not from any
            // one handler -- so catching it per route would be the same four
            // lines a dozen times, and the one that was forgotten would answer
            // 500 for a condition that is not an error.
            app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (ServerBusyException busy) when (!context.Response.HasStarted)
                {
                    Write($"busy: {busy.Message}");
                    context.Response.StatusCode = 503;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            error = new { message = busy.Message, type = "server_busy" },
                        }));
                }
            });

            // Before the routes, so it applies to every one of them including
            // the ones that answer errors. A CORS header attached only to
            // successes tells a browser nothing about why a request failed.
            if (config.CorsOrigins.Length > 0)
            {
                app.Use(async (context, next) =>
                {
                    var origin = context.Request.Headers.Origin.ToString();
                    var allowed = config.CorsOrigins == "*"
                        ? (origin.Length > 0 ? origin : "*")
                        : config.CorsOrigins
                            .Split(',', StringSplitOptions.RemoveEmptyEntries
                                        | StringSplitOptions.TrimEntries)
                            .FirstOrDefault(candidate =>
                                string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase));

                    if (allowed is { Length: > 0 })
                    {
                        context.Response.Headers.AccessControlAllowOrigin = allowed;
                        // Echoing a specific origin makes the response vary by
                        // it, and a cache that missed that would serve one
                        // site's response to another.
                        if (allowed != "*") context.Response.Headers.Vary = "Origin";
                        context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                        context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                    }

                    // A preflight is answered here rather than falling through
                    // to a 405 from routing, which a browser reports as a CORS
                    // failure with no indication that the route exists.
                    if (HttpMethods.IsOptions(context.Request.Method))
                    {
                        context.Response.StatusCode = 204;
                        return;
                    }
                    await next();
                });
            }

            if (config.LogRequestBody)
            {
                app.Use(async (context, next) =>
                {
                    if (context.Request.ContentType?.StartsWith(
                            "application/json", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Buffered so the route can still read it: a body read
                        // once is gone, and logging it would otherwise cost the
                        // request it was logging.
                        context.Request.EnableBuffering();
                        var buffer = new byte[Math.Min(4096, config.MaxRequestBodyBytes)];
                        var read = await context.Request.Body.ReadAsync(buffer);
                        context.Request.Body.Position = 0;
                        var body = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                        Write($"{context.Request.Method} {context.Request.Path} body: {body}"
                              + (read == buffer.Length ? " (truncated)" : ""));
                    }
                    await next();
                });
            }

            _jobs = new InstallJobs(config.ModelSpecsDirectory, Write)
            {
                ModelsRoot = config.ModelsRoot,
            };
            Routes.Map(app, _pool, _jobs, config, Write);

            await app.StartAsync(cancel);
            _app = app;
            State = ServerState.Running;
            Write($"listening on {Address}");

            await _pool.WarmAsync(cancel);
        }
        catch (Exception exception)
        {
            // A port already taken is the common one, and it has to be visible:
            // a page that says "running" over a server that never bound is
            // worse than one that says why it did not.
            Problem = exception.Message;
            State = ServerState.Failed;
            Write($"failed to start: {exception.Message}");
            await TearDownAsync();
        }
    }

    public async Task StopAsync()
    {
        if (State is ServerState.Stopped or ServerState.Stopping) return;

        State = ServerState.Stopping;
        Write("stopping");
        await TearDownAsync();
        State = ServerState.Stopped;
        Write("stopped");
    }

    private async Task TearDownAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            try
            {
                // StopAsync waits for in-flight requests, which is what makes
                // stopping mid-request safe rather than a truncated response.
                await app.StopAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                              or ObjectDisposedException)
            {
            }
            await app.DisposeAsync();
        }

        _pool?.Dispose();
        _pool = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
