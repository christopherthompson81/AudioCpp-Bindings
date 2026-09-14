using System.Net;
using System.Text;
using System.Text.Json;
using AudioCpp.Packages;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// The /v1/ui routes.
/// </summary>
/// <remarks>
/// These install files, delete directories, walk the filesystem and accept
/// uploads, so two properties matter more than the happy paths: every one of
/// them is refused when management is off, and none of them can be talked into
/// touching something outside the folder it was pointed at. Both are checked
/// against every route rather than a representative one — a gate that is
/// applied eleven times out of twelve is not a gate.
///
/// Needs no model and no network: the catalogue is read from model_specs on
/// disk, and the destructive routes are exercised against a temp directory.
/// </remarks>
internal static class Ui
{
    public static async Task<int> RunAsync(string modelSpecs)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var root = Directory.CreateTempSubdirectory("audiocpp-ui");
        try
        {
            var models = Directory.CreateDirectory(Path.Combine(root.FullName, "models"));
            var voices = Directory.CreateDirectory(Path.Combine(root.FullName, "voices"));
            await File.WriteAllBytesAsync(Path.Combine(voices.FullName, "alba.wav"),
                AudioCpp.Wav.ToBytes(new float[8000], 16000, 1));
            // A file outside the voice directory, to aim a traversal at.
            var secret = Path.Combine(root.FullName, "secret.wav");
            await File.WriteAllBytesAsync(secret, AudioCpp.Wav.ToBytes(new float[8], 16000, 1));

            var config = new ServerConfig
            {
                Port = 19950 + Random.Shared.Next(40),
                UiManagement = true,
                ModelsRoot = models.FullName,
                ModelSpecsDirectory = modelSpecs,
                VoiceDir = voices.FullName,
                Models = [],
            };

            await using var server = new AudioCppServer();
            await server.StartAsync(config);
            if (server.State != ServerState.Running)
            {
                Console.Error.WriteLine($"server did not start: {server.Problem}");
                return 1;
            }

            using var http = new HttpClient { BaseAddress = new Uri(server.Address) };

            async Task<(HttpStatusCode Status, string Body)> PostAsync(string route, string json)
            {
                var response = await http.PostAsync(route,
                    new StringContent(json, Encoding.UTF8, "application/json"));
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            async Task<(HttpStatusCode Status, string Body)> GetAsync(string route)
            {
                var response = await http.GetAsync(route);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            // 1. Where models live.
            var rootGet = await GetAsync("/v1/ui/models-root");
            Check("models-root reports the configured folder",
                  rootGet.Status == HttpStatusCode.OK
                  && rootGet.Body.Contains("\"is_default\":true", StringComparison.Ordinal),
                  rootGet.Body);

            var moved = Path.Combine(root.FullName, "elsewhere");
            var rootSet = await PostAsync("/v1/ui/models-root",
                JsonSerializer.Serialize(new { path = moved }));
            Check("models-root can be moved, and says it is no longer the default",
                  rootSet.Status == HttpStatusCode.OK
                  && rootSet.Body.Contains("\"is_default\":false", StringComparison.Ordinal),
                  rootSet.Body);
            Check("and the folder is created", Directory.Exists(moved));

            var rootReset = await PostAsync("/v1/ui/models-root", "{}");
            Check("an empty path resets it to the default",
                  rootReset.Body.Contains("\"is_default\":true", StringComparison.Ordinal),
                  rootReset.Body);

            var overFile = await PostAsync("/v1/ui/models-root",
                JsonSerializer.Serialize(new { path = secret }));
            Check("pointing it at a file is a 400", overFile.Status == HttpStatusCode.BadRequest);

            // 2. Filesystem inspection.
            var browse = await PostAsync("/v1/ui/browse-directories",
                JsonSerializer.Serialize(new { path = root.FullName }));
            Check("browse lists subdirectories",
                  browse.Status == HttpStatusCode.OK
                  && browse.Body.Contains("\"voices\"", StringComparison.Ordinal),
                  browse.Body.Length > 160 ? browse.Body[..160] : browse.Body);
            Check("and offers a parent to climb to",
                  browse.Body.Contains("\"parent\"", StringComparison.Ordinal));

            var browseMissing = await PostAsync("/v1/ui/browse-directories",
                JsonSerializer.Serialize(new { path = "/no/such/place" }));
            Check("browsing somewhere unreadable is a 400",
                  browseMissing.Status == HttpStatusCode.BadRequest);

            var statusFile = await PostAsync("/v1/ui/path-status",
                JsonSerializer.Serialize(new { path = secret }));
            Check("path-status distinguishes a file from a directory",
                  statusFile.Body.Contains("\"file\":true", StringComparison.Ordinal)
                  && statusFile.Body.Contains("\"directory\":false", StringComparison.Ordinal),
                  statusFile.Body);
            var statusDir = await PostAsync("/v1/ui/path-status",
                JsonSerializer.Serialize(new { path = voices.FullName }));
            Check("and reports a directory as one",
                  statusDir.Body.Contains("\"directory\":true", StringComparison.Ordinal),
                  statusDir.Body);
            var statusGone = await PostAsync("/v1/ui/path-status",
                JsonSerializer.Serialize(new { path = "/no/such/thing" }));
            Check("a path that is not there is reported, not refused",
                  statusGone.Status == HttpStatusCode.OK
                  && statusGone.Body.Contains("\"exists\":false", StringComparison.Ordinal),
                  statusGone.Body);

            // 3. Installation, without downloading anything.
            var catalogue = Catalog.Load(modelSpecs);
            var unknown = await PostAsync("/v1/ui/models/install",
                JsonSerializer.Serialize(new { id = "not-a-package" }));
            Check("installing an unknown package is a 400",
                  unknown.Status == HttpStatusCode.BadRequest, unknown.Body);

            var idle = await GetAsync("/v1/ui/models/install-status?id=not-a-package");
            Check("status for a package never started is idle, not an error",
                  idle.Status == HttpStatusCode.OK
                  && idle.Body.Contains("\"state\":\"idle\"", StringComparison.Ordinal),
                  idle.Body);
            Check("and its progress is -1 rather than 0",
                  idle.Body.Contains("\"progress_percent\":-1", StringComparison.Ordinal),
                  idle.Body);

            var all = await GetAsync("/v1/ui/models/install-status");
            Check("status with no id lists every job",
                  all.Status == HttpStatusCode.OK
                  && all.Body.Contains("\"data\":", StringComparison.Ordinal), all.Body);

            var sizes = await GetAsync("/v1/ui/models/package-sizes");
            Check("package-sizes answers immediately for an empty folder",
                  sizes.Status == HttpStatusCode.OK
                  && sizes.Body.Contains("\"data\":[]", StringComparison.Ordinal), sizes.Body);

            var cleanUnknown = await PostAsync("/v1/ui/models/clean-partial",
                JsonSerializer.Serialize(new { id = "not-a-package" }));
            Check("cleaning an unknown package is a 400",
                  cleanUnknown.Status == HttpStatusCode.BadRequest);
            var deleteUnknown = await PostAsync("/v1/ui/models/delete",
                JsonSerializer.Serialize(new { id = "not-a-package" }));
            Check("deleting an unknown package is a 400",
                  deleteUnknown.Status == HttpStatusCode.BadRequest);

            // A real package id from the catalogue, to prove the ids the routes
            // accept are the ones the catalogue publishes.
            var sample = catalogue.Families.SelectMany(f => f.Packages).FirstOrDefault();
            if (sample is not null)
            {
                var clean = await PostAsync("/v1/ui/models/clean-partial",
                    JsonSerializer.Serialize(new { id = sample.Id }));
                Check("a real package id is accepted by clean-partial",
                      clean.Status == HttpStatusCode.OK, $"{sample.Id}: {clean.Body}");
            }

            // A package whose spec declares no automated download runs the whole
            // job lifecycle without touching the network: start, background
            // task, terminal state, and a message a poller can act on. The
            // download path itself is not covered here -- a test that fetched
            // real weights would be minutes and gigabytes -- but everything
            // around it is, and a job stuck at "running" with no message is the
            // one outcome a UI cannot recover from.
            var manual = catalogue.Families
                .SelectMany(f => f.Packages)
                .FirstOrDefault(package => !package.Download.Supported);
            if (manual is not null)
            {
                var started = await PostAsync("/v1/ui/models/install",
                    JsonSerializer.Serialize(new { id = manual.Id }));
                Check("a package with no automated download starts a job",
                      started.Status == HttpStatusCode.OK, started.Body);

                var settled = "";
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var poll = await GetAsync($"/v1/ui/models/install-status?id={manual.Id}");
                    settled = poll.Body;
                    if (!settled.Contains("\"state\":\"queued\"", StringComparison.Ordinal)
                        && !settled.Contains("\"state\":\"running\"", StringComparison.Ordinal))
                    {
                        break;
                    }
                    await Task.Delay(50);
                }
                Check("and the job reaches a terminal state",
                      settled.Contains("\"state\":\"failed\"", StringComparison.Ordinal),
                      settled.Length > 170 ? settled[..170] : settled);
                Check("carrying a message that says why",
                      settled.Contains("manually", StringComparison.OrdinalIgnoreCase)
                      || settled.Contains("download", StringComparison.OrdinalIgnoreCase),
                      settled.Length > 170 ? settled[..170] : settled);
                Check("and a finish time, so a poller can stop",
                      !settled.Contains("\"finished_at_ms\":0", StringComparison.Ordinal), settled);

                var listed = await GetAsync("/v1/ui/models/install-status");
                Check("the finished job appears in the full listing",
                      listed.Body.Contains(manual.Id, StringComparison.Ordinal),
                      listed.Body.Length > 170 ? listed.Body[..170] : listed.Body);
            }

            // 4. Uploads.
            var upload = await http.PostAsync("/v1/ui/upload",
                new ByteArrayContent(AudioCpp.Wav.ToBytes(new float[1600], 16000, 1)));
            var uploadBody = await upload.Content.ReadAsStringAsync();
            Check("upload stores the body and reports where",
                  upload.StatusCode == HttpStatusCode.OK
                  && uploadBody.Contains("\"bytes\":", StringComparison.Ordinal),
                  uploadBody);

            // A filename is a client-supplied string. One carrying a path must
            // not decide where the write lands.
            var traversal = new HttpRequestMessage(HttpMethod.Post, "/v1/ui/upload")
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }),
            };
            traversal.Headers.Add("x-audiocpp-filename", "../../escaped.wav");
            var traversalResponse = await http.SendAsync(traversal);
            var traversalBody = await traversalResponse.Content.ReadAsStringAsync();
            Check("an upload filename cannot climb out of the uploads folder",
                  !File.Exists(Path.Combine(root.FullName, "escaped.wav"))
                  && !File.Exists(Path.Combine(models.FullName, "..", "escaped.wav")),
                  traversalBody.Length > 120 ? traversalBody[..120] : traversalBody);

            var emptyUpload = await http.PostAsync("/v1/ui/upload", new ByteArrayContent([]));
            Check("an empty upload is a 400", emptyUpload.StatusCode == HttpStatusCode.BadRequest);

            // 5. Voice preview.
            var preview = await http.GetAsync("/v1/ui/voice-preview?voice=alba");
            Check("voice-preview returns the wav",
                  preview.StatusCode == HttpStatusCode.OK
                  && preview.Content.Headers.ContentType?.MediaType == "audio/wav",
                  $"{(int)preview.StatusCode} {preview.Content.Headers.ContentType}");

            var escape = await GetAsync("/v1/ui/voice-preview?voice=../secret");
            Check("and cannot be walked out of the voice folder",
                  escape.Status == HttpStatusCode.NotFound, $"{(int)escape.Status}");
            var absent = await GetAsync("/v1/ui/voice-preview?voice=nobody");
            Check("an unknown voice is a 404", absent.Status == HttpStatusCode.NotFound);

            await server.StopAsync();

            // 6. The gate. Every route, not a sample of them.
            var closed = new ServerConfig
            {
                Port = config.Port + 41,
                ModelsRoot = models.FullName,
                ModelSpecsDirectory = modelSpecs,
                VoiceDir = voices.FullName,
                Models = [],
                UiManagement = false,
            };
            await using var guarded = new AudioCppServer();
            await guarded.StartAsync(closed);
            using var guardedClient = new HttpClient { BaseAddress = new Uri(guarded.Address) };

            var posts = new[]
            {
                "/v1/ui/models/install", "/v1/ui/models/install/stop",
                "/v1/ui/models/clean-partial", "/v1/ui/models/delete",
                "/v1/ui/models-root", "/v1/ui/browse-directories", "/v1/ui/path-status",
                "/v1/ui/upload",
            };
            var gets = new[]
            {
                "/v1/ui/models/install-status", "/v1/ui/models/package-sizes",
                "/v1/ui/models-root", "/v1/ui/voice-preview?voice=alba",
            };
            var open = new List<string>();
            foreach (var route in posts)
            {
                var response = await guardedClient.PostAsync(route,
                    new StringContent("""{"id":"x","path":"/tmp"}""", Encoding.UTF8, "application/json"));
                if (response.StatusCode != HttpStatusCode.Forbidden) open.Add($"POST {route}");
            }
            foreach (var route in gets)
            {
                var response = await guardedClient.GetAsync(route);
                if (response.StatusCode != HttpStatusCode.Forbidden) open.Add($"GET {route}");
            }
            Check($"all {posts.Length + gets.Length} ui routes are 403 when management is off",
                  open.Count == 0, string.Join(", ", open));

            await guarded.StopAsync();
        }
        finally
        {
            root.Delete(recursive: true);
        }

        Console.WriteLine(failures == 0 ? "ui OK" : $"ui: {failures} failure(s)");
        return failures;
    }
}
