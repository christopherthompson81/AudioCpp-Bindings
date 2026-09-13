namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Headless exercise of the view model, for checking the sample still drives the ABI
/// without opening a window. Exit codes follow CTest: 0 pass, 1 fail, 77 skip.
/// </summary>
internal static class Smoke
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: --smoke <model-path> [audio.wav] [task] [family-hint] "
                              + "[vad.gguf] [name=value ...]");
            Console.WriteLine("no model given; skipping");
            return 77;
        }

        var outPath = "";
        var viewModel = new MainWindowViewModel
        {
            ModelPath = args[0],
            AudioPath = args.Length > 1 ? args[1] : "",
            Task = args.Length > 2 ? args[2] : "asr",
            FamilyHint = args.Length > 3 ? args[3] : "",
            // A VAD model turns ASR into the segmented pipeline, which is what a long
            // recording needs -- handing the whole clip to the model at once builds one
            // encoder graph over all of it.
            VadModelPath = args.Length > 4 && !args[4].Contains('=') ? args[4] : "",
        };

        if (!File.Exists(viewModel.ModelPath) && !Directory.Exists(viewModel.ModelPath))
        {
            Console.WriteLine($"no model at {viewModel.ModelPath}; skipping");
            return 77;
        }

        Console.WriteLine(viewModel.AbiVersion);
        Console.WriteLine(viewModel.CatalogCount);
        foreach (var entry in viewModel.CatalogEntries.Take(4))
        {
            Console.WriteLine($"  {entry.StateText,-10} {entry.Title}");
        }

        await viewModel.LoadCommand.ExecuteAsync();
        Console.WriteLine(viewModel.Status);
        if (!viewModel.IsLoaded) return 1;

        Console.WriteLine(viewModel.ModelSummary);
        Console.WriteLine($"declared options: {viewModel.Options.Count}");
        foreach (var option in viewModel.Options.Take(6))
        {
            Console.WriteLine($"  {option.Scope,-8} {option.Name,-34} {option.Type,-12} " +
                              $"default='{option.Default}' {option.Range}");
        }

        // Trailing name=value arguments set declared options, which is what the
        // Options grid does in the window. A GUI cannot be driven headlessly, so
        // without this the smoke path cannot reach any non-default configuration --
        // including offline_mode, which a long clip needs.
        foreach (var assignment in args.Skip(4).Where(a => a.Contains('=')))
        {
            var parts = assignment.Split('=', 2);
            if (parts.Length != 2)
            {
                Console.Error.WriteLine($"option must be name=value, got '{assignment}'");
                return 1;
            }
            // A few view-model knobs are not declared options -- they configure the
            // session rather than the request -- but the window exposes them, so the
            // smoke path needs them too.
            switch (parts[0])
            {
                case "backend": viewModel.Backend = parts[1]; continue;
                case "threads": viewModel.Threads = int.Parse(parts[1]); continue;
                case "chunking": viewModel.UseBuiltInChunking = bool.Parse(parts[1]); continue;
                case "vad_assets": viewModel.VadAssetPath = parts[1]; continue;
                case "split": viewModel.SplitLongText = bool.Parse(parts[1]); continue;
                case "budget": viewModel.ChunkBudget = int.Parse(parts[1]); continue;
                case "text": viewModel.Text = parts[1]; continue;
                case "describe": viewModel.VoiceDescription = parts[1]; continue;
                case "language": viewModel.SpeechLanguage = parts[1]; continue;
                // Somewhere to put generated audio. The window has Save WAV;
                // without this the headless path can report that a run produced
                // 12 seconds of audio but give no way to listen to it.
                case "out": outPath = parts[1]; continue;
                case "voice_ref": viewModel.VoiceAudioPath = parts[1]; continue;
                case "voice_text": viewModel.VoiceTranscript = parts[1]; continue;
                case "capture":
                    viewModel.CaptureDevice = viewModel.CaptureDevices
                        .FirstOrDefault(d => d.Index == int.Parse(parts[1]));
                    continue;
                case "chunk_seconds":
                    viewModel.ChunkSeconds = double.Parse(
                        parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    continue;
            }

            var option = viewModel.Options.FirstOrDefault(o => o.Name == parts[0]);
            if (option is null)
            {
                Console.Error.WriteLine($"model declares no option '{parts[0]}'");
                return 1;
            }
            option.Value = parts[1];
            Console.WriteLine($"set {option.Scope} {option.Name}={parts[1]}");
        }

        // --record exercises the microphone path headlessly: start, let audio
        // flow, stop, and report what came back. It cannot catch a cross-thread
        // bug -- only a real window can -- but it proves the pump and the engine
        // agree.
        if (args.Contains("--record"))
        {
            Console.WriteLine($"inputs: {viewModel.CaptureDevices.Count}  {viewModel.LiveStatus}");
            var seconds = 6;
            await viewModel.RecordCommand.ExecuteAsync();
            Console.WriteLine($"recording: {viewModel.IsRecording}  {viewModel.Status}");
            if (!viewModel.IsRecording) { Console.Error.WriteLine("did not start"); return 1; }

            var peak = 0f;
            for (var i = 0; i < seconds * 4; i++)
            {
                await System.Threading.Tasks.Task.Delay(250);
                peak = Math.Max(peak, viewModel.InputLevel);
            }
            await viewModel.RecordCommand.ExecuteAsync();
            Console.WriteLine($"stopped: {viewModel.Status}");
            Console.WriteLine($"peak level: {peak:F4}");
            Console.WriteLine($"transcript: {viewModel.Transcript}");
            return viewModel.IsRecording ? 1 : 0;
        }

        await viewModel.RunCommand.ExecuteAsync();
        Console.WriteLine(viewModel.Status);

        if (viewModel.Transcript.Length > 0) Console.WriteLine($"transcript: {viewModel.Transcript}");
        if (viewModel.OutputSamples.Length > 0) Console.WriteLine($"audio: {viewModel.OutputSummary}");
        if (viewModel.Segments.Count > 0)
        {
            Console.WriteLine($"segments: {viewModel.Segments.Count}");
            foreach (var segment in viewModel.Segments.Take(4))
            {
                Console.WriteLine($"  #{segment.Index} {segment.Seconds:F2}s  "
                                  + $"{segment.SampleRate} Hz  {segment.Text.Length} chars");
            }
        }
        foreach (var row in viewModel.Rows.Take(6))
        {
            Console.WriteLine($"  {row.Kind,-9} {row.Span,-16} {row.Value}");
        }
        Console.WriteLine($"rows: {viewModel.Rows.Count}");
        if (viewModel.OutputStreams.Count > 0)
        {
            Console.WriteLine($"streams: {viewModel.OutputStreams.Count}");
            foreach (var stream in viewModel.OutputStreams)
                Console.WriteLine($"  {stream.Id}  {stream.Summary}");
        }
        if (viewModel.Artifacts.Count > 0)
        {
            Console.WriteLine($"artifacts: {viewModel.Artifacts.Count}");
            foreach (var artifact in viewModel.Artifacts.Take(3))
                Console.WriteLine($"  {artifact.Id}  {artifact.Summary}");
        }
        Console.WriteLine($"timing: {viewModel.TimingBreakdown}");
        Console.WriteLine($"word timings: {viewModel.HasWordTimings}");
        Console.WriteLine($"supports reference: {viewModel.SupportsVoiceReference}, "
                          + $"reference set: {viewModel.VoiceAudioPath.Length > 0}");
        if (args.Contains("--srt") && viewModel.HasWordTimings)
        {
            var srt = viewModel.SubtitlePreview("srt");
            Console.WriteLine("--- first cues ---");
            Console.WriteLine(string.Join("\n", srt.Split('\n').Take(12)));
        }

        var produced = viewModel.Transcript.Length > 0
                       || viewModel.OutputSamples.Length > 0
                       || viewModel.Rows.Count > 0;
        if (!produced)
        {
            Console.WriteLine("the run produced nothing");
            return 1;
        }

        if (outPath.Length > 0 && viewModel.OutputSamples.Length > 0)
        {
            viewModel.WriteOutput(outPath);
            Console.WriteLine($"wrote {outPath}");
        }

        Console.WriteLine("demo smoke OK");
        return 0;
    }
}
