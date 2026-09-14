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
