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
            var app = builder.Build();

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
