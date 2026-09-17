using AudioCpp;
using AudioCpp.Native;

namespace AudioCpp.PathTest;

/// <summary>
/// The C# counterpart of tests/capi/path_test.c.
///
/// It re-checks the ABI contract from a garbage-collected runtime, which is the
/// case the C test cannot reach: handles are finalized in an order nobody
/// controls, so an ABI that required a disposal order would be unusable from
/// .NET no matter how carefully a caller wrote their code. The out-of-order and
/// GC sections below are the point of this file.
///
/// Exit codes match CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Program
{
    private const int CTestSkip = 77;
    private static int _failures;

    private static void Check(bool condition, string message)
    {
        if (condition) return;
        Console.Error.WriteLine($"FAIL: {message}");
        _failures++;
    }

    private static int Main(string[] args)
    {
        var modelPath = args.Length > 0 ? args[0] : "assets/framework/models/silero_vad";
        var audioPath = args.Length > 1 ? args[1] : "assets/resources/sample_16k.wav";
        var backendName = args.Length > 2 ? args[2] : "cpu";

        if (!Directory.Exists(modelPath) || !File.Exists(audioPath))
        {
            Console.WriteLine($"missing {modelPath} or {audioPath}; skipping");
            return CTestSkip;
        }

        try
        {
            Console.WriteLine($"abi=0x{AudioCppRegistry.AbiVersion:x} build={AudioCppRegistry.BuildVersion} backend={backendName}");

            var (samples, sampleRate, channels) = Wav.Read(audioPath);
            Console.WriteLine($"audio={audioPath} frames={samples.Length / channels} rate={sampleRate} ch={channels}");

            // Silero is tiny, so this is about exercising the plumbing rather
            // than throughput; two threads keeps the test quick and stable.
            var backend = new BackendConfig(backendName, 0, 2);

            // Handles are declared here and disposed deliberately out of order
            // further down; nothing in between assumes an ordering.
            var registry = AudioCppRegistry.Create();
            Check(registry.Families.Count > 0, "registry advertises no families");
            Check(registry.Families.Contains("silero_vad"), "silero_vad is not in the registry");

            // A failure must arrive as a typed exception carrying the native
            // detail, not as a silent bad handle.
            var unsupported = Assert.Throws<AudioCppException>(() => registry.Load(modelPath, "no_such_family"));
            Check(unsupported.Status == AudioCppStatus.UnsupportedFamily,
                  $"unknown family reported {unsupported.Status}, expected UnsupportedFamily");
            Check(unsupported.Detail.Length > 0, "failing call carried no native detail");

            var model = registry.Load(modelPath, "silero_vad");
            Console.WriteLine($"family={model.Family}");
            Check(model.Family == "silero_vad", $"unexpected family '{model.Family}'");
            Check(model.Supports("vad", "offline"), "model does not advertise vad/offline");
            Check(!model.Supports("tts", "offline"), "vad model claims to do tts");
            Check(!model.Supports("nonsense", "offline"), "an unknown task should read as unsupported");

            foreach (var scope in Enum.GetValues<AudioCppOptionScope>())
            {
                var declared = model.GetOptions(scope);
                Console.WriteLine($"options[{scope}]={declared.Count}");
                foreach (var option in declared)
                {
                    Check(option.Name.Length > 0, $"{scope} option has no name");
                    Console.WriteLine($"  {option.Name} ({option.ValueName}) default='{option.DefaultValue}' " +
                                      $"range=[{option.MinValue},{option.MaxValue}] required={option.Required}");
                }
            }

            var session = model.CreateSession("vad", "offline", backend);
            Check(session.Family.Length > 0, "session reports no family");

            var first = RunOnce(session, samples, sampleRate, channels);
            var second = RunOnce(session, samples, sampleRate, channels);
            Console.WriteLine($"segments={first.Count}");
            Check(first.Count > 0, "no speech found in a speech recording");
            Check(first.SequenceEqual(second), "reusing the session changed the result");

            // A GC with live handles must not disturb anything. Finalizers for
            // any garbage produced above run here, in whatever order the runtime
            // picks.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var afterCollect = RunOnce(session, samples, sampleRate, channels);
            Check(first.SequenceEqual(afterCollect), "a garbage collection changed the session's result");

            RunStreaming(model, samples, sampleRate, channels, backend);

            // The ABI holds parents alive, so this order is legal. Without that
            // the session would now be pointing at a freed model.
            model.Dispose();
            registry.Dispose();
            var afterParentsDisposed = RunOnce(session, samples, sampleRate, channels);
            Check(first.SequenceEqual(afterParentsDisposed),
                  "the session stopped agreeing with itself after its model and registry were disposed");

            // Double dispose must be harmless: SafeHandle guarantees it, but the
            // combination with the native side is what is being pinned here.
            model.Dispose();
            session.Dispose();
            session.Dispose();

            // LAST, and inside its own try. It needs no model, while everything above is the
            // point of this file -- and both of its failure shapes (Assert.Throws when nothing
            // throws, EntryPointNotFoundException against an engine older than ABI minor 2)
            // would otherwise escape to the outer catch and take the dispose-ordering coverage
            // down with them.
            try
            {
                CheckOptionArrayContract();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: option arrays: {exception.GetType().Name}: {exception.Message}");
                _failures++;
            }
        }
        catch (DllNotFoundException exception)
        {
            Console.WriteLine($"libaudiocpp not found ({exception.Message}); run ./scripts/build-engine.sh. Skipping.");
            return CTestSkip;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: unexpected {exception.GetType().Name}: {exception.Message}");
            _failures++;
        }

        if (_failures > 0)
        {
            Console.Error.WriteLine($"{_failures} check(s) failed");
            return 1;
        }
        Console.WriteLine("c# path test OK");
        return 0;
    }

    private static List<(long Start, long End)> RunOnce(
        AudioCppSession session, float[] samples, int sampleRate, int channels)
    {
        using var request = new AudioCppRequest();
        request.SetAudio(samples, sampleRate, channels);
        using var result = session.Run(request);

        Check(result.Audio is null, "a vad result claimed to carry audio");
        Check(result.Text is null, "a vad result claimed to carry text");

        var spans = new List<(long, long)>();
        long previousEnd = -1;
        foreach (var segment in result.Segments)
        {
            Check(segment.StartSample >= 0 && segment.EndSample >= segment.StartSample,
                  $"segment span [{segment.StartSample},{segment.EndSample}] is malformed");
            Check(segment.StartSample >= previousEnd, "segments are out of order");
            Check(segment.Text is not null, "segment text is null; absent should be empty");
            previousEnd = segment.EndSample;
            spans.Add((segment.StartSample, segment.EndSample));
        }
        return spans;
    }

    /// <summary>
    /// The argument contract of <see cref="AudioCppRequest.SetOptionArray"/>, which is a list
    /// transport over an ABI whose option map is string-to-string.
    ///
    /// <para>
    /// Model-free on purpose: whether the VALUES reach a family is that family's test, but
    /// whether a malformed call is rejected cleanly belongs with the rest of the ABI contract —
    /// and these are the calls a garbage-collected caller is most likely to get wrong, since it
    /// has to pin an array of pointers into managed memory to make them at all.
    /// </para>
    /// </summary>
    private static void CheckOptionArrayContract()
    {
        using var request = new AudioCppRequest();

        // The ordinary case, and an empty list -- which sets an EMPTY list rather than
        // being a no-op, and so must be accepted rather than rejected as "nothing to set".
        request.SetOptionArray("phonemes", ["one", "two", "three"]);
        request.SetOptionArray("phonemes", []);

        // Replace, not append: two calls leave the second list, so a caller correcting a
        // mistake gets what it asked for rather than both attempts concatenated. Not
        // observable from here without a family that reads it, so this asserts only that the
        // repeat is accepted; AudioCpp.ModelTest checks the value that survives.
        request.SetOptionArray("phonemes", ["first"]);
        request.SetOptionArray("phonemes", ["second"]);

        // Unicode has to survive the marshalling: these are phoneme strings in practice, and
        // every interesting one is non-ASCII.
        request.SetOptionArray("phonemes", ["\u00f0\u0259 h\u02c8\u0251\u0279b\u025a", "\u02c8\u00e6fr\u0131k\u0259"]);

        // A value with an embedded NUL must be refused rather than silently truncated: the
        // native side builds a std::string from the pointer, so everything past the first NUL
        // would be dropped and the list accepted -- the same "set but quietly degraded" shape
        // the engine refuses an empty entry for.
        Assert.Throws<ArgumentException>(() => request.SetOptionArray("phonemes", ["h\u0259l\u02c8O\0wrld"]));

        // A null element must be refused rather than marshalled as a null pointer, and refused
        // BEFORE anything is written, so the option cannot be left half-assigned.
        Assert.Throws<ArgumentException>(() => request.SetOptionArray("phonemes", ["ok", null!]));
        Assert.Throws<ArgumentNullException>(() => request.SetOptionArray("phonemes", null!));
        Assert.Throws<ArgumentNullException>(() => request.SetOptionArray(null!, ["x"]));

        Console.WriteLine("option arrays: contract ok");
    }

    private static void RunStreaming(
        AudioCppModel model, float[] samples, int sampleRate, int channels, BackendConfig backend)
    {
        if (!model.Supports("vad", "streaming")) return;

        using var stream = model.CreateSession("vad", "streaming", backend);
        var policy = stream.GetStreamPolicy();
        Check(policy.Input == AudioCppStreamInputKind.AudioChunks, "vad stream wants no audio chunks");
        Check(policy.PreferredChunkSamples > 0, $"stream policy gave chunk {policy.PreferredChunkSamples}");

        var chunk = (int)Math.Max(policy.PreferredChunkSamples, 1);
        var activity = 0;
        stream.StartStream();
        for (var offset = 0; offset + chunk * channels <= samples.Length; offset += chunk * channels)
        {
            using var pushed = stream.PushStream(
                samples.AsSpan(offset, chunk * channels), sampleRate, channels, offset / channels);
            if (pushed is null) continue;

            foreach (var voiceActivity in pushed.VoiceActivity)
            {
                Check(voiceActivity.Probability is >= 0f and <= 1f,
                      $"probability {voiceActivity.Probability} is outside [0,1]");
                activity++;
            }
            // The event owns its result view; reading through it must work and
            // must not free anything.
            _ = pushed.Result.Segments.Count;
        }

        using var final = stream.FinishStream();
        Console.WriteLine($"stream: chunk={chunk} events={activity} segments={final.Segments.Count}");
        Check(activity > 0 || final.Segments.Count > 0,
              "streaming produced neither events nor segments over speech audio");

        // Each mode must refuse the other's entry point.
        using var request = new AudioCppRequest();
        var wrongMode = Assert.Throws<AudioCppException>(() => stream.Run(request));
        Check(wrongMode.Status == AudioCppStatus.NotAvailable,
              $"streaming session accepted an offline run ({wrongMode.Status})");
        stream.ResetStream();
    }
}

internal static class Assert
{
    internal static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name}, but nothing was thrown");
    }
}
