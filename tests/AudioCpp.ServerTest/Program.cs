using System.Net;
using System.Text.Json;
using AudioCpp.Server;

// Drives the server over real HTTP rather than calling the handlers: what a
// client sees is the whole point of serving an API, and an in-process call
// would skip binding, routing and serialisation -- which is where a server
// fails.
var failures = 0;

void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  {detail}" : "")}");
    if (!ok) failures++;
}

var port = 18080 + Random.Shared.Next(400);
var config = new ServerConfig
{
    Port = port,
    Models =
    [
        new ServerModel("demo-tts", Task: "tts"),
        new ServerModel("demo-asr", Task: "asr"),
    ],
};

await using var server = new AudioCppServer();
await server.StartAsync(config);
Check("starts", server.State == ServerState.Running, server.Problem);

using var http = new HttpClient { BaseAddress = new Uri(server.Address) };

var health = await http.GetAsync("/health");
var healthBody = await health.Content.ReadAsStringAsync();
Check("GET /health is 200", health.StatusCode == HttpStatusCode.OK, healthBody);
using (var doc = JsonDocument.Parse(healthBody))
{
    Check("health reports the model count",
        doc.RootElement.GetProperty("models").GetInt32() == 2);
}

var models = await http.GetAsync("/v1/models");
var modelsBody = await models.Content.ReadAsStringAsync();
Check("GET /v1/models is 200", models.StatusCode == HttpStatusCode.OK);
using (var doc = JsonDocument.Parse(modelsBody))
{
    var data = doc.RootElement.GetProperty("data");
    Check("lists both configured ids", data.GetArrayLength() == 2);
    Check("uses OpenAI's list shape",
        doc.RootElement.GetProperty("object").GetString() == "list"
        && data[0].GetProperty("object").GetString() == "model");
    Check("ids are the configured ones",
        data.EnumerateArray().Select(m => m.GetProperty("id").GetString()).Order()
            .SequenceEqual(["demo-asr", "demo-tts"]));
}

// A port already taken must fail visibly. A page that says "running" over a
// server that never bound is worse than one that says why.
await using (var second = new AudioCppServer())
{
    await second.StartAsync(config);
    Check("a taken port fails rather than pretending",
        second.State == ServerState.Failed && second.Problem.Length > 0,
        second.Problem.Length > 60 ? second.Problem[..60] : second.Problem);
}

await server.StopAsync();
Check("stops", server.State == ServerState.Stopped);

// And the port is actually released, not merely marked stopped.
await using (var third = new AudioCppServer())
{
    await third.StartAsync(config);
    Check("the port is free again", third.State == ServerState.Running, third.Problem);
    await third.StopAsync();
}

// The routes that need a model run only when one is given: the suite stays
// model-free by default, as the rest of it is.
var speechModel = Environment.GetEnvironmentVariable("AUDIOCPP_TTS_MODEL");
if (speechModel is { Length: > 0 } && (File.Exists(speechModel) || Directory.Exists(speechModel)))
{
    Console.WriteLine();
    failures += await AudioCpp.ServerTest.Speech.RunAsync(
        speechModel,
        Environment.GetEnvironmentVariable("AUDIOCPP_TTS_FAMILY") ?? "",
        Environment.GetEnvironmentVariable("AUDIOCPP_BACKEND") ?? "cpu");
}
else
{
    Console.WriteLine("\nno AUDIOCPP_TTS_MODEL; skipping the routes that need one");
}

Console.WriteLine(failures == 0 ? "server OK" : $"server: {failures} failure(s)");
return failures == 0 ? 0 : 1;
