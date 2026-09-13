using System.Diagnostics;
using AudioCpp;
using AudioCpp.PathTest;

// Vernacula's VAD + Parakeet pipeline, driven entirely through audio.cpp's C ABI.
//
// The cost of running parakeet over a long recording in one shot is that
// full_context builds a single encoder graph across the whole clip, and silence
// is decoded at the same price as speech. Vernacula avoids both: Silero marks
// the speech, adjacent segments are merged into groups that are long enough to
// give the decoder context but short enough to stay cheap, and only those
// windows reach ASR.
//
//   args: <vad-model-dir> <parakeet-gguf> <wav> [min-span-s] [max-span-s] [threads]

var family     = Environment.GetEnvironmentVariable("ASR_FAMILY") ?? "parakeet_tdt";
var vadModel   = args[0];
var asrModel   = args[1];
var audioPath  = args[2];
var minSpan    = args.Length > 3 ? double.Parse(args[3]) : 15.0;
var maxSpan    = args.Length > 4 ? double.Parse(args[4]) : 28.0;
var threads    = args.Length > 5 ? int.Parse(args[5]) : Environment.ProcessorCount;
// Extra parakeet session options as k=v, e.g. perf_mode=flash_attention
var extra = new List<KeyValuePair<string, string>>();
foreach (var arg in args.Skip(6))
{
    var kv = arg.Split('=', 2);
    if (kv.Length != 2)
        throw new ArgumentException($"session option must be key=value, got '{arg}'");
    extra.Add(new($"{family}.{kv[0]}", kv[1]));
}
if (extra.Count > 0) Console.WriteLine("session opts: " + string.Join(", ", extra.Select(k => $"{k.Key}={k.Value}")));

// Backend for the ASR session. Defaults to cpu so existing numbers stay
// reproducible; set ASR_BACKEND=cuda (or vulkan, metal) to measure a GPU.
var backendName = Environment.GetEnvironmentVariable("ASR_BACKEND") ?? "cpu";

var clip = Wav.Read(audioPath);
if (clip.Channels != 1) throw new InvalidOperationException($"expected mono, got {clip.Channels}ch");
var rate = clip.SampleRate;
var total = clip.Samples.Length / (double)rate;
Console.WriteLine($"backend={backendName}");
Console.WriteLine($"audio {total:F1}s @ {rate} Hz   min-span={minSpan}s max-span={maxSpan}s threads={threads}");

var wall = Stopwatch.StartNew();
TimeSpan swVad = default, swAsr = default;
using var registry = AudioCppRegistry.Create();

// ── 1. VAD ────────────────────────────────────────────────────────────────
// Load and session creation are setup, not inference. They cost seconds for a GGUF
// plus a backend context, do not scale with batch size, and would therefore flatten
// exactly the ratios this harness exists to measure. Timed and reported apart.
var swVadLoad = Stopwatch.StartNew();
List<(long Start, long End)> segments;
using (var vad = registry.Load(vadModel, "silero_vad"))
using (var vadSession = vad.CreateSession("vad", "offline", new BackendConfig(
    Environment.GetEnvironmentVariable("VAD_BACKEND") ?? "cpu", 0, threads)))
{
    swVadLoad.Stop();
    var swVadRun = Stopwatch.StartNew();
    using (var req = new AudioCppRequest())
    {
        req.SetAudio(clip.Samples, rate, 1);
        using var result = vadSession.Run(req);
        segments = result.Segments.Select(s => (s.StartSample, s.EndSample)).ToList();
    }
    swVadRun.Stop();
    swVad = swVadRun.Elapsed;
}

var speech = segments.Sum(s => (s.End - s.Start)) / (double)rate;
Console.WriteLine($"VAD: {segments.Count} segment(s), {speech:F1}s speech "
                + $"({speech / total * 100:F0}% of clip) in {swVad.TotalSeconds:F1}s "
                + $"(load {swVadLoad.Elapsed.TotalSeconds:F1}s)");

// ── 2. Group, Vernacula-style ─────────────────────────────────────────────
// MergeShortGroups absorbs neighbours until a group clears the minimum span.
// The maximum is this harness's own guard: Vernacula only enforces a floor,
// but parakeet's full_context cost grows with window length, so a single long
// unbroken segment still needs a ceiling.
var groups = new List<(long Start, long End)>();
if (segments.Count > 0)
{
    var (gs, ge) = segments[0];
    for (var i = 1; i < segments.Count; i++)
    {
        var span = (ge - gs) / (double)rate;
        var wouldBe = (segments[i].End - gs) / (double)rate;
        if (span >= minSpan || wouldBe > maxSpan)
        {
            AddGroup(groups, gs, ge, rate, maxSpan);
            (gs, ge) = segments[i];
        }
        else ge = segments[i].End;
    }
    if ((ge - gs) / (double)rate < minSpan && groups.Count > 0
        && (ge - groups[^1].Start) / (double)rate <= maxSpan)
        groups[^1] = (groups[^1].Start, ge);
    else AddGroup(groups, gs, ge, rate, maxSpan);
}

// Enforce the ceiling on the group itself, not only when merging. A single unbroken
// VAD segment longer than maxSpan would otherwise reach ASR whole, which is the
// full_context blow-up the ceiling exists to prevent.
static void AddGroup(List<(long Start, long End)> groups, long start, long end,
                     int rate, double maxSpan)
{
    var limit = (long)(maxSpan * rate);
    for (var s = start; s < end; s += limit)
        groups.Add((s, Math.Min(s + limit, end)));
}

var spans = groups.Select(g => (g.End - g.Start) / (double)rate).ToList();
if (Environment.GetEnvironmentVariable("DUMP_SPANS") == "1")
{
    // Encoded frames at 80ms each; tinyBLAS fills a column block at ~72.
    foreach (var g in groups)
        Console.WriteLine($"SPAN\t{(g.End - g.Start) / (double)rate:F3}");
    return;
}
if (spans.Count == 0)
{
    // Silence, a music-only clip, a wrong VAD_BACKEND, or a model that loads but
    // detects nothing. Report it rather than throwing out of Min().
    Console.WriteLine("groups: 0 — VAD found no speech, nothing to transcribe");
    return;
}
Console.WriteLine($"groups: {groups.Count}  span min/mean/max = "
                + $"{spans.Min():F1}/{spans.Average():F1}/{spans.Max():F1}s  "
                + $"decoded audio {spans.Sum():F1}s");

// ── 3. ASR per group, one session reused ──────────────────────────────────
// Creating the session once and reusing it across every group is the whole
// reason to embed rather than shell out per clip.
var pieces = new List<string>();
var words = new List<(string Word, double Start, double End)>();

var swAsrLoad = Stopwatch.StartNew();
using (var asr = registry.Load(asrModel, new ModelConfig(family)))
using (var asrSession = asr.CreateSession("asr", "offline", new BackendConfig(backendName, 0, threads), extra.Count > 0 ? extra : null))
{
    swAsrLoad.Stop();
    var swAsrRun = Stopwatch.StartNew();
    // Length-sort before decoding, restoring chronological order afterwards.
    //
    // MEASURED AND IT DOES NOT HELP -- kept because the negative result is worth
    // being able to reproduce. The reasoning was that the encoder graph cache only
    // reuses within kMaxGraphOversizeRatio (1.10), so chronological order misses it
    // constantly. But the cache wants the graph to be both >= and <= 1.10x the
    // request, so ascending order rebuilds on every size step instead of avoiding
    // rebuilds: 40.1s sorted against 32.7s chronological on a 600s clip. Vernacula
    // sorts to minimise padding waste inside a batch, which is a different problem.
    var order = Environment.GetEnvironmentVariable("SORT_BY_LENGTH") == "1"
        ? groups.Select((g, i) => (g, i)).OrderBy(x => x.g.End - x.g.Start).ToList()
        : groups.Select((g, i) => (g, i)).ToList();
    Console.WriteLine($"decode order: {(Environment.GetEnvironmentVariable("SORT_BY_LENGTH") == "1" ? "length-sorted" : "chronological")}");
    var byIndex = new (string Text, List<(string, double, double)> W)[groups.Count];

    foreach (var (grp, origIdx) in order)
    {
        var (start, end) = grp;
        // Silero implementations commonly pad the final analysis window, so an end
        // index can land past the buffer. Clamp rather than fail after paying for VAD.
        var clampedEnd = Math.Min(end, clip.Samples.Length);
        var length = (int)Math.Max(0, clampedEnd - start);
        if (length == 0) continue;
        var window = new float[length];
        Array.Copy(clip.Samples, (int)start, window, 0, length);

        using var req = new AudioCppRequest();
        req.SetAudio(window, rate, 1);
        using var result = asrSession.Run(req);

        var text = result.Text?.Text ?? "";
        var ws = result.Words
            .Select(w => (w.Word, (start + w.StartSample) / (double)rate,
                                  (start + w.EndSample) / (double)rate))
            .ToList();
        byIndex[origIdx] = (text.Trim(), ws);
    }

    // Reassemble in chronological order regardless of decode order.
    foreach (var (text, ws) in byIndex)
    {
        if (!string.IsNullOrEmpty(text)) pieces.Add(text);
        foreach (var w in ws) words.Add(w);
    }
    swAsrRun.Stop();
    swAsr = swAsrRun.Elapsed;
}
wall.Stop();

var transcript = string.Join(" ", pieces);
Console.WriteLine();
Console.WriteLine($"ASR   {swAsr.TotalSeconds,7:F1}s over {groups.Count} group(s) "
                + $"(load {swAsrLoad.Elapsed.TotalSeconds:F1}s, excluded)");
Console.WriteLine($"VAD   {swVad.TotalSeconds,7:F1}s");
Console.WriteLine($"TOTAL {wall.Elapsed.TotalSeconds,7:F1}s   "
                + $"{total / wall.Elapsed.TotalSeconds:F2}x realtime");
Console.WriteLine($"words={words.Count} chars={transcript.Length}");
if (words.Count > 0)
    Console.WriteLine($"first word @ {words[0].Start:F2}s, last @ {words[^1].End:F2}s");
Console.WriteLine("head: " + transcript[..Math.Min(110, transcript.Length)]);

var outPath = Path.Combine(Path.GetTempPath(), "vad_pipeline_transcript.txt");
File.WriteAllText(outPath, transcript);
Console.WriteLine($"transcript -> {outPath}");
