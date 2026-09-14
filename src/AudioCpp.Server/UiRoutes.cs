using System.Text.Json;
using AudioCpp.Packages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// The twelve <c>/v1/ui/*</c> routes that back a browser model manager.
/// </summary>
/// <remarks>
/// Every one of them is gated on <c>ui_management</c> and answers 403 when it
/// is off, as upstream does. They install files, delete directories, walk the
/// filesystem and accept uploads, so the gate is the whole of their security
/// model — on a reachable port with it enabled, this API can read and write
/// anywhere the server process can.
/// </remarks>
internal static class UiRoutes
{
    public static void Map(WebApplication app, ModelPool pool, InstallJobs jobs,
                           ServerConfig config, Action<string> log)
    {
        // One helper rather than twelve copies of the same four lines. The
        // check has to be on every route: a UI that can be told management is
        // off will hide the buttons, and something that is not that UI will
        // call the endpoints anyway.
        IResult? Denied(string what) => pool.ManagementEnabled
            ? null
            : Routes.Problem(403, "forbidden", $"{what} is disabled");

        async Task<JsonElement> BodyAsync(HttpRequest request, CancellationToken cancel)
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<JsonElement>(
                    request.Body, cancellationToken: cancel);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"malformed JSON: {error.Message}");
            }
        }

        static string Field(JsonElement body, string name) =>
            body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";

        static bool Flag(JsonElement body, string name) =>
            body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        // --- package installation -------------------------------------------

        app.MapPost("/v1/ui/models/install",
            async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI model installation") is { } denied) return denied;
            try
            {
                var body = await BodyAsync(request, cancel);
                var id = Field(body, "id");
                if (id.Length == 0) return Routes.Problem(400, "invalid_request_error", "install requires an 'id'");
                log($"POST /v1/ui/models/install  {id}");
                return Results.Json(jobs.Start(id, Flag(body, "overwrite")));
            }
            catch (InvalidDataException error)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        app.MapGet("/v1/ui/models/install-status", (HttpRequest request) =>
        {
            if (Denied("UI model installation") is { } denied) return denied;
            return Results.Json(jobs.Status(request.Query["id"].ToString()));
        });

        app.MapPost("/v1/ui/models/install/stop",
            async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI model installation") is { } denied) return denied;
            try
            {
                var id = Field(await BodyAsync(request, cancel), "id");
                if (id.Length == 0) return Routes.Problem(400, "invalid_request_error", "stop requires an 'id'");
                log($"POST /v1/ui/models/install/stop  {id}");
                return Results.Json(jobs.Stop(id));
            }
            catch (InvalidDataException error)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        app.MapPost("/v1/ui/models/clean-partial",
            async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI model cleanup") is { } denied) return denied;
            try
            {
                var id = Field(await BodyAsync(request, cancel), "id");
                log($"POST /v1/ui/models/clean-partial  {id}");
                return Results.Json(jobs.CleanPartial(id));
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        app.MapPost("/v1/ui/models/delete",
            async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI model removal") is { } denied) return denied;
            try
            {
                var id = Field(await BodyAsync(request, cancel), "id");
                log($"POST /v1/ui/models/delete  {id}");
                return Results.Json(jobs.Remove(id));
            }
            catch (Exception error) when (error is InvalidDataException or IOException
                                          or UnauthorizedAccessException)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        app.MapGet("/v1/ui/models/package-sizes", () =>
            Denied("UI model installation") ?? Results.Json(jobs.PackageSizes()));

        // --- where models live ----------------------------------------------

        IResult ModelsRootJson() => Results.Json(new
        {
            models_root = jobs.ModelsRoot,
            default_models_root = config.ModelsRoot,
            is_default = jobs.ModelsRoot == config.ModelsRoot,
        });

        app.MapGet("/v1/ui/models-root", () =>
            Denied("UI model management") ?? ModelsRootJson());

        app.MapPost("/v1/ui/models-root", async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI model management") is { } denied) return denied;
            try
            {
                var requested = Field(await BodyAsync(request, cancel), "path");
                var resolved = requested.Length == 0
                    ? config.ModelsRoot
                    : Path.GetFullPath(requested);

                if (resolved == jobs.ModelsRoot) return ModelsRootJson();

                // Refused while anything is downloading, because the root is
                // where a running job is writing: moving it mid-download leaves
                // half a package in the old place and half in the new, and
                // neither is loadable.
                if (jobs.Busy)
                {
                    return Routes.Problem(409, "model_install_active",
                        "cannot change the models folder while a package download is running");
                }
                if (File.Exists(resolved))
                {
                    return Routes.Problem(400, "invalid_request_error",
                        $"models folder path is not a directory: {resolved}");
                }
                Directory.CreateDirectory(resolved);
                jobs.ModelsRoot = resolved;
                log($"models root changed to {resolved}");
                return ModelsRootJson();
            }
            catch (Exception error) when (error is InvalidDataException or IOException
                                          or UnauthorizedAccessException or ArgumentException)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        // --- filesystem inspection -------------------------------------------

        app.MapPost("/v1/ui/browse-directories",
            async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI directory browsing") is { } denied) return denied;
            try
            {
                var requested = Field(await BodyAsync(request, cancel), "path");
                var current = Path.GetFullPath(
                    requested.Length == 0 ? jobs.ModelsRoot : requested);
                if (!Directory.Exists(current))
                {
                    return Routes.Problem(400, "invalid_request_error",
                        $"folder is not accessible: {current}");
                }

                // Unreadable subdirectories are skipped rather than failing the
                // listing: one directory the server cannot enter is normal on a
                // real filesystem, and it should not hide the fifty it can.
                var directories = new List<object>();
                try
                {
                    foreach (var entry in Directory.EnumerateDirectories(current)
                                 .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    {
                        directories.Add(new { name = Path.GetFileName(entry), path = entry });
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    log($"browse: {current} partially unreadable: {error.Message}");
                }

                var parent = Path.GetDirectoryName(current) ?? "";
                return Results.Json(new
                {
                    current,
                    parent,
                    roots = OperatingSystem.IsWindows()
                        ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).ToArray()
                        : ["/"],
                    directories = directories.ToArray(),
                });
            }
            catch (Exception error) when (error is InvalidDataException or IOException
                                          or UnauthorizedAccessException or ArgumentException)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        app.MapPost("/v1/ui/path-status", async (HttpRequest request, CancellationToken cancel) =>
        {
            if (Denied("UI path inspection") is { } denied) return denied;
            try
            {
                var path = Field(await BodyAsync(request, cancel), "path");
                var resolved = path.Length > 0 ? Path.GetFullPath(path) : "";
                var file = resolved.Length > 0 && File.Exists(resolved);
                var directory = resolved.Length > 0 && Directory.Exists(resolved);
                return Results.Json(new
                {
                    path = resolved,
                    exists = file || directory,
                    directory,
                    file,
                });
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                return Routes.Problem(400, "invalid_request_error", error.Message);
            }
        });

        // --- uploads and previews --------------------------------------------

        app.MapPost("/v1/ui/upload", async (HttpContext http, CancellationToken cancel) =>
        {
            if (!pool.ManagementEnabled)
            {
                return Routes.Problem(403, "forbidden", "UI uploads are disabled");
            }

            var name = http.Request.Headers["x-audiocpp-filename"].ToString();
            if (name.Length == 0) name = "audio.wav";
            // The client names the file and the server must not believe it: a
            // name carrying a path would otherwise decide where the write lands.
            name = Path.GetFileName(name);
            if (name.Length == 0 || name is "." or "..")
            {
                return Routes.Problem(400, "invalid_request_error", "upload filename is not usable");
            }

            var directory = Path.Combine(jobs.ModelsRoot, "uploads");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, $"{Guid.NewGuid():N}-{name}");

            long written;
            await using (var file = File.Create(target))
            {
                await http.Request.Body.CopyToAsync(file, cancel);
                written = file.Length;
            }
            if (written == 0)
            {
                File.Delete(target);
                return Routes.Problem(400, "invalid_request_error", "upload body is empty");
            }

            log($"POST /v1/ui/upload  {name}  {written} bytes");
            return Results.Json(new { path = target, bytes = written });
        });

        app.MapGet("/v1/ui/voice-preview", (HttpRequest request) =>
        {
            if (Denied("UI voice preview") is { } denied) return denied;

            var name = request.Query["voice"].ToString();
            if (name.Length == 0)
            {
                return Routes.Problem(400, "invalid_request_error", "voice-preview requires a 'voice'");
            }
            if (config.VoiceDir.Length == 0)
            {
                return Routes.Problem(404, "not_found", "no voice_dir is configured");
            }

            // Resolved and then checked to be inside the voice directory. A
            // name is a client-supplied string, and "../../etc/passwd" is a
            // valid one -- GetFileName alone would be enough here, but the
            // containment check is what stays correct if this ever accepts
            // nested names.
            var root = Path.GetFullPath(config.VoiceDir);
            var file = Path.GetFullPath(Path.Combine(root, Path.GetFileName(name) + ".wav"));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(file))
            {
                return Routes.Problem(404, "not_found", $"no voice named '{name}'");
            }
            return Results.File(file, "audio/wav");
        });
    }
}
