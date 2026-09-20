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
            CheckWordTimings(modelsRoot, backend);
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
    /// Word timings out of a TTS family, end to end: the ABI has carried
    /// <c>audiocpp_result_word()</c> all along and <c>kokoro_tts</c> now fills it, so what is
    /// being tested is whether measured timings actually cross the boundary — not whether the
    /// accessor compiles.
    ///
    /// <para>
    /// ⚠ NOTHING IN THE BINDING CHANGED FOR THIS, which is the reason to test it here rather
    /// than trust it. <c>AudioCppResult.Words</c> and <c>AudioCppModel.SupportsTimestamps</c>
    /// were already bound and already returned nothing, because no TTS family populated the
    /// field. A binding that is correct and a binding that is inert look identical from inside
    /// the binding; only a family that reports something tells them apart.
    /// </para>
    ///
    /// <para>
    /// ⚠ AND IT REPORTS RATHER THAN SKIPS when the engine has no timings to give, for the same
    /// reason CheckSuppliedPhonemes does: the pinned engine may predate the change, and a test
    /// that skipped itself into silence on the default pin would be indistinguishable from one
    /// that passes. Point <c>AUDIOCPP_KOKORO_SPEC</c> at <c>model_specs/kokoro_tts.json</c> to
    /// see the capability declared as well — a published package's embedded contract predates it,
    /// so the timings arrive while <c>SupportsTimestamps</c> still reads false.
    /// </para>
    /// </summary>
    private static void CheckWordTimings(string modelsRoot, BackendConfig backend)
    {
        var modelPath = Path.Combine(modelsRoot, "Kokoro-82M-GGUF", "kokoro-82m-q8_0.gguf");
        if (!File.Exists(modelPath))
        {
            Console.WriteLine("word timings: no kokoro package; skipping");
            _skipped++;
            return;
        }

        var specOverride = Environment.GetEnvironmentVariable("AUDIOCPP_KOKORO_SPEC");
        var config = new ModelConfig("kokoro_tts")
        {
            ModelSpecOverride = string.IsNullOrEmpty(specOverride) ? null : specOverride,
        };

        using var registry = AudioCppRegistry.Create();
        AudioCppModel model;
        try
        {
            model = registry.Load(modelPath, config);
        }
        catch (AudioCppException exception)
        {
            Console.WriteLine($"word timings: skip (load: {exception.Detail})");
            _skipped++;
            return;
        }

        using var owned = model;
        Console.WriteLine($"word timings: model advertises timestamps: {(model.SupportsTimestamps ? "yes" : "no")}");

        AudioCppSession session;
        try
        {
            session = model.CreateSession("tts", "offline", backend);
        }
        catch (AudioCppException exception)
        {
            Console.WriteLine($"word timings: skip (session: {exception.Detail})");
            _skipped++;
            return;
        }

        using var ownedSession = session;

        (float[] Audio, IReadOnlyList<WordTimestamp> Words, int Rate) Speak(
            IReadOnlyList<string> phonemes, float rate = 1.0f)
        {
            using var request = new AudioCppRequest();
            request.SetText("placeholder", "en-us");
            request.SetVoiceId("af_heart");
            request.SetSpeakingRate(rate);
            request.SetOptionArray("phonemes", phonemes);
            using var result = session.Run(request);
            var audio = result.Audio;
            return (audio?.Samples ?? [], result.Words, audio?.SampleRate ?? 0);
        }

        // ⚠ THE EXPECTED COUNT IS DERIVED FROM THE STREAM, NOT WRITTEN DOWN. The first version of
        // this hard-coded it and hard-coded it wrong — six groups counted as five — so the test
        // failed against an engine that was right. A literal here is a second place to make the
        // mistake the assertion exists to catch.
        const string Utterance = "ðə hˈɑɹbɚ wʌz kwˈaɪət ðɪs mˈɔɹnɪŋ";
        var expectedGroups = Utterance.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var (audio, words, sampleRate) = Speak([Utterance]);
        if (words.Count == 0)
        {
            // Not a failure: an engine older than the change reports nothing, and this test is in
            // the tree before the pin moves. Said out loud so it cannot be mistaken for a pass.
            Console.WriteLine("word timings: engine reported none — pinned engine predates "
                              + "kokoro_tts word timings; nothing to assert");
            _skipped++;
            return;
        }

        var failuresBefore = _failures;
        _ran++;

        Check(audio.Length > 0 && sampleRate > 0, "supplied phonemes produced no audio to time against");
        // One entry per spoken group. A standalone punctuation mark is not a group, which is why
        // this stream deliberately carries none.
        Check(words.Count == expectedGroups,
              $"expected one timing per phoneme group ({expectedGroups}), got {words.Count}");

        var duration = audio.Length / (double)sampleRate;
        long previousEnd = 0;
        foreach (var word in words)
        {
            Check(word.StartSample >= previousEnd,
                  $"\"{word.Word}\" starts at {word.StartSample} before the previous group ended at {previousEnd}");
            Check(word.EndSample > word.StartSample, $"\"{word.Word}\" has no duration");
            Check(word.EndSample <= audio.Length,
                  $"\"{word.Word}\" ends at {word.EndSample}, past the {audio.Length}-sample buffer");
            Check(word.Word.Length > 0, "a timing came back with no label");
            previousEnd = word.EndSample;
        }

        // The last group must reach the end of the utterance. Not exactly: Kokoro's trailing pad
        // token carries real frames and belongs to no word, so a few percent of silence after the
        // last group is correct rather than a shortfall.
        var covered = previousEnd / (double)audio.Length;
        Check(covered is > 0.90 and <= 1.0,
              $"the last group ends at {covered:P1} of the buffer, so the timings do not span it");

        // ⚠ SPEED IS THE CASE THAT COULD BE SILENTLY WRONG. The durations are predicted from a
        // graph that is handed the speaking rate, but a rate applied to the AUDIO after prediction
        // would leave the timings describing the 1.0 timeline with nothing to indicate it. Same
        // coverage test at a different rate is what catches that.
        var fast = Speak([Utterance], 1.5f);
        Check(fast.Audio.Length < audio.Length, "a 1.5x rate did not shorten the audio");
        if (fast.Words.Count > 0)
        {
            var fastCovered = fast.Words[^1].EndSample / (double)fast.Audio.Length;
            Check(fastCovered is > 0.90 and <= 1.0,
                  $"at 1.5x the last group ends at {fastCovered:P1} of the buffer: the timings "
                  + "describe a different timeline than the audio");
        }

        // A caller's chunking is merged into one buffer, so the timings must be merged into one
        // timeline too — the second entry's groups offset by the first entry's audio, not restarted.
        // The same utterance, split — so the group count must be identical to the single-entry
        // run and the boundary must be invisible in the timeline.
        var twoEntries = Speak(["ðə hˈɑɹbɚ wʌz kwˈaɪət", "ðɪs mˈɔɹnɪŋ"]);
        Check(twoEntries.Words.Count == expectedGroups,
              $"a 2-entry list reported {twoEntries.Words.Count} timings for {expectedGroups} groups");
        long across = 0;
        var monotonic = true;
        foreach (var word in twoEntries.Words)
        {
            if (word.StartSample < across) monotonic = false;
            across = word.EndSample;
        }
        Check(monotonic, "timings restarted at the entry boundary instead of continuing");
        Check(across <= twoEntries.Audio.Length,
              $"the merged timeline ends at {across}, past its {twoEntries.Audio.Length}-sample buffer");

        if (_failures == failuresBefore) Console.WriteLine("word timings: ok");
    }

    /// <summary>
    /// A list-valued request option, end to end: the values have to reach the family and change
    /// what it synthesizes, not merely be accepted by the setter.
    ///
    /// <para>
    /// Kokoro's <c>phonemes</c> is the only family that reads one today. The option set a model
    /// declares is embedded in its GGUF at conversion time and a published package cannot be
    /// edited in place, so the engine drops the key from its VALIDATION COPY when the contract
    /// predates the option — a shipped package takes phonemes it never declared. This therefore
    /// runs either way and only REPORTS which side it ran, because skipping on the declaration
    /// would skip on the package everyone actually has. Point <c>AUDIOCPP_KOKORO_SPEC</c> at
    /// <c>model_specs/kokoro_tts.json</c> to run the declared side.
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
            _skipped++;
            return;
        }

        // NULL, not "": the ABI reads an empty override as a path, and rejects it as a path
        // that does not exist. Only a missing setting means "use the package's own spec".
        var specOverride = Environment.GetEnvironmentVariable("AUDIOCPP_KOKORO_SPEC");
        var config = new ModelConfig("kokoro_tts")
        {
            ModelSpecOverride = string.IsNullOrEmpty(specOverride) ? null : specOverride,
        };

        using var registry = AudioCppRegistry.Create();
        AudioCppModel model;
        try
        {
            model = registry.Load(modelPath, config);
        }
        catch (AudioCppException exception)
        {
            // Same rule as RunCase: a family this build did not link is a build-time choice,
            // so it is a skip. Without this the package being on disk while kokoro_tts is not
            // in the composite throws out of Main -- a stack trace and no summary line.
            Console.WriteLine($"option arrays: skip (load: {exception.Detail})");
            _skipped++;
            return;
        }

        using var owned = model;
        var declared = model.GetOptions(AudioCppOptionScope.Request).Any(o => o.Name == "phonemes");
        Console.WriteLine($"option arrays: package declares a 'phonemes' request option: {(declared ? "yes" : "no")}");

        AudioCppSession session;
        try
        {
            session = model.CreateSession("tts", "offline", backend);
        }
        catch (AudioCppException exception)
        {
            Console.WriteLine($"option arrays: skip (session: {exception.Detail})");
            _skipped++;
            return;
        }

        using var ownedSession = session;
        var failuresBefore = _failures;

        float[] Speak(string text, IReadOnlyList<string>? phonemes)
        {
            using var request = new AudioCppRequest();
            request.SetText(text, "en-us");
            request.SetVoiceId("af_heart");
            if (phonemes is not null) request.SetOptionArray("phonemes", phonemes);
            try
            {
                using var result = session.Run(request);
                return result.Audio?.Samples ?? [];
            }
            catch (AudioCppException error)
            {
                // Every call here is one the engine is supposed to serve, so a refusal is a
                // failure of THIS test rather than of the run -- reported, with what the
                // engine said, instead of thrown out of Main as a stack trace.
                Check(false, $"synthesis was refused: {error.Message}");
                return [];
            }
        }

        // Runs a request that is expected to be refused, handing back the native detail so the
        // caller can assert WHY -- a rejection for the wrong reason is not the one being tested.
        bool Refuses(string text, IReadOnlyList<string> phonemes, out string message)
        {
            using var request = new AudioCppRequest();
            request.SetText(text, "en-us");
            request.SetVoiceId("af_heart");
            request.SetOptionArray("phonemes", phonemes);
            try
            {
                using var result = session.Run(request);
                message = "";
                return false;
            }
            catch (AudioCppException error)
            {
                message = error.Message;
                return true;
            }
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
        Check(Refuses("I like it", ["a\u1da6 l\u02c8a\u1da6k \u026at"], out var offGlide),  // canonical IPA off-glide
              "a supplied stream with an out-of-vocabulary symbol was accepted and silently degraded");
        // Separately, because a refusal for some OTHER reason would otherwise be reported as
        // acceptance -- sending the reader to look for a validation that is in fact present.
        Check(offGlide.Contains("\u1da6", StringComparison.Ordinal),
              $"the off-glide was refused, but not for being out of vocabulary: {offGlide}");

        // ...and the leniency our own G2P depends on must survive that strictness.
        Check(Speak("button", null).Length > 0,
              "the built-in G2P stopped tolerating a symbol its own output contains");

        // ⚠ SET-BUT-EMPTY IS NOT UNSET. The ABI's option map cannot tell "no phonemes" from
        // "an empty list of phonemes" by the key's presence alone, so a caller whose own G2P
        // stage produced nothing would otherwise have the SOURCE TEXT read aloud with no error
        // -- the one failure mode a caller cannot detect from the audio. Both shapes are refused:
        // the whole list empty, and one empty entry among good ones.
        Check(Refuses("x", [], out var emptyList), "an empty phoneme list was accepted");
        Check(emptyList.Contains("no entries", StringComparison.OrdinalIgnoreCase),
              $"an empty list was refused but not for being empty: {emptyList}");
        Check(Refuses("placeholder", ["həlˈO", ""], out var emptyEntry),
              "an empty entry beside a good one was accepted");
        Check(emptyEntry.Contains("entry 1", StringComparison.Ordinal),
              $"an empty entry was refused without naming which one: {emptyEntry}");

        // A long list must name the offending ENTRY, not just the symbol: "bad symbol R" in a
        // 200-entry list tells the caller nothing about where to look. The engine sizes every
        // entry in prepare(), so this is reported before any audio is rendered.
        var manyChunks = Enumerable.Repeat("həlˈO", 200).Append("R").ToArray();
        Check(Refuses("placeholder", manyChunks, out var lateEntry),
              "an out-of-vocabulary symbol in the last entry was accepted");
        Check(lateEntry.Contains("entry 200", StringComparison.Ordinal),
              $"a bad entry in a long list was refused without naming which one: {lateEntry}");

        // An undeclared list key must be rejected by the same contract that rejects an undeclared
        // scalar one, rather than silently ignored.
        using (var request = new AudioCppRequest())
        {
            request.SetText("x", "en-us");
            request.SetVoiceId("af_heart");
            request.SetOptionArray("not_a_real_option", ["v"]);
            var rejected = "";
            try { using var result = session.Run(request); }
            catch (AudioCppException error) { rejected = error.Message; }
            Check(rejected.Length > 0, "an undeclared list option was accepted");
            Check(rejected.Contains("not_a_real_option", StringComparison.Ordinal),
                  $"an undeclared list option was refused without naming it: {rejected}");
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
