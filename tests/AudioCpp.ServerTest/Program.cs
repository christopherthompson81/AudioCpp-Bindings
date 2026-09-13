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

// The voices route never loads a model -- it reads config and two directories
// -- so every source it draws from can be checked without one. Worth doing
// here rather than beside the model tests: this is where a mistake in the
// merging shows up, and it costs nothing to run.
{
    var root = Directory.CreateTempSubdirectory("audiocpp-voices");
    try
    {
        var embeddings = Directory.CreateDirectory(Path.Combine(root.FullName, "model", "embeddings"));
        await File.WriteAllTextAsync(Path.Combine(embeddings.FullName, "cosette.safetensors"), "x");
        await File.WriteAllTextAsync(Path.Combine(embeddings.FullName, "marius.safetensors"), "x");
        // Not an embedding, and must not be listed as one.
        await File.WriteAllTextAsync(Path.Combine(embeddings.FullName, "notes.txt"), "x");

        var library = Directory.CreateDirectory(Path.Combine(root.FullName, "voices"));
        await File.WriteAllTextAsync(Path.Combine(library.FullName, "alba.wav"), "x");
        // The same name from two sources, which must appear once.
        await File.WriteAllTextAsync(Path.Combine(library.FullName, "cosette.wav"), "x");

        var voiceConfig = new ServerConfig
        {
            Port = 19900 + Random.Shared.Next(90),
            VoiceDir = library.FullName,
            Models = [new ServerModel("tts", "", Path.Combine(root.FullName, "model"), "tts",
                                      "offline", ["preset_one"])],
        };
        await using var voiceServer = new AudioCppServer();
        await voiceServer.StartAsync(voiceConfig);
        using var voiceClient = new HttpClient { BaseAddress = new Uri(voiceServer.Address) };

        var listed = await voiceClient.GetStringAsync("/v1/audio/voices?model=tts");
        Check("voices merges presets, embeddings and the voice library",
            listed == """{"voices":["alba","cosette","marius","preset_one"]}""", listed);

        // One model configured, so an omitted id resolves to it rather than
        // answering empty.
        var implied = await voiceClient.GetStringAsync("/v1/audio/voices");
        Check("one configured model means the id can be omitted", implied == listed, implied);

        var none = await voiceClient.GetStringAsync("/v1/audio/voices?model=nope");
        Check("an unknown id still lists the voice library",
            none == """{"voices":["alba","cosette"]}""", none);

        await voiceServer.StopAsync();
    }
    finally
    {
        root.Delete(recursive: true);
    }
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

var asrModel = Environment.GetEnvironmentVariable("AUDIOCPP_ASR_MODEL");
var asrAudio = Environment.GetEnvironmentVariable("AUDIOCPP_ASR_AUDIO");
if (asrModel is { Length: > 0 } && asrAudio is { Length: > 0 } && File.Exists(asrAudio)
    && (File.Exists(asrModel) || Directory.Exists(asrModel)))
{
    Console.WriteLine();
    failures += await AudioCpp.ServerTest.Transcription.RunAsync(
        asrModel,
        Environment.GetEnvironmentVariable("AUDIOCPP_ASR_FAMILY") ?? "",
        Environment.GetEnvironmentVariable("AUDIOCPP_BACKEND") ?? "cpu",
        asrAudio);
}
else
{
    Console.WriteLine("no AUDIOCPP_ASR_MODEL/AUDIOCPP_ASR_AUDIO; skipping transcription");
}

if (asrModel is { Length: > 0 } && File.Exists(asrModel)
    && asrAudio is { Length: > 0 } && File.Exists(asrAudio))
{
    Console.WriteLine();
    failures += await AudioCpp.ServerTest.Management.RunAsync(
        asrModel,
        Environment.GetEnvironmentVariable("AUDIOCPP_ASR_FAMILY") ?? "",
        Environment.GetEnvironmentVariable("AUDIOCPP_BACKEND") ?? "cpu",
        asrAudio);
}

var streamTts = Environment.GetEnvironmentVariable("AUDIOCPP_STREAM_TTS_MODEL") ?? "";
var streamAsr = (asrModel is { Length: > 0 } && File.Exists(asrModel)) ? asrModel : "";
if ((streamTts.Length > 0 && File.Exists(streamTts)) || streamAsr.Length > 0)
{
    Console.WriteLine();
    failures += await AudioCpp.ServerTest.Streamed.RunAsync(
        File.Exists(streamTts) ? streamTts : "",
        Environment.GetEnvironmentVariable("AUDIOCPP_STREAM_TTS_FAMILY") ?? "",
        streamAsr,
        Environment.GetEnvironmentVariable("AUDIOCPP_ASR_FAMILY") ?? "",
        Environment.GetEnvironmentVariable("AUDIOCPP_BACKEND") ?? "cpu",
        asrAudio ?? "");
}

var alignModel = Environment.GetEnvironmentVariable("AUDIOCPP_ALIGN_MODEL");
var alignAudio = Environment.GetEnvironmentVariable("AUDIOCPP_ALIGN_AUDIO");
var alignText = Environment.GetEnvironmentVariable("AUDIOCPP_ALIGN_TEXT");
if (alignModel is { Length: > 0 } && File.Exists(alignModel)
    && alignAudio is { Length: > 0 } && File.Exists(alignAudio)
    && alignText is { Length: > 0 })
{
    Console.WriteLine();
    failures += await AudioCpp.ServerTest.Alignment.RunAsync(
        alignModel,
        Environment.GetEnvironmentVariable("AUDIOCPP_ALIGN_FAMILY") ?? "",
        Environment.GetEnvironmentVariable("AUDIOCPP_BACKEND") ?? "cpu",
        alignAudio,
        alignText);
}
else
{
    Console.WriteLine("no AUDIOCPP_ALIGN_MODEL/AUDIOCPP_ALIGN_AUDIO/AUDIOCPP_ALIGN_TEXT; "
                      + "skipping alignment");
}

Console.WriteLine(failures == 0 ? "server OK" : $"server: {failures} failure(s)");
return failures == 0 ? 0 : 1;
