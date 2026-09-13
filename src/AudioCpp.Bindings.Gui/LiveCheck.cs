using Avalonia.Threading;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Drives the microphone path inside a real window, on the real dispatcher.
/// </summary>
/// <remarks>
/// The headless --smoke path cannot verify live updates: every callback from the
/// capture pump hops through Dispatcher.UIThread, and with no dispatcher loop
/// running those posts are queued and never executed. A headless run therefore
/// reports a level of zero and an empty live transcript while the underlying
/// audio is flowing perfectly — which looks exactly like a broken meter.
///
/// This runs the same sequence with a window up, so the posts actually execute.
/// </remarks>
internal static class LiveCheck
{
    /// <summary>
    /// Drive the preview player. Like the meter, the playhead is advanced by a
    /// dispatcher timer, so a headless run would show it frozen at zero while
    /// audio plays perfectly.
    /// </summary>
    private static async Task<int> PlayCheckAsync(MainWindowViewModel viewModel)
    {
        var failures = 0;

        if (!viewModel.HasPreviewAudio)
        {
            Console.Error.WriteLine("nothing to preview; load a clip first");
            return 1;
        }

        await viewModel.PlayCommand.ExecuteAsync();
        Console.WriteLine($"playing: {viewModel.PlayLabel}");

        await Task.Delay(700);
        var moved = viewModel.PlayProgress;
        Console.WriteLine($"progress after 0.7s: {moved:F4}  ({viewModel.PlayStatus})");
        if (moved <= 0) { Console.Error.WriteLine("playhead never advanced"); failures++; }

        await viewModel.PlayCommand.ExecuteAsync();   // pause
        await Task.Delay(300);
        var held = viewModel.PlayProgress;
        Console.WriteLine($"progress after pausing 0.3s: {held:F4}");
        if (Math.Abs(held - moved) > 0.02) { Console.Error.WriteLine("paused playback kept advancing"); failures++; }

        viewModel.SeekTo(0.75);
        await Task.Delay(50);
        Console.WriteLine($"after seeking to 0.75: {viewModel.PlayProgress:F4}");
        if (Math.Abs(viewModel.PlayProgress - 0.75) > 0.05)
        {
            Console.Error.WriteLine("seek did not land");
            failures++;
        }

        // Selecting a result row should seek the preview to it -- the whole
        // reason the rows carry sample offsets.
        var positioned = viewModel.Rows.FirstOrDefault(r => r.StartSample > 0);
        if (positioned.StartSample > 0)
        {
            viewModel.SelectedRow = positioned;
            await Task.Delay(50);
            var expected = positioned.StartSample / (double)16000;
            Console.WriteLine($"row seek: wanted {expected:F2}s, playhead at {viewModel.PlayStatus}");
            if (viewModel.PlayProgress <= 0)
            {
                Console.Error.WriteLine("selecting a row did not move the playhead");
                failures++;
            }
        }
        else
        {
            Console.WriteLine("row seek: no positioned rows in this result, skipped");
        }

        await viewModel.StopAudioAsync();
        return failures;
    }

    /// <summary>
    /// Load, run, unload, and load again — checking the GPU actually gives the
    /// memory back rather than the flag merely flipping.
    /// </summary>
    private static async Task<int> LifecycleCheckAsync(MainWindowViewModel viewModel)
    {
        var failures = 0;

        Console.WriteLine($"before load:  {GpuUsedMb()} MB used");
        await viewModel.LoadCommand.ExecuteAsync();
        if (!viewModel.IsLoaded) { Console.Error.WriteLine("load failed"); return 1; }

        await viewModel.RunCommand.ExecuteAsync();
        var afterRun = GpuUsedMb();
        Console.WriteLine($"after run:    {afterRun} MB used  ({viewModel.LoadedModelState}, "
                          + $"{viewModel.LoadedModelWeights})");

        await viewModel.UnloadCommand.ExecuteAsync();
        await Task.Delay(1500);   // the driver frees asynchronously
        var afterUnload = GpuUsedMb();
        Console.WriteLine($"after unload: {afterUnload} MB used  ({viewModel.LoadedModelState})");

        if (viewModel.IsLoaded) { Console.Error.WriteLine("still loaded after unload"); failures++; }
        if (viewModel.Options.Count > 0) { Console.Error.WriteLine("options survived unload"); failures++; }

        // The point of an unload is the memory. Anything less than most of it
        // coming back means sessions or the registry are still holding on.
        if (afterRun > 0 && afterUnload > 0 && afterUnload > afterRun * 0.7)
        {
            Console.Error.WriteLine($"  unload freed little: {afterRun} -> {afterUnload} MB");
            failures++;
        }

        await viewModel.LoadCommand.ExecuteAsync();
        Console.WriteLine($"reloaded:     {viewModel.IsLoaded} ({viewModel.LoadedModelName})");
        if (!viewModel.IsLoaded) { Console.Error.WriteLine("could not load again after unload"); failures++; }

        // Switching models must free the old one rather than stacking them. The
        // comparison has to be run-to-run, not load-to-load: loading is lazy and
        // weights only reach the GPU when a session runs, so comparing two
        // freshly-loaded models compares two baselines and proves nothing.
        var second = Environment.GetEnvironmentVariable("LIFECYCLE_SECOND_MODEL");
        if (second is { Length: > 0 } && File.Exists(second))
        {
            await viewModel.RunCommand.ExecuteAsync();
            var parakeetResident = GpuUsedMb();

            viewModel.ModelPath = second;
            viewModel.FamilyHint = "";
            await viewModel.LoadCommand.ExecuteAsync();
            viewModel.Task = "tts";
            await viewModel.RunCommand.ExecuteAsync();
            await Task.Delay(1500);
            var afterSwitch = GpuUsedMb();

            Console.WriteLine($"switched to:  {viewModel.LoadedModelName} and ran, {afterSwitch} MB "
                              + $"(the previous model resident was {parakeetResident} MB)");

            // The second model is an order of magnitude smaller, so if the first
            // were still resident the total could not fall.
            if (afterSwitch >= parakeetResident)
            {
                Console.Error.WriteLine($"  switching did not free the previous model: "
                                        + $"{parakeetResident} -> {afterSwitch} MB");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>GPU memory in use, or 0 where nvidia-smi is not available.</summary>
    private static int GpuUsedMb()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
            });
            if (process is null) return 0;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return int.TryParse(output.Split('\n')[0].Trim(), out var mb) ? mb : 0;
        }
        catch (Exception) { return 0; }
    }

    internal static void Arm(MainWindowViewModel viewModel, string[] args, int seconds)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var failures = 0;
            try
            {
                foreach (var assignment in args.Where(a => a.Contains('=')))
                {
                    var parts = assignment.Split('=', 2);
                    switch (parts[0])
                    {
                        case "model": viewModel.ModelPath = parts[1]; break;
                        case "family": viewModel.FamilyHint = parts[1]; break;
                        case "backend": viewModel.Backend = parts[1]; break;
                        case "capture":
                            viewModel.CaptureDevice = viewModel.CaptureDevices
                                .FirstOrDefault(d => d.Index == int.Parse(parts[1]));
                            break;
                        case "audio": viewModel.AudioPath = parts[1]; break;
                    }
                }

                if (args.Contains("--shot"))
                {
                    // Screenshot each task so the control layout can be compared.
                    // One task, held open: capturing a rotating set meant guessing
                    // when each frame was up, and the guesses kept landing wrong.
                    // Load first when a model is given: the option editors are
                    // built from what the model declares, so an unloaded window
                    // shows none of them.
                    if (viewModel.ModelPath.Length > 0)
                    {
                        await viewModel.LoadCommand.ExecuteAsync();
                        Console.WriteLine($"load: {viewModel.Status}");
                        Console.WriteLine($"request options: {viewModel.RequestOptions.Count}, "
                                          + $"session: {viewModel.SessionOptions.Count}");
                        foreach (var option in viewModel.RequestOptions.Concat(viewModel.SessionOptions))
                        {
                            Console.WriteLine($"  {option.Editor,-7} {option.Name} "
                                              + $"(type '{option.Type}', default '{option.DefaultDisplay}')");
                        }
                    }

                    // Round-trip check: a typed control must land on the same
                    // DeclaredOption the run path reads, and returning it to the
                    // model's default must clear it so the request stays minimal.
                    if (viewModel.SessionOptions.FirstOrDefault(o => o.IsChoice) is { } choice)
                    {
                        var other = choice.Choices.FirstOrDefault(c => c != choice.DefaultDisplay);
                        choice.Choice = other;
                        var shared = viewModel.Options.First(o => o.Name == choice.Name);
                        Console.WriteLine($"roundtrip: set {choice.Name}={other} -> "
                                          + $"Value='{shared.Value}' (shared instance: {ReferenceEquals(shared, choice)})");
                        choice.Choice = choice.DefaultDisplay;
                        Console.WriteLine($"roundtrip: back to default -> Value='{shared.Value}' (expect empty)");
                    }
                    if (viewModel.RequestOptions.FirstOrDefault(o => o.IsNumber) is { } number)
                    {
                        number.NumberValue = 42;
                        Console.WriteLine($"roundtrip: set {number.Name}=42 -> Value='{number.Value}'");
                        number.Value = "";
                    }

                    var task = args.FirstOrDefault(a => a.StartsWith("task="))?["task=".Length..] ?? "asr";
                    viewModel.Task = task;
                    await Task.Delay(400);
                    Console.WriteLine($"{task}: text={viewModel.ShowText} voice={viewModel.ShowVoice} "
                                      + $"audio={viewModel.ShowAudioInput} asrExtras={viewModel.ShowAsrAudioControls} "
                                      + $"split={viewModel.ShowTextChunking}");
                    await Task.Delay(60000);   // held for a screenshot; killed externally
                    return;
                }

                if (args.Contains("--arena-check"))
                {
                    var arena = viewModel.Arena;
                    arena.Left.ModelPath = viewModel.ModelPath;
                    arena.Left.FamilyHint = viewModel.FamilyHint;
                    arena.Left.Backend = "cuda";
                    arena.Right.ModelPath = viewModel.ModelPath;
                    arena.Right.FamilyHint = viewModel.FamilyHint;
                    arena.Right.Backend = "cpu";          // same weights, different backend
                    arena.AudioPath = viewModel.AudioPath;
                    arena.Task = "asr";

                    await arena.LoadLeftCommand.ExecuteAsync();
                    await arena.LoadRightCommand.ExecuteAsync();
                    Console.WriteLine($"A: {arena.Left.Status}");
                    Console.WriteLine($"B: {arena.Right.Status}");

                    await arena.CompareCommand.ExecuteAsync();
                    Console.WriteLine($"A timing: {arena.Left.Timing}  ({arena.Left.WordCount} words)");
                    Console.WriteLine($"B timing: {arena.Right.Timing}  ({arena.Right.WordCount} words)");
                    Console.WriteLine($"summary: {arena.Summary}");
                    Console.WriteLine($"diff pieces: {arena.Diff.Count}");

                    var onlyA = arena.Diff.Count(d => d.Side == "A only");
                    var onlyB = arena.Diff.Count(d => d.Side == "B only");
                    Console.WriteLine($"disagreements: A only {onlyA}, B only {onlyB}");

                    if (arena.Diff.Count == 0) { Console.Error.WriteLine("no diff produced"); failures++; }
                    if (arena.Left.WordCount == 0 || arena.Right.WordCount == 0)
                    { Console.Error.WriteLine("a side produced nothing"); failures++; }

                    Console.WriteLine(failures == 0 ? "arena OK" : $"arena: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                if (args.Contains("--lifecycle-check"))
                {
                    failures += await LifecycleCheckAsync(viewModel);
                    Console.WriteLine(failures == 0 ? "lifecycle OK" : $"lifecycle: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                if (args.Contains("--play-check"))
                {
                    // Load and run once so there is audio to preview: the input
                    // clip is captured during a run.
                    await viewModel.LoadCommand.ExecuteAsync();
                    Console.WriteLine($"load: {viewModel.Status}");
                    await viewModel.RunCommand.ExecuteAsync();
                    Console.WriteLine($"run: {viewModel.Status}");

                    failures += await PlayCheckAsync(viewModel);
                    Console.WriteLine(failures == 0 ? "play check OK" : $"play check: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                await viewModel.LoadCommand.ExecuteAsync();
                Console.WriteLine($"load: {viewModel.Status}");
                if (!viewModel.IsLoaded) { Console.Error.WriteLine("model did not load"); failures++; }

                if (failures == 0)
                {
                    await viewModel.RecordCommand.ExecuteAsync();
                    Console.WriteLine($"recording: {viewModel.IsRecording}");

                    var peak = 0f;
                    var liveSeen = "";
                    for (var i = 0; i < seconds * 4; i++)
                    {
                        await Task.Delay(250);
                        peak = Math.Max(peak, viewModel.InputLevel);
                        if (viewModel.Transcript.Length > liveSeen.Length) liveSeen = viewModel.Transcript;
                    }

                    await viewModel.RecordCommand.ExecuteAsync();

                    Console.WriteLine($"peak level while recording: {peak:F4}");
                    Console.WriteLine($"live transcript seen: {(liveSeen.Length > 0 ? "yes" : "no")}"
                                      + (liveSeen.Length > 0 ? $" ({liveSeen.Length} chars)" : ""));
                    Console.WriteLine($"final: {viewModel.Transcript}");
                    Console.WriteLine($"status: {viewModel.Status}");

                    // The meter is the point of this check: a live path that only
                    // produces text at the end would pass the headless smoke.
                    if (peak <= 0) { Console.Error.WriteLine("meter never moved"); failures++; }
                    if (liveSeen.Length == 0) { Console.Error.WriteLine("no live transcript"); failures++; }
                    if (viewModel.IsRecording) { Console.Error.WriteLine("still recording"); failures++; }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"live check threw: {exception}");
                failures++;
            }

            Console.WriteLine(failures == 0 ? "live check OK" : $"live check: {failures} failure(s)");
            Environment.Exit(failures == 0 ? 0 : 1);
        });
    }
}
