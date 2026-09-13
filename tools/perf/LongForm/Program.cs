using AudioCpp;
using AudioCpp.PathTest;

var model = args[0];
var audio = args[1];
var mode  = args.Length > 2 ? args[2] : "";
var threads = args.Length > 3 ? int.Parse(args[3]) : Environment.ProcessorCount;

// Backend for the ASR session. Defaults to cpu so existing numbers stay
// reproducible; set ASR_BACKEND=cuda (or vulkan, metal) to measure a GPU.
var backendName = Environment.GetEnvironmentVariable("ASR_BACKEND") ?? "cpu";

var clip = Wav.Read(audio);
Console.WriteLine($"backend={backendName}");
Console.WriteLine($"audio {clip.Samples.Length / clip.Channels / (double)clip.SampleRate:F1}s  offline_mode={(mode.Length > 0 ? mode : "<default>")}");

// mode may be a bare offline_mode value, or one or more k=v pairs.
// "req:k=v" -> request option (bare key); otherwise a parakeet_tdt.* session option.
var parts = mode.Length == 0 ? [] : mode.Split(',');
var reqOpts = new List<KeyValuePair<string, string>>();
var opts = new List<KeyValuePair<string, string>>();
foreach (var part in parts)
{
    if (part.StartsWith("req:"))
    {
        var kv = part[4..].Split('=', 2);
        if (kv.Length != 2)
            throw new ArgumentException($"request option must be req:key=value, got '{part}'");
        reqOpts.Add(new(kv[0], kv[1]));
    }
    else
    {
        var kv = part.Contains('=') ? part.Split('=', 2) : ["offline_mode", part];
        opts.Add(new($"parakeet_tdt.{kv[0]}", kv[1]));
    }
}
Console.WriteLine($"session opts: [{string.Join(", ", opts.Select(o => $"{o.Key}={o.Value}"))}]  request opts: [{string.Join(", ", reqOpts.Select(o => $"{o.Key}={o.Value}"))}]");

using var registry = AudioCppRegistry.Create();
var specOverride = Environment.GetEnvironmentVariable("SPEC_OVERRIDE");
if (!string.IsNullOrEmpty(specOverride)) Console.WriteLine($"spec override: {specOverride}");
using var m = registry.Load(model, new ModelConfig("parakeet_tdt", ModelSpecOverride: specOverride));
using var session = m.CreateSession("asr", "offline", new BackendConfig(backendName, 0, threads), opts.Count > 0 ? opts : null);
using var req = new AudioCppRequest();
req.SetAudio(clip.Samples, clip.SampleRate, clip.Channels);
foreach (var o in reqOpts) req.SetOption(o.Key, o.Value);
var sw = System.Diagnostics.Stopwatch.StartNew();
using var result = session.Run(req);
sw.Stop();
var text = result.Text?.Text ?? "";
Console.WriteLine($"OK in {sw.Elapsed.TotalSeconds:F1}s  words={result.Words.Count}  chars={text.Length}");
Console.WriteLine("head: " + text[..Math.Min(90, text.Length)]);
var outPath = Environment.GetEnvironmentVariable("LONGFORM_OUT");
if (!string.IsNullOrEmpty(outPath))
{
    File.WriteAllText(outPath, text);
    // Word timings let us see WHERE content is missing, not just how much.
    File.WriteAllLines(outPath + ".words",
        result.Words.Select(w => $"{w.StartSample / (double)clip.SampleRate:F2}\t{w.EndSample / (double)clip.SampleRate:F2}\t{w.Word}"));
    Console.WriteLine($"transcript -> {outPath}");
}
