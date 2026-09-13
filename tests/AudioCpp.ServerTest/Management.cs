using System.Net;
using System.Text;
using System.Text.Json;
using AudioCpp.Server;

namespace AudioCpp.ServerTest;

/// <summary>
/// Model residency and the generic task route.
/// </summary>
/// <remarks>
/// Residency is the thing worth testing here, and it is only observable
/// indirectly: the API reports what it unloaded, but "unloaded" is a claim
/// about memory. So the check is that the claim and the behaviour agree — a
/// model reports as unloaded, and the next request to it works anyway, because
/// an unload that broke the model would look identical to one that worked
/// right up until someone used it.
/// </remarks>
internal static class Management
{
    public static async Task<int> RunAsync(string model, string family, string backend, string audio)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
            if (!ok) failures++;
        }

        var config = new ServerConfig
        {
            Port = 19300 + Random.Shared.Next(200),
            Backend = backend,
            UiManagement = true,
            Models = [new ServerModel("asr", family, model, "asr")],
        };

        await using var server = new AudioCppServer();
        await server.StartAsync(config);
        if (server.State != ServerState.Running)
        {
            Console.Error.WriteLine($"server did not start: {server.Problem}");
            return 1;
        }

        using var http = new HttpClient
        {
            BaseAddress = new Uri(server.Address),
            Timeout = TimeSpan.FromMinutes(5),
        };

        async Task<(HttpStatusCode Status, string Body)> PostAsync(string route, string json)
        {
            var response = await http.PostAsync(
                route, new StringContent(json, Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // 1. The generic route, which is how the model gets loaded in the first
        // place. Its request shape is the CLI's, not this API's.
        var run = await PostAsync("/v1/tasks/run",
            JsonSerializer.Serialize(new { model = "asr", request = new { audio } }));
        Check("tasks/run is 200", run.Status == HttpStatusCode.OK,
              run.Body.Length > 140 ? run.Body[..140] : run.Body);

        var transcript = "";
        if (run.Status == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(run.Body);
            transcript = document.RootElement.TryGetProperty("text", out var text)
                ? text.GetString() ?? "" : "";
            Check("tasks/run transcribes", transcript.Length > 0, $"{transcript.Length} chars");
            Check("and reports timing",
                  document.RootElement.TryGetProperty("timing", out var timing)
                  && timing.TryGetProperty("wall_ms", out _));
            // An ASR result has no audio, and the route must not invent the
            // field: a client cannot tell "produced nothing" from "produced
            // silence" once an empty audio field is always present.
            Check("and carries no audio field for a model that made none",
                  !document.RootElement.TryGetProperty("audio", out _));
        }

        // The flat form, without the "request" wrapper.
        var flat = await PostAsync("/v1/tasks/run",
            JsonSerializer.Serialize(new { model = "asr", audio }));
        Check("the flat request form works too", flat.Status == HttpStatusCode.OK);

        // 2. Unloading what is loaded.
        var unload = await PostAsync("/v1/tasks/unload_models",
            """{"model_ids":["asr","no-such-model"]}""");
        Check("unload_models is 200", unload.Status == HttpStatusCode.OK, unload.Body);
        Check("it reports the model it unloaded and the id it did not know",
              unload.Body.Contains("\"unloaded\":[\"asr\"]", StringComparison.Ordinal)
              && unload.Body.Contains("\"not_found\":[\"no-such-model\"]", StringComparison.Ordinal),
              unload.Body);

        // A second unload of the same id: known, but no longer resident, so it
        // belongs to neither list. Upstream's contract, and the one that lets a
        // client unload defensively without interpreting a false report.
        var again = await PostAsync("/v1/tasks/unload_models", """{"model_ids":["asr"]}""");
        Check("unloading an idle model reports it in neither list",
              again.Body.Contains("\"unloaded\":[]", StringComparison.Ordinal)
              && again.Body.Contains("\"not_found\":[]", StringComparison.Ordinal),
              again.Body);

        // 3. The claim has to survive contact with a real request.
        var after = await PostAsync("/v1/tasks/run",
            JsonSerializer.Serialize(new { model = "asr", request = new { audio } }));
        Check("an unloaded model reloads transparently", after.Status == HttpStatusCode.OK,
              after.Body.Length > 140 ? after.Body[..140] : after.Body);
        if (after.Status == HttpStatusCode.OK && transcript.Length > 0)
        {
            using var document = JsonDocument.Parse(after.Body);
            var reloaded = document.RootElement.GetProperty("text").GetString() ?? "";
            Check("and transcribes the same thing it did before", reloaded == transcript,
                  reloaded == transcript ? $"{reloaded.Length} chars" : reloaded);
        }

        var all = await PostAsync("/v1/tasks/unload_all_models", "");
        Check("unload_all_models reports what was resident",
              all.Status == HttpStatusCode.OK
              && all.Body.Contains("\"unloaded\":[\"asr\"]", StringComparison.Ordinal),
              all.Body);

        // 4. Bad requests.
        var noIds = await PostAsync("/v1/tasks/unload_models", "{}");
        Check("unload_models without model_ids is a 400",
              noIds.Status == HttpStatusCode.BadRequest);
        var notStrings = await PostAsync("/v1/tasks/unload_models", """{"model_ids":[7]}""");
        Check("a non-string id is a 400", notStrings.Status == HttpStatusCode.BadRequest);
        var noModel = await PostAsync("/v1/tasks/run", """{"request":{"text":"hi"}}""");
        Check("tasks/run without a model is a 400", noModel.Status == HttpStatusCode.BadRequest);
        var unknownModel = await PostAsync("/v1/tasks/run", """{"model":"nope"}""");
        Check("tasks/run with an unknown model is a 404",
              unknownModel.Status == HttpStatusCode.NotFound);

        // 5. Dynamic registration. The same model under a second id, which is
        // the cheap way to prove the route registers something usable rather
        // than merely recording it.
        var load = await PostAsync("/v1/models/load",
            JsonSerializer.Serialize(new { id = "second", path = model, family, task = "asr" }));
        Check("models/load is 200", load.Status == HttpStatusCode.OK, load.Body);
        Check("and reports a fresh registration, not a reconfiguration",
              load.Body.Contains("\"reconfigured\":false", StringComparison.Ordinal), load.Body);

        var listed = await http.GetStringAsync("/v1/models");
        Check("the new id appears in /v1/models",
              listed.Contains("\"second\"", StringComparison.Ordinal), listed);

        var viaSecond = await PostAsync("/v1/tasks/run",
            JsonSerializer.Serialize(new { model = "second", request = new { audio } }));
        Check("the newly loaded model serves requests", viaSecond.Status == HttpStatusCode.OK);

        var reload = await PostAsync("/v1/models/load",
            JsonSerializer.Serialize(new { id = "second", path = model, family, task = "asr" }));
        Check("loading the same registration again is not a reconfiguration",
              reload.Body.Contains("\"reconfigured\":false", StringComparison.Ordinal), reload.Body);

        var badPath = await PostAsync("/v1/models/load",
            JsonSerializer.Serialize(new { id = "broken", path = "/no/such/model.gguf" }));
        Check("a model that cannot be opened is a 400",
              badPath.Status == HttpStatusCode.BadRequest);
        var afterBad = await http.GetStringAsync("/v1/models");
        Check("and is not left registered, failing every request sent to it",
              !afterBad.Contains("\"broken\"", StringComparison.Ordinal), afterBad);

        // A reconfiguration that fails must leave the working registration
        // alone. Rolling it forward into "forget it" would mean one typo in a
        // path deletes a model that was serving requests a moment earlier.
        var brokenReconfigure = await PostAsync("/v1/models/load",
            JsonSerializer.Serialize(new { id = "second", path = "/no/such/model.gguf" }));
        Check("a failed reconfiguration is a 400",
              brokenReconfigure.Status == HttpStatusCode.BadRequest);
        var stillThere = await http.GetStringAsync("/v1/models");
        Check("and leaves the registration it was replacing",
              stillThere.Contains("\"second\"", StringComparison.Ordinal), stillThere);
        var stillWorks = await PostAsync("/v1/tasks/run",
            JsonSerializer.Serialize(new { model = "second", request = new { audio } }));
        Check("which still serves requests", stillWorks.Status == HttpStatusCode.OK,
              stillWorks.Body.Length > 120 ? stillWorks.Body[..120] : stillWorks.Body);

        // Word offsets need the rate that counts them, and a transcription
        // returns no audio to read it from.
        var detailed = await PostAsync("/v1/tasks/run",
            JsonSerializer.Serialize(new { model = "asr", request = new { audio } }));
        using (var document = JsonDocument.Parse(detailed.Body))
        {
            var hasOffsets = document.RootElement.TryGetProperty("words", out var words)
                             && words.GetArrayLength() > 0;
            var rate = document.RootElement.TryGetProperty("sample_rate", out var value)
                ? value.GetInt32() : 0;
            Check("offsets come with the rate that counts them, from the input audio",
                  !hasOffsets || rate > 0, $"words={hasOffsets} sample_rate={rate}");
        }

        var unloadOne = await PostAsync("/v1/models/unload", """{"id":"second"}""");
        Check("models/unload is 200", unloadOne.Status == HttpStatusCode.OK, unloadOne.Body);
        var unloadUnknown = await PostAsync("/v1/models/unload", """{"id":"nope"}""");
        Check("models/unload of an unknown id is a 404",
              unloadUnknown.Status == HttpStatusCode.NotFound);

        await server.StopAsync();

        // 6. The management routes are off unless the config turns them on,
        // because they name a path for the server to open.
        var closed = new ServerConfig
        {
            Port = config.Port + 1,
            Backend = backend,
            Models = [new ServerModel("asr", family, model, "asr")],
        };
        await using (var guarded = new AudioCppServer())
        {
            await guarded.StartAsync(closed);
            using var guardedClient = new HttpClient { BaseAddress = new Uri(guarded.Address) };
            var refused = await guardedClient.PostAsync("/v1/models/load",
                new StringContent("""{"id":"x","path":"y"}""", Encoding.UTF8, "application/json"));
            Check("models/load is 403 unless management is enabled",
                  refused.StatusCode == HttpStatusCode.Forbidden, $"{(int)refused.StatusCode}");
            var refusedUnload = await guardedClient.PostAsync("/v1/models/unload",
                new StringContent("""{"id":"asr"}""", Encoding.UTF8, "application/json"));
            Check("and so is models/unload",
                  refusedUnload.StatusCode == HttpStatusCode.Forbidden);
            await guarded.StopAsync();
        }

        Console.WriteLine(failures == 0 ? "management OK" : $"management: {failures} failure(s)");
        return failures;
    }
}
