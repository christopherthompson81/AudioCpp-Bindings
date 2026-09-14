using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// server.json, CORS, request-body policy, and the voice library.
/// </summary>
/// <remarks>
/// The load/save assertions matter more than they look. This file is the
/// interchange format with the reference server, so a key spelled differently
/// here is a config that means one thing under one server and another under the
/// other — a difference no test of the routes would catch, because both would
/// pass their own.
/// </remarks>
internal static class Configuration
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var root = Directory.CreateTempSubdirectory("audiocpp-config");
        try
        {
            // 1. A config written by hand, in upstream's spelling, read here.
            var path = Path.Combine(root.FullName, "server.json");
            await File.WriteAllTextAsync(path, """
            {
              "host": "127.0.0.1",
              "port": 9123,
              "backend": "cpu",
              "threads": 4,
              "lazy_load": true,
              "ui": false,
              "ui_management": true,
              "cors_origins": "https://example.invalid",
              "log_request_body": true,
              "max_request_body_bytes": 1048576,
              "busy_timeout_ms": 1234,
              "max_loaded_models": 2,
              "voice_dir": "voices",
              "live_ingest": { "total_timeout_ms": 42000 },
              "frontend_options": { "kept": "verbatim" },
              "models": [
                { "id": "tts", "family": "kokoro_tts", "path": "m.gguf", "task": "tts" }
              ]
            }
            """);

            var loaded = ServerConfigFile.Load(path);
            Check("port, threads and backend are read", loaded.Port == 9123
                  && loaded.Threads == 4 && loaded.Backend == "cpu");
            Check("'ui' is the key, not 'ui_enabled'", !loaded.UiEnabled);
            Check("ui_management is read", loaded.UiManagement);
            Check("cors_origins is read", loaded.CorsOrigins == "https://example.invalid");
            Check("log_request_body and max_request_body_bytes are read",
                  loaded.LogRequestBody && loaded.MaxRequestBodyBytes == 1048576);
            Check("busy_timeout_ms and max_loaded_models are read",
                  loaded.BusyTimeoutMs == 1234 && loaded.MaxLoadedModels == 2);
            Check("a live_ingest subset overrides only what it names",
                  loaded.LiveIngest.TotalTimeoutMs == 42000
                  && loaded.LiveIngest.IdleTimeoutMs == new LiveIngestLimits().IdleTimeoutMs,
                  $"total={loaded.LiveIngest.TotalTimeoutMs} idle={loaded.LiveIngest.IdleTimeoutMs}");

            // Relative paths mean "beside the config", which is what makes a
            // config directory movable.
            Check("relative paths resolve against the config, not the cwd",
                  loaded.VoiceDir == Path.Combine(root.FullName, "voices"), loaded.VoiceDir);
            Check("and so do model paths",
                  loaded.Models[0].Path == Path.Combine(root.FullName, "m.gguf"),
                  loaded.Models[0].Path);

            // 2. Round trip.
            var savedPath = Path.Combine(root.FullName, "saved.json");
            ServerConfigFile.Save(savedPath, loaded);
            var reloaded = ServerConfigFile.Load(savedPath);
            Check("a saved config reads back the same",
                  reloaded.Port == loaded.Port && reloaded.CorsOrigins == loaded.CorsOrigins
                  && reloaded.UiEnabled == loaded.UiEnabled
                  && reloaded.BusyTimeoutMs == loaded.BusyTimeoutMs
                  && reloaded.MaxLoadedModels == loaded.MaxLoadedModels
                  && reloaded.LiveIngest.TotalTimeoutMs == loaded.LiveIngest.TotalTimeoutMs
                  && reloaded.Models.Count == 1 && reloaded.Models[0].Id == "tts");

            // A field this implementation has no opinion about must survive.
            // Dropping it on save would be invisible data loss, discovered only
            // by whatever stopped working.
            var savedText = await File.ReadAllTextAsync(savedPath);
            Check("keys this server does not model are preserved",
                  savedText.Contains("frontend_options", StringComparison.Ordinal)
                  && savedText.Contains("verbatim", StringComparison.Ordinal));

            // 3. CORS, over a running server.
            var voices = Directory.CreateDirectory(Path.Combine(root.FullName, "voices"));
            var config = new ServerConfig
            {
                Port = 19860 + Random.Shared.Next(30),
                CorsOrigins = "https://example.invalid",
                VoiceDir = voices.FullName,
                Models = [],
            };
            await using var server = new AudioCppServer();
            await server.StartAsync(config);
            using var http = new HttpClient { BaseAddress = new Uri(server.Address) };

            var allowed = new HttpRequestMessage(HttpMethod.Get, "/health");
            allowed.Headers.Add("Origin", "https://example.invalid");
            var allowedResponse = await http.SendAsync(allowed);
            Check("a configured origin is allowed",
                  allowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
                  && values.First() == "https://example.invalid");
            Check("and the response says it varies by origin",
                  allowedResponse.Headers.TryGetValues("Vary", out var vary)
                  && vary.Any(v => v.Contains("Origin", StringComparison.OrdinalIgnoreCase)));

            var other = new HttpRequestMessage(HttpMethod.Get, "/health");
            other.Headers.Add("Origin", "https://elsewhere.invalid");
            var otherResponse = await http.SendAsync(other);
            Check("an origin that is not configured gets no allow header",
                  !otherResponse.Headers.Contains("Access-Control-Allow-Origin"));

            // A preflight answered here rather than falling through to a 405,
            // which a browser reports as a CORS failure with no hint that the
            // route exists.
            var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/audio/speech");
            preflight.Headers.Add("Origin", "https://example.invalid");
            var preflightResponse = await http.SendAsync(preflight);
            Check("a preflight is answered, not routed",
                  preflightResponse.StatusCode == HttpStatusCode.NoContent,
                  $"{(int)preflightResponse.StatusCode}");

            await server.StopAsync();

            // 4. Body limit.
            var bounded = new ServerConfig
            {
                Port = config.Port + 31,
                MaxRequestBodyBytes = 2048,
                Models = [],
            };
            await using var small = new AudioCppServer();
            await small.StartAsync(bounded);
            using var smallClient = new HttpClient { BaseAddress = new Uri(small.Address) };
            var oversized = await smallClient.PostAsync("/v1/audio/speech",
                new StringContent(
                    JsonSerializer.Serialize(new { model = "x", input = new string('a', 8192) }),
                    Encoding.UTF8, "application/json"));
            Check("a body past max_request_body_bytes is refused",
                  oversized.StatusCode is HttpStatusCode.RequestEntityTooLarge
                                        or HttpStatusCode.BadRequest,
                  $"{(int)oversized.StatusCode}");
            await small.StopAsync();

            // 5. The voice library, resolved without a model.
            await File.WriteAllBytesAsync(Path.Combine(voices.FullName, "alba.wav"),
                AudioCpp.Wav.ToBytes(new float[16000], 16000, 1));
            await File.WriteAllTextAsync(Path.Combine(voices.FullName, "prompt_text"),
                "alba|The quick brown fox.\nother|Not this one.\n");

            var library = new ServerConfig { VoiceDir = voices.FullName };
            var resolved = library.ResolveLibraryVoice("alba");
            Check("a library voice resolves to its wav and its transcript",
                  resolved is { Text: "The quick brown fox." }
                  && File.Exists(resolved.Value.Wav),
                  resolved?.Text ?? "(none)");

            var missingText = library.ResolveLibraryVoice("nameless");
            Check("a name with no wav resolves to nothing", missingText is null);

            // The transcript matters as much as the audio: a cloning model given
            // a reference clip and no reference text either refuses or clones
            // from a transcript it guessed.
            await File.WriteAllBytesAsync(Path.Combine(voices.FullName, "quiet.wav"),
                AudioCpp.Wav.ToBytes(new float[16], 16000, 1));
            var noTranscript = library.ResolveLibraryVoice("quiet");
            Check("a wav with no prompt_text line still resolves, with empty text",
                  noTranscript is { Text: "" }, noTranscript?.Text ?? "(none)");

            foreach (var attack in new[] { "../secret", "/etc/passwd", "..", "." })
            {
                if (library.ResolveLibraryVoice(attack) is not null)
                {
                    Check($"a voice name cannot escape the library ('{attack}')", false);
                    break;
                }
            }
            Check("a voice name cannot escape the library",
                  new[] { "../secret", "/etc/passwd", "..", "." }
                      .All(attack => library.ResolveLibraryVoice(attack) is null));
        }
        finally
        {
            root.Delete(recursive: true);
        }

        Console.WriteLine(failures == 0 ? "config OK" : $"config: {failures} failure(s)");
        return failures;
    }
}
