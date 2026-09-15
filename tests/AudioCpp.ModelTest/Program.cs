using AudioCpp;
using AudioCpp.Native;
using AudioCpp.PathTest;

namespace AudioCpp.ModelTest;

/// <summary>
/// The C# counterpart of tests/capi/model_test.c: real families across task
/// types, so the binding's result accessors are covered by something other than
/// an empty list.
///
/// Prints the same `parity:` lines the C test does, so both languages can be
/// compared against audiocpp_cli with one harness.
///
/// Exit codes match CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Program
{
    private const int CTestSkip = 77;
    private static int _failures;
    private static int _ran;
    private static int _skipped;

    private sealed record Case(
        string Family,
        string Task,
        string RelativeModel,
        string? Text = null,
        string? Language = null,
        string? VoiceId = null,
        string? Seed = null,
        bool ExpectAudio = false,
        bool ExpectText = false,
        bool ExpectTurns = false,
        bool ExpectNamedAudio = false,
        bool UseAlternateAudio = false);

    private static readonly Case[] Cases =
    [
        new("kokoro_tts", "tts", "Kokoro-82M-GGUF/kokoro-82m-q8_0.gguf",
            Text: "The quick brown fox jumps over the lazy dog.", Language: "en-us",
            VoiceId: "af_heart", Seed: "1234", ExpectAudio: true),
        new("citrinet_asr", "asr", "Citrinet-ASR-GGUF/citrinet-asr-q8_0.gguf", ExpectText: true),
        new("parakeet_tdt", "asr", "Parakeet-TDT-0.6B-v3-GGUF/parakeet-tdt-0.6b-v3-q8_0.gguf", ExpectText: true),
        new("sortformer_diar", "diar", "Sortformer-Diar-4spk-v1-GGUF/sortformer-diar-4spk-v1-q8_0.gguf",
            ExpectTurns: true),
        new("bs_roformer", "sep", "BS-RoFormer-ep368-GGUF/bs-roformer-ep368-q8_0.gguf",
            ExpectNamedAudio: true, UseAlternateAudio: true),
    ];

    private static void Check(bool condition, string message)
    {
        if (condition) return;
        Console.Error.WriteLine($"FAIL: {message}");
        _failures++;
    }

    private static int Main(string[] args)
    {
        var modelsRoot = args.Length > 0 ? args[0] : "";
        var audioPath = args.Length > 1 ? args[1] : "assets/resources/sample_16k.wav";
        var backendName = args.Length > 2 ? args[2] : "cpu";
        var alternateAudioPath = args.Length > 3
            ? args[3]
            : "tests/ace_step/assets/complete_source_demucs_8s.wav";
        // Separation dominates this test and scales with threads; thread count
        // does not change results, so default to what the machine has.
        var threads = args.Length > 4 && int.TryParse(args[4], out var parsed) && parsed > 0
            ? parsed
            : Environment.ProcessorCount;

        if (string.IsNullOrEmpty(modelsRoot))
        {
            Console.WriteLine("no model root given; skipping");
            return CTestSkip;
        }
        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine($"cannot read {audioPath}");
            return 1;
        }

        var clip = Wav.Read(audioPath);
        var alternate = File.Exists(alternateAudioPath) ? Wav.Read(alternateAudioPath) : default;

        Console.WriteLine($"models_root={modelsRoot}");
        Console.WriteLine($"audio={audioPath} frames={clip.Samples.Length / clip.Channels} rate={clip.SampleRate}");
        Console.WriteLine($"backend={backendName} threads={threads}");

        var backend = new BackendConfig(backendName, 0, threads);

        try
        {
            foreach (var testCase in Cases)
            {
                RunCase(testCase, modelsRoot, clip, alternate, backend);
            }
            CheckSuppliedPhonemes(modelsRoot, backend);
        }
        catch (DllNotFoundException exception)
        {
            Console.WriteLine($"libaudiocpp not found ({exception.Message}); run ./scripts/build-engine.sh. Skipping.");
            return CTestSkip;
        }

        Console.WriteLine($"\nran={_ran} skipped={_skipped} failures={_failures}");
        if (_failures > 0) return 1;
        if (_ran == 0)
        {
            Console.WriteLine("no model was available; skipping");
            return CTestSkip;
        }
        Console.WriteLine("c# model test OK");
        return 0;
    }

    /// <summary>
    /// A list-valued request option, end to end: the values have to reach the family and change
    /// what it synthesizes, not merely be accepted by the setter.
    ///
    /// <para>
    /// Kokoro's <c>phonemes</c> is the only family that reads one today. The option set a model
    /// declares is embedded in its GGUF at conversion time, so a package built before the option
    /// existed will not accept it — hence the declaration probe and the skip, rather than a
    /// failure that only means "your model is older than this test". Point
    /// <c>AUDIOCPP_KOKORO_SPEC</c> at a spec that declares it to exercise this against such a
    /// package.
    /// </para>
    ///
    /// <para>
    /// Emits no <c>parity:</c> lines on purpose: those are diffed against the C test, which does
    /// not run this.
    /// </para>
    /// </summary>
    private static void CheckSuppliedPhonemes(string modelsRoot, BackendConfig backend)
    {
        var modelPath = Path.Combine(modelsRoot, "Kokoro-82M-GGUF", "kokoro-82m-q8_0.gguf");
        if (!File.Exists(modelPath))
        {
            Console.WriteLine("option arrays: no kokoro package; skipping");
            return;
        }

        var specOverride = Environment.GetEnvironmentVariable("AUDIOCPP_KOKORO_SPEC") ?? "";
        var config = new ModelConfig("kokoro_tts") { ModelSpecOverride = specOverride };

        using var registry = AudioCppRegistry.Create();
        using var model = registry.Load(modelPath, config);
        if (!model.GetOptions(AudioCppOptionScope.Request).Any(o => o.Name == "phonemes"))
        {
            Console.WriteLine("option arrays: this kokoro package does not declare 'phonemes'; skipping "
                              + "(set AUDIOCPP_KOKORO_SPEC to a spec that does)");
            return;
        }

        using var session = model.CreateSession("tts", "offline", backend);
        var failuresBefore = _failures;

        float[] Speak(string text, IReadOnlyList<string>? phonemes)
        {
            using var request = new AudioCppRequest();
            request.SetText(text, "en-us");
            request.SetVoiceId("af_heart");
            if (phonemes is not null) request.SetOptionArray("phonemes", phonemes);
            using var result = session.Run(request);
            return result.Audio?.Samples ?? [];
        }

        // Two readings of the SAME text. If the option were ignored, or if the run cache keyed on
        // text alone, these would come back identical — which is the bug this guards.
        var first = Speak("record", ["ɹˈɛkɚd"]);
        var second = Speak("record", ["ɹɪkˈɔɹd"]);
        Check(first.Length > 0 && second.Length > 0, "supplied phonemes produced no audio");
        Check(!first.SequenceEqual(second),
              "two different phoneme lists for the same text produced identical audio");

        // The list is an array, so a caller's own chunking survives: one call over N entries must
        // equal N calls concatenated, or the merge is not the same operation the text path does.
        string[] chunks = ["ðə hˈɑɹbɚ wʌz kwˈaɪət", "lˈɔŋ pˈeɪl bˈændz"];
        var merged = Speak("the harbour was quiet long pale bands", chunks);
        var joined = chunks.SelectMany(c => Speak("x", [c])).ToArray();
        Check(merged.SequenceEqual(joined),
              $"a {chunks.Length}-entry list ({merged.Length} samples) did not match the same "
              + $"entries rendered separately ({joined.Length} samples)");

        // Replace, not append: PathTest asserts the repeat is accepted, this asserts which one won.
        using (var request = new AudioCppRequest())
        {
            request.SetText("record", "en-us");
            request.SetVoiceId("af_heart");
            request.SetOptionArray("phonemes", ["ɹˈɛkɚd"]);
            request.SetOptionArray("phonemes", ["ɹɪkˈɔɹd"]);
            using var result = session.Run(request);
            Check(result.Audio?.Samples.SequenceEqual(second) == true,
                  "setting the option twice did not leave the second list");
        }

        // ⚠ THE ASYMMETRY, which is the whole design and is easy to "simplify" away later.
        // Our own G2P's output is dropped when the vocabulary has no id for it, because nobody
        // downstream can fix it -- "button" phonemizes to a syllabic mark Kokoro has no token
        // for. A CALLER'S stream is refused instead, because they can fix it and silence is
        // actively harmful: canonical IPA writes a diphthong as two symbols where Kokoro writes
        // one, so dropping the off-glide renders "like" as "lack" with no error at all.
        using (var request = new AudioCppRequest())
        {
            request.SetText("I like it", "en-us");
            request.SetVoiceId("af_heart");
            request.SetOptionArray("phonemes", ["a\u1da6 l\u02c8a\u1da6k \u026at"]);   // canonical IPA off-glide
            var refused = false;
            try { using var result = session.Run(request); }
            catch (AudioCppException error)
            {
                refused = error.Message.Contains("\u1da6", StringComparison.Ordinal);
            }
            Check(refused, "a supplied stream with an out-of-vocabulary symbol was accepted and silently degraded");
        }

        // ...and the leniency our own G2P depends on must survive that strictness.
        Check(Speak("button", null).Length > 0,
              "the built-in G2P stopped tolerating a symbol its own output contains");

        // An undeclared list key must be rejected by the same contract that rejects an undeclared
        // scalar one, rather than silently ignored.
        using (var request = new AudioCppRequest())
        {
            request.SetText("x", "en-us");
            request.SetVoiceId("af_heart");
            request.SetOptionArray("not_a_real_option", ["v"]);
            var rejected = false;
            try { using var result = session.Run(request); }
            catch (AudioCppException) { rejected = true; }
            Check(rejected, "an undeclared list option was accepted");
        }

        _ran++;
        // ⚠ Reports what happened, not that it ran. An earlier version printed "ok"
        // unconditionally and said so while two of its own Checks were failing.
        Console.WriteLine(_failures == failuresBefore
            ? "option arrays: supplied phonemes ok"
            : $"option arrays: {_failures - failuresBefore} check(s) FAILED");
    }

    private static void RunCase(
        Case testCase,
        string modelsRoot,
        (float[] Samples, int SampleRate, int Channels) clip,
        (float[] Samples, int SampleRate, int Channels) alternate,
        BackendConfig backend)
    {
        if (testCase.UseAlternateAudio)
        {
            if (alternate.Samples is null)
            {
                Console.WriteLine($"skip {testCase.Family,-16} (needs the alternate audio clip)");
                _skipped++;
                return;
            }
            clip = alternate;
        }

        var modelPath = Path.Combine(modelsRoot, testCase.RelativeModel);
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"skip {testCase.Family,-16} (no {testCase.RelativeModel})");
            _skipped++;
            return;
        }

        Console.WriteLine($"\n=== {testCase.Family} ({testCase.Task}/offline) ===");
        using var registry = AudioCppRegistry.Create();

        AudioCppModel model;
        try
        {
            model = registry.Load(modelPath, testCase.Family);
        }
        catch (AudioCppException exception)
        {
            // A family that this build did not link is a skip, not a failure:
            // the model composite is a build-time choice.
            Console.WriteLine($"skip {testCase.Family,-16} (load: {exception.Detail})");
            _skipped++;
            return;
        }

        using (model)
        {
            Check(model.Family == testCase.Family, $"loaded '{model.Family}', asked for '{testCase.Family}'");
            Check(model.Supports(testCase.Task, "offline"),
                  $"{testCase.Family} does not advertise {testCase.Task}/offline");

            var declared = 0;
            foreach (var scope in Enum.GetValues<AudioCppOptionScope>())
            {
                foreach (var option in model.GetOptions(scope))
                {
                    Check(option.Name.Length > 0, $"{testCase.Family} {scope} option has no name");
                    Console.WriteLine($"  {scope,-8} {option.Name,-34} value={option.ValueName,-14} " +
                                      $"required={option.Required} default='{option.DefaultValue}' " +
                                      $"range=[{option.MinValue},{option.MaxValue}]");
                    declared++;
                }
            }
            Console.WriteLine($"declared_options={declared} speaker_ref={model.SupportsSpeakerReference} " +
                              $"style={model.SupportsStyleCondition} timestamps={model.SupportsTimestamps}");

            using var session = model.CreateSession(testCase.Task, "offline", backend);
            using var request = new AudioCppRequest();

            if (testCase.Text is not null)
            {
                request.SetText(testCase.Text, testCase.Language);
                if (testCase.VoiceId is not null) request.SetVoiceId(testCase.VoiceId);
            }
            else
            {
                request.SetAudio(clip.Samples, clip.SampleRate, clip.Channels);
            }
            if (testCase.Seed is not null) request.SetOption("seed", testCase.Seed);

            using var result = session.Run(request);
            _ran++;

            if (testCase.ExpectAudio)
            {
                var audio = result.Audio;
                Check(audio is not null, $"{testCase.Family} produced no audio");
                if (audio is { } buffer)
                {
                    Check(buffer.Frames > 0, $"{testCase.Family} produced 0 frames");
                    var peak = buffer.Samples.Length == 0 ? 0f : buffer.Samples.Max(Math.Abs);
                    Check(peak > 0.001f, $"{testCase.Family} audio is silent (peak {peak})");
                    Console.WriteLine($"parity:{testCase.Family}:audio_frames={buffer.Frames}");
                    Console.WriteLine($"parity:{testCase.Family}:audio_rate={buffer.SampleRate}");
                    Console.WriteLine($"parity:{testCase.Family}:audio_seconds={buffer.Duration:F3}");
                    Console.WriteLine($"parity:{testCase.Family}:audio_peak={peak:F4}");
                }
            }

            if (testCase.ExpectText)
            {
                var text = result.Text;
                Check(text is not null, $"{testCase.Family} produced no transcript");
                if (text is { } transcript)
                {
                    Check(transcript.Text.Length > 0, $"{testCase.Family} transcript is empty");
                    Console.WriteLine($"parity:{testCase.Family}:text={transcript.Text}");
                }
            }

            if (testCase.ExpectTurns)
            {
                var turns = result.SpeakerTurns;
                Check(turns.Count > 0, $"{testCase.Family} produced no speaker turns");
                foreach (var turn in turns)
                {
                    Check(turn.StartSample >= 0 && turn.EndSample >= turn.StartSample,
                          $"{testCase.Family} turn span [{turn.StartSample},{turn.EndSample}]");
                    Check(turn.SpeakerId is not null, $"{testCase.Family} turn has null speaker id");
                }
                Console.WriteLine($"parity:{testCase.Family}:speaker_turns={turns.Count}");
            }

            if (testCase.ExpectNamedAudio)
            {
                var streams = result.NamedAudio;
                Check(streams.Count > 0, $"{testCase.Family} produced no named streams");
                for (var i = 0; i < streams.Count; i++)
                {
                    var stream = streams[i];
                    Check(stream.Id.Length > 0, $"{testCase.Family} stream {i} has no id");
                    Check(stream.Frames > 0 && stream.SampleRate > 0,
                          $"{testCase.Family} stream '{stream.Id}' is empty");
                    Console.WriteLine(
                        $"parity:{testCase.Family}:stream_{i}={stream.Id}:{stream.Frames}@{stream.SampleRate}");
                }
                Console.WriteLine($"parity:{testCase.Family}:named_audio={streams.Count}");
            }

            if (result.Words.Count > 0)
            {
                foreach (var word in result.Words)
                {
                    Check(word.Word is not null, $"{testCase.Family} word is null");
                    Check(word.StartSample >= 0 && word.EndSample >= word.StartSample,
                          $"{testCase.Family} word span [{word.StartSample},{word.EndSample}]");
                }
                Console.WriteLine($"parity:{testCase.Family}:words={result.Words.Count}");
            }

            if (result.Artifacts.Count > 0)
            {
                Console.WriteLine($"parity:{testCase.Family}:artifacts={result.Artifacts.Count}");
            }
        }
    }
}
