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
            CheckKokoro(modelsRoot, backend);
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
    /// Loads Kokoro once and runs every check that needs it.
    ///
    /// <para>
    /// ⚠ ONE LOAD, NOT ONE PER CHECK. Each of these used to open its own registry, model and
    /// session over the same 190 MB package, on what is already the slowest test in the suite —
    /// and the second copy tested nothing the first had not. The skip rules are the ones the
    /// individual checks had: a package that is not installed, and a family this build did not
    /// link, are both build-time facts rather than failures.
    /// </para>
    ///
    /// <para>
    /// <c>AUDIOCPP_KOKORO_SPEC</c> points the load at <c>model_specs/kokoro_tts.json</c> instead
    /// of the contract embedded in the package. A published package's contract predates the
    /// <c>phonemes</c> and <c>return_timestamps</c> request options, so the override is how the
    /// DECLARED side of those gets exercised at all. It turns on no capability: kokoro declares
    /// none for timings in either contract.
    /// </para>
    /// </summary>
    private static void CheckKokoro(string modelsRoot, BackendConfig backend)
    {
        var modelPath = Path.Combine(modelsRoot, "Kokoro-82M-GGUF", "kokoro-82m-q8_0.gguf");
        if (!File.Exists(modelPath))
        {
            Console.WriteLine("kokoro: no package; skipping");
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
            Console.WriteLine($"kokoro: skip (load: {exception.Detail})");
            _skipped++;
            return;
        }

        using var owned = model;
        AudioCppSession session;
        try
        {
            session = model.CreateSession("tts", "offline", backend);
        }
        catch (AudioCppException exception)
        {
            Console.WriteLine($"kokoro: skip (session: {exception.Detail})");
            _skipped++;
            return;
        }

        using var ownedSession = session;
        CheckSuppliedPhonemes(model, session);
        CheckWordTimings(model, session, contractDeclaresIt: !string.IsNullOrEmpty(specOverride));
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
    /// ⚠ IT RUNS WHATEVER <c>SupportsTimestamps</c> SAYS, and that flag reads false on BOTH sides
    /// of the override. Review of the engine change deleted <c>word_timestamps</c> from kokoro's
    /// capabilities outright — the current spec is <c>["built_in_voices", "long_form"]</c> — so
    /// there is no declared side for timings to exercise and nothing the override can turn on. The
    /// feature is a <c>return_timestamps</c> REQUEST OPTION, which the override does declare, and
    /// which the engine serves either way.
    ///
    /// <para>
    /// An engine with no timings at all IS a skip — the pinned engine may predate the change —
    /// but only when nothing else could explain it; see the empty-list branch below, which fails
    /// instead whenever the run was told to expect the current contract.
    /// </para>
    /// </para>
    /// </summary>
    private static void CheckWordTimings(AudioCppModel model, AudioCppSession session, bool contractDeclaresIt)
    {
        Console.WriteLine($"word timings: model advertises timestamps: {(model.SupportsTimestamps ? "yes" : "no")}");

        // ⚠ FRAMES, NOT Samples.Length. The spans are frame offsets, and Samples.Length counts
        // INTERLEAVED samples — identical while Kokoro is mono, and half the real length on the
        // first multi-channel family this is ever pointed at, which would read as ~50% coverage
        // and fail for a reason that has nothing to do with the timings.
        (long Frames, float[] Samples, IReadOnlyList<WordTimestamp> Words, int Rate, string? Refusal) Speak(
            IReadOnlyList<string> phonemes, float rate = 1.0f, bool timings = true)
        {
            using var request = new AudioCppRequest();
            request.SetText("placeholder", "en-us");
            request.SetVoiceId("af_heart");
            request.SetSpeakingRate(rate);
            request.SetOptionArray("phonemes", phonemes);
            if (timings) request.SetOption("return_timestamps", "true");
            try
            {
                using var result = session.Run(request);
                var audio = result.Audio;
                return (audio?.Frames ?? 0, audio?.Samples ?? [], result.Words, audio?.SampleRate ?? 0, null);
            }
            catch (AudioCppException error)
            {
                // ⚠ RETURNED, NOT REPORTED, because one refusal is not a failure: an engine older
                // than the option rejects `return_timestamps` as unknown, and that is a build-time
                // choice the caller has to be able to treat as a skip. Everything else here is a
                // request the engine is supposed to serve, so the caller reports those -- with what
                // the engine said -- rather than letting it out of Main as a stack trace.
                return (0, [], [], 0, error.Message);
            }
        }

        // ⚠ THE EXPECTED COUNT IS DERIVED FROM THE STREAM, NOT WRITTEN DOWN. The first version of
        // this hard-coded it and hard-coded it wrong — six groups counted as five — so the test
        // failed against an engine that was right. A literal here is a second place to make the
        // mistake the assertion exists to catch.
        const string Utterance = "ðə hˈɑɹbɚ wʌz kwˈaɪət ðɪs mˈɔɹnɪŋ";
        var expectedGroups = Utterance.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var (frames, samples, words, sampleRate, refusal) = Speak([Utterance]);
        if (refusal is not null)
        {
            // ⚠ THE OLD-ENGINE CASE ARRIVES HERE, NOT AT THE EMPTY LIST BELOW. An engine whose
            // kokoro contract has no `return_timestamps` REFUSES the request outright -- the
            // drop-list that lets a published package's older contract through was added by the
            // same change that added the option -- so it never renders and never reports an empty
            // list. Treating that as a failure would turn a build-time choice red.
            if (refusal.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                && refusal.Contains("return_timestamps", StringComparison.Ordinal))
            {
                Console.WriteLine("word timings: engine rejects return_timestamps — this build "
                                  + "predates the option; nothing to assert");
                _skipped++;
                return;
            }
            Check(false, $"synthesis was refused: {refusal}");
            return;
        }
        if (words.Count == 0)
        {
            // ⚠ "OLD ENGINE" IS ONLY ONE OF THE REASONS THIS CAN BE EMPTY, and the others are
            // defects. append_kokoro_word_timings reports nothing — deliberately, rather than
            // throwing — when its token and duration counts disagree, and again when the durations
            // sum to zero. Blaming the pin for those would print a reassuring sentence over a
            // broken invariant and leave CI green, which is the failure this method's own doc
            // claims to avoid.
            //
            // Nothing observable separates the two: SupportsTimestamps cannot, since it reads
            // false on a current engine with a published package. What CAN separate them is
            // intent — a run pointed at the current contract has asked for an engine that reports
            // timings, so silence there is a failure rather than a configuration.
            if (contractDeclaresIt)
            {
                Check(false, "the engine reported no word timings for a request that set "
                             + "return_timestamps, with AUDIOCPP_KOKORO_SPEC pointing at the "
                             + "current contract: this build is supposed to report them, so an "
                             + "empty list is a defect and not an old pin");
                return;
            }

            // An engine predating the option cannot reach here — it refuses the request instead,
            // which the branch above catches — so the remaining cause is the engine declining to
            // report: append_kokoro_word_timings returns empty on a token/duration mismatch and on
            // durations summing to zero, both defects.
            Console.WriteLine("word timings: the engine accepted return_timestamps and reported "
                              + "none, which it only does on a token/duration mismatch. Re-run with "
                              + "AUDIOCPP_KOKORO_SPEC set to make this a failure; nothing asserted");
            _skipped++;
            return;
        }

        var failuresBefore = _failures;
        _ran++;

        Check(frames > 0 && sampleRate > 0, "supplied phonemes produced no audio to time against");
        // One entry per spoken group. A standalone punctuation mark is not a group, which is why
        // this stream deliberately carries none.
        Check(words.Count == expectedGroups,
              $"expected one timing per phoneme group ({expectedGroups}), got {words.Count}");

        // ⚠ ONE SET OF RULES, APPLIED TO EVERY RUN. Written inline for the single-entry case and
        // summarised for the merged one, the merged case quietly got weaker checks: tracking only
        // the running end lets a zero-length or inverted span slide past, because the next
        // comparison against a moved-backwards cursor passes trivially.
        void CheckTimeline(string label, IReadOnlyList<WordTimestamp> timings, long totalFrames)
        {
            long previousEnd = 0;
            foreach (var word in timings)
            {
                Check(word.StartSample >= previousEnd,
                      $"{label}: \"{word.Word}\" starts at {word.StartSample} before the previous group ended at {previousEnd}");
                Check(word.EndSample > word.StartSample, $"{label}: \"{word.Word}\" has no duration");
                Check(word.EndSample <= totalFrames,
                      $"{label}: \"{word.Word}\" ends at {word.EndSample}, past the {totalFrames}-frame buffer");
                Check(word.Word.Length > 0, $"{label}: a timing came back with no label");
                previousEnd = word.EndSample;
            }

            // The last group must reach the end of the utterance. Not exactly: Kokoro's trailing
            // pad token carries real frames and belongs to no word, so a few percent of silence
            // after the last group is correct rather than a shortfall.
            if (timings.Count == 0 || totalFrames == 0) return;
            var covered = previousEnd / (double)totalFrames;
            Check(covered is > 0.90 and <= 1.0,
                  $"{label}: the last group ends at {covered:P1} of the buffer, so the timings do not span it");
        }

        CheckTimeline("one entry", words, frames);

        // ⚠ THE OPT-IN IS THE CONTRACT, so the OFF case is worth pinning as much as the ON case.
        // Timings used to be reported unconditionally and advertised through the generic
        // `word_timestamps` capability; review replaced that with a per-request option precisely so
        // a caller cannot receive a phoneme-group alignment while believing it asked for a
        // written-word one. If the default ever drifted back to on, every such caller would start
        // receiving it again silently, which is the failure the redesign exists to prevent.
        var unasked = Speak([Utterance], timings: false);
        // The refusal has to be checked before the assertions, or a refused opt-out request passes
        // the opt-in check vacuously — an empty word list because nothing rendered — while the
        // audio comparison fires and blames a cause that had nothing to do with it.
        if (unasked.Refusal is not null)
        {
            Check(false, $"the opt-out request was refused: {unasked.Refusal}");
        }
        else
        {
            Check(unasked.Words.Count == 0,
                  $"timings came back for a request that did not set return_timestamps "
                  + $"({unasked.Words.Count} of them): the option is supposed to be opt-in");
            // Samples, not the frame count: a change that altered the waveform without altering
            // its length would pass a length comparison, and the message would still claim the
            // audio was untouched. CheckSuppliedPhonemes compares samples for the same question.
            Check(unasked.Samples.SequenceEqual(samples),
                  "asking for timings changed the audio, which it must not");
        }

        // ⚠ SPEED IS THE CASE THAT COULD BE SILENTLY WRONG. The durations are predicted from a
        // graph that is handed the speaking rate, but a rate applied to the AUDIO after prediction
        // would leave the timings describing the 1.0 timeline with nothing to indicate it. Same
        // coverage test at a different rate is what catches that.
        var fast = Speak([Utterance], 1.5f);
        Check(fast.Frames < frames, "a 1.5x rate did not shorten the audio");
        // Asserted, not guarded: the engine has already reported timings once in this run, so a
        // rate change producing none is a result, and an `if` would have swallowed it.
        Check(fast.Words.Count == expectedGroups,
              $"at 1.5x the engine reported {fast.Words.Count} timings for {expectedGroups} groups");
        CheckTimeline("1.5x", fast.Words, fast.Frames);

        // A caller's chunking is merged into one buffer, so the timings must be merged into one
        // timeline too — the second entry's groups offset by the first entry's audio, not restarted.
        // ⚠ SPLIT FROM `Utterance`, NOT RETYPED. A hand-copied second stream is the same trap the
        // derived count above exists to avoid: edit the utterance and this one silently keeps
        // speaking the old text, so the merge assertion fails for a reason that has nothing to do
        // with merging — while the comment still claims it is "the same utterance, split".
        var groups = Utterance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var split = groups.Length / 2;
        var twoEntries = Speak([
            string.Join(' ', groups[..split]),
            string.Join(' ', groups[split..]),
        ]);
        Check(twoEntries.Words.Count == expectedGroups,
              $"a 2-entry list reported {twoEntries.Words.Count} timings for {expectedGroups} groups");
        CheckTimeline("two entries", twoEntries.Words, twoEntries.Frames);
        // And specifically that the SEAM is invisible: the first group of the second entry has to
        // sit after the first entry's audio, not back at zero.
        if (twoEntries.Words.Count == expectedGroups && split < twoEntries.Words.Count)
            Check(twoEntries.Words[split].StartSample > twoEntries.Words[split - 1].StartSample,
                  "timings restarted at the entry boundary instead of continuing across it");

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
    private static void CheckSuppliedPhonemes(AudioCppModel model, AudioCppSession session)
    {
        var declared = model.GetOptions(AudioCppOptionScope.Request).Any(o => o.Name == "phonemes");
        Console.WriteLine($"option arrays: package declares a 'phonemes' request option: {(declared ? "yes" : "no")}");
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
