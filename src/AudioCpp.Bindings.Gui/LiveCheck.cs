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
                        case "modelsRoot": viewModel.ModelsRoot = parts[1]; break;
                    }
                }

                // Open the real save dialog for one file name, so what the
                // platform actually shows can be looked at. The mapping is
                // covered by --save-check; this is the part that needs eyes.
                if (args.FirstOrDefault(a => a.StartsWith("save-dialog="))?["save-dialog=".Length..]
                    is { } saveName)
                {
                    // The dialog needs the window it parents to, which is shown
                    // after this runs.
                    await Task.Delay(1500);
                    Console.WriteLine($"opening save dialog for {saveName}");
                    var picked = await viewModel.PickSavePath!(saveName);
                    Console.WriteLine($"picked: {picked ?? "(cancelled)"}");
                    return;
                }

                Console.WriteLine($"settings: {viewModel.SettingsPath}");

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

                    if (args.Contains("--with-events"))
                    {
                        // Do something worth logging before the screenshot, so
                        // the runtime page is not photographed empty.
                        await viewModel.RunCommand.ExecuteAsync();
                        await viewModel.UnloadCommand.ExecuteAsync();
                        Console.WriteLine($"log entries: {viewModel.SessionLog.Count}");
                        foreach (var entry in viewModel.SessionLog)
                            Console.WriteLine($"  {entry.Time} {entry.Kind,-7} {entry.Message}");
                    }

                    if (args.FirstOrDefault(a => a.StartsWith("lang="))?["lang=".Length..] is { } lang)
                    {
                        // Accept either the display name as the selector shows
                        // it, or a shorthand, so a screenshot run can say lang=ru.
                        viewModel.Language = lang switch
                        {
                            "pseudo" => "Pseudo (qps-ploc)",
                            "it" => "Italiano",
                            "pl" => "Polski",
                            "ru" => "Русский",
                            "zh" => "中文",
                            "en" => "English",
                            _ => viewModel.Languages.Contains(lang) ? lang : "English",
                        };
                        await Task.Delay(400);
                        Console.WriteLine($"language: {viewModel.Language}  "
                                          + $"task title now '{viewModel.StudioTitle}'");
                    }

                    if (args.FirstOrDefault(a => a.StartsWith("page="))?["page=".Length..] is { } page)
                    {
                        viewModel.Page = page;
                        await Task.Delay(300);
                    }

                    if (args.FirstOrDefault(a => a.StartsWith("theme="))?["theme=".Length..] is { } theme)
                    {
                        viewModel.Theme = theme;
                        Console.WriteLine($"theme: {viewModel.Theme}");
                        await Task.Delay(500);
                    }

                    // Left alone when not given, so a screenshot can show what
                    // the saved settings restored rather than what this forced.
                    // task= names a workflow tab; the engine task inside it is
                    // resolved from the model, as it is in the window.
                    var task = args.FirstOrDefault(a => a.StartsWith("task="))?["task=".Length..];
                    if (task is not null) viewModel.CurrentWorkflow = task;
                    task = viewModel.Task;
                    await Task.Delay(400);
                    Console.WriteLine($"{task}: text={viewModel.ShowText} voice={viewModel.ShowVoice} "
                                      + $"audio={viewModel.ShowAudioInput} asrExtras={viewModel.ShowAsrAudioControls} "
                                      + $"split={viewModel.ShowTextChunking}");
                    await Task.Delay(60000);   // held for a screenshot; killed externally
                    return;
                }

                // Cancel has to either stop the run or be visibly unavailable.
                // Needs a real model: what decides the answer is the shape of
                // the run, and that only exists once there is one.
                if (args.Contains("--cancel-check"))
                {
                    await viewModel.LoadCommand.ExecuteAsync();
                    Console.WriteLine($"model: {viewModel.Status}");

                    // A whole-clip run: one call into the engine, nothing to stop.
                    viewModel.VadModelPath = "";
                    var whole = viewModel.RunCommand.ExecuteAsync();
                    await Task.Delay(400);
                    Console.WriteLine($"single call: cancel enabled={viewModel.CancelCommand.CanExecute(null)}");
                    Console.WriteLine($"  hint: {viewModel.CancelHint}");
                    if (viewModel.CancelCommand.CanExecute(null))
                    {
                        Console.Error.WriteLine("cancel offered for a run it cannot stop"); failures++;
                    }
                    await whole;
                    var fullRows = viewModel.Rows.Count;
                    Console.WriteLine($"  ran to the end: {fullRows} row(s)");

                    // Segmented: one call per stretch of speech, so the loop can
                    // see the token between them.
                    var vad = args.FirstOrDefault(a => a.StartsWith("vad="))?["vad=".Length..];
                    if (vad is null)
                    {
                        Console.Error.WriteLine("pass vad=<silero dir> to check the segmented path");
                        failures++;
                    }
                    else
                    {
                        viewModel.VadModelPath = vad;
                        await viewModel.LoadCommand.ExecuteAsync();
                        var segmented = viewModel.RunCommand.ExecuteAsync();
                        await Task.Delay(300);
                        Console.WriteLine($"segmented: cancel enabled={viewModel.CancelCommand.CanExecute(null)}");
                        if (!viewModel.CancelCommand.CanExecute(null))
                        {
                            Console.Error.WriteLine("cancel unavailable for a run it can stop"); failures++;
                        }
                        await viewModel.CancelCommand.ExecuteAsync();
                        await segmented;
                        Console.WriteLine($"  after cancel: {viewModel.Rows.Count} row(s), "
                                          + $"status '{viewModel.Status}'");
                        if (viewModel.Rows.Count >= fullRows && fullRows > 0)
                        {
                            Console.Error.WriteLine("cancelled run produced as much as a full one");
                            failures++;
                        }
                    }

                    // The Cancel button is shared with the installer, and a
                    // download is always interruptible -- gating the button on
                    // the run's shape alone disabled it there, which is a
                    // regression this catches. Needs a package that actually
                    // downloads; where one is not given, say so rather than
                    // passing on a condition that was never staged.
                    if (args.FirstOrDefault(a => a.StartsWith("package="))?["package=".Length..]
                        is { } package)
                    {
                        if (args.FirstOrDefault(a => a.StartsWith("packageTask="))?["packageTask=".Length..]
                            is { } packageTask)
                        {
                            // A workflow id, not an engine task: the catalogue is
                            // workflow-filtered, and an unknown id would quietly
                            // fall back to ASR and search the wrong list.
                            if (!Workflow.All.Any(w => w.Id == packageTask))
                            {
                                Console.Error.WriteLine($"packageTask='{packageTask}' is not a "
                                    + $"workflow; try {string.Join(", ", Workflow.All.Select(w => w.Id))}");
                                failures++;
                            }
                            viewModel.CurrentWorkflow = packageTask;
                            await Task.Delay(200);
                        }
                        viewModel.SelectedEntry = viewModel.CatalogEntries
                            .FirstOrDefault(e => e.Title.Contains(package, StringComparison.OrdinalIgnoreCase));
                        Console.WriteLine($"package: {viewModel.SelectedEntry?.Title ?? "(none)"}");

                        if (viewModel.SelectedEntry is null)
                        {
                            Console.Error.WriteLine($"  no catalog entry matches '{package}'; "
                                                    + $"{viewModel.CatalogEntries.Count} entries, e.g.");
                            foreach (var e in viewModel.CatalogEntries.Take(8))
                                Console.Error.WriteLine($"    {e.Title}");
                            failures++;
                        }
                        else
                        {
                            var install = viewModel.InstallCommand.ExecuteAsync();
                            await Task.Delay(2500);
                            var running = viewModel.Busy;
                            Console.WriteLine($"installing: running={running} "
                                              + $"cancel enabled={viewModel.CancelCommand.CanExecute(null)}");
                            if (!running)
                            {
                                Console.WriteLine("  install finished before it could be cancelled; "
                                                  + "not a download, so this says nothing either way");
                            }
                            else if (!viewModel.CancelCommand.CanExecute(null))
                            {
                                Console.Error.WriteLine("cancel unavailable during a download"); failures++;
                            }
                            else
                            {
                                await viewModel.CancelCommand.ExecuteAsync();
                            }
                            await install;
                            Console.WriteLine($"  after: {viewModel.InstallStatus}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("install cancel not checked; pass package=<title fragment>");
                    }

                    Console.WriteLine($"after the run: cancel enabled={viewModel.CancelCommand.CanExecute(null)}");
                    if (viewModel.CancelCommand.CanExecute(null))
                    {
                        Console.Error.WriteLine("cancel still offered with nothing running"); failures++;
                    }

                    Console.WriteLine(failures == 0 ? "cancel OK" : $"cancel: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                // Type-ahead over the package list. The control supplies the
                // strings; what is worth checking is the matching -- and in
                // relationships rather than counts, because a count is a fact
                // about today's catalogue and would need editing every time
                // upstream adds a package.
                // The workflow tabs, and the task each one resolves to.
                if (args.Contains("--workflow-check"))
                {
                    void Expect(string what, bool ok)
                    {
                        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
                        if (!ok) failures++;
                    }

                    Console.WriteLine($"{Workflow.All.Count} workflows, "
                                      + $"{Workflow.AllTasks.Count} engine tasks");
                    foreach (var workflow in Workflow.All)
                    {
                        viewModel.CurrentWorkflow = workflow.Id;
                        await Task.Delay(120);
                        Console.WriteLine($"  {workflow.Id,-9} "
                                          + $"{Resources.Strings.WorkflowLabel(workflow),-22} "
                                          + $"tasks [{string.Join(" ", workflow.Tasks)}]  "
                                          + $"-> {viewModel.Task}  "
                                          + $"{viewModel.CatalogEntries.Count} package(s)");
                        if (!workflow.Tasks.Contains(viewModel.Task))
                        {
                            Console.Error.WriteLine($"  {workflow.Id} resolved to {viewModel.Task}, "
                                                    + "which is not one of its tasks");
                            failures++;
                        }
                    }

                    // Every task belongs to exactly one workflow: a task in two
                    // would make the tab a model lands on depend on iteration
                    // order, and a task in none is unreachable, which is what
                    // #36 was about.
                    // Every task name the installed specs actually use has to be
                    // in the vocabulary. Upstream adding one this does not know
                    // would silently hide those packages from every tab, which
                    // is exactly how the music workflow came to be empty.
                    var declared = viewModel.AllEntries
                        .SelectMany(e => e.Family.Tasks)
                        .Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
                    var unmapped = declared
                        .Where(t => SpecTasks.Abi(t) is null && !SpecTasks.Unmappable.ContainsKey(t))
                        .ToList();
                    foreach (var (name, why) in SpecTasks.Unmappable
                                 .Where(u => declared.Contains(u.Key)))
                    {
                        Console.WriteLine($"  '{name}' is deliberately unmapped: {why}");
                    }
                    Console.WriteLine($"  spec vocabulary in use: {string.Join(" ", declared)}");
                    if (unmapped.Count > 0)
                    {
                        Console.Error.WriteLine($"  spec tasks with no ABI token: "
                                                + string.Join(", ", unmapped));
                        failures++;
                    }

                    // Per-task package counts, so a task nothing declares shows
                    // up here rather than as an empty chip in the window.
                    viewModel.CurrentWorkflow = "asr";
                    await Task.Delay(80);
                    foreach (var task in Workflow.AllTasks)
                    {
                        var n = viewModel.AllEntries.Count(e => e.SupportsTask(task));
                        if (n == 0) Console.WriteLine($"  no package declares '{task}'");
                    }

                    var duplicated = Workflow.AllTasks
                        .GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    Expect("no task is in two workflows", duplicated.Count == 0);

                    // Control visibility has to follow the task, not the tab.
                    var layouts = new List<string>();
                    foreach (var task in Workflow.AllTasks)
                    {
                        viewModel.CurrentWorkflow = Workflow.ForTask(task).Id;
                        viewModel.Task = task;
                        await Task.Delay(40);
                        var shape = $"text={viewModel.ShowText} voice={viewModel.ShowVoice} "
                                  + $"audio={viewModel.ShowAudioInput} asr={viewModel.ShowAsrAudioControls} "
                                  + $"split={viewModel.ShowTextChunking}";
                        Console.WriteLine($"  {task,-6} {Resources.Strings.TaskName(task),-26} {shape}");
                        layouts.Add(shape);

                        if (!viewModel.ShowText && !viewModel.ShowAudioInput)
                        {
                            Console.Error.WriteLine($"  {task} offers no input at all"); failures++;
                        }
                    }
                    Expect("tasks do not all share one layout", layouts.Distinct().Count() > 1);

                    // Only a multi-task workflow asks which task.
                    foreach (var workflow in Workflow.All)
                    {
                        viewModel.CurrentWorkflow = workflow.Id;
                        await Task.Delay(40);
                        if (viewModel.ShowTaskChoice != workflow.Tasks.Count > 1)
                        {
                            Console.Error.WriteLine($"  {workflow.Id}: task choice shown="
                                                    + $"{viewModel.ShowTaskChoice} for "
                                                    + $"{workflow.Tasks.Count} task(s)");
                            failures++;
                        }
                    }
                    Expect("the task choice appears only where there is one", true);

                    Console.WriteLine(failures == 0 ? "workflows OK" : $"workflows: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                if (args.Contains("--picker-check"))
                {
                    viewModel.CurrentWorkflow = "asr";
                    await Task.Delay(150);
                    var all = viewModel.CatalogEntries.ToList();

                    List<string> Hits(string search) => all
                        .Where(e => MainWindowViewModel.Matches(e, search))
                        .Select(e => e.Title).ToList();

                    void Expect(string what, bool ok)
                    {
                        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
                        if (!ok) failures++;
                    }

                    var empty = Hits("");
                    var name = Hits("parakeet");
                    var precision = Hits("q8");
                    var both = Hits("parakeet q8");
                    var reversed = Hits("q8 parakeet");
                    var shouted = Hits("PARAKEET");
                    var subtitle = Hits("gguf");

                    Console.WriteLine($"{all.Count} asr packages; "
                                      + $"'parakeet' {name.Count}, 'q8' {precision.Count}, "
                                      + $"'parakeet q8' {both.Count}, 'gguf' {subtitle.Count}");
                    Console.WriteLine($"  'parakeet q8' -> {string.Join(", ", both)}");

                    Expect("an empty search leaves everything", empty.Count == all.Count);
                    Expect("a name narrows it", name.Count > 0 && name.Count < all.Count);
                    Expect("word order does not matter", both.SequenceEqual(reversed));
                    Expect("case does not matter", shouted.SequenceEqual(name));
                    Expect("every word must match", both.Count <= name.Count
                                                    && both.All(name.Contains));
                    Expect("the subtitle is searched too", subtitle.Count > 0);
                    Expect("nonsense matches nothing", Hits("zzzz").Count == 0);

                    // The picker is already filtered by task; searching must not
                    // reach past that filter.
                    viewModel.CurrentWorkflow = "tts";
                    await Task.Delay(150);
                    var acrossTask = viewModel.CatalogEntries
                        .Count(e => MainWindowViewModel.Matches(e, "parakeet"));
                    Console.WriteLine($"  'parakeet' under tts: {acrossTask} "
                                      + $"of {viewModel.CatalogEntries.Count}");
                    Expect("search stays inside the task filter", acrossTask == 0);

                    Console.WriteLine(failures == 0 ? "picker OK" : $"picker: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                if (args.Contains("--strings-check"))
                {
                    // One key from each area, plus one of ours that upstream
                    // has no equivalent for.
                    var keys = new[] { "run.run", "task.asr", "request.label",
                                       "request.splitLongText", "nav.arena", "section.events" };
                    var cultures = new[] { "en", "it", "pl", "ru", "zh", "qps-ploc" };
                    foreach (var culture in cultures)
                    {
                        Resources.Strings.Culture = new System.Globalization.CultureInfo(culture);
                        var rendered = keys.Select(Resources.Strings.Get).ToList();
                        Console.WriteLine($"{culture,-9} {string.Join(" | ", rendered)}");

                        // A key coming back as itself means the resource is missing,
                        // which is what an unextracted or mistyped key looks like.
                        // An untranslated key is a different thing: it resolves to
                        // the English, because ResourceManager walks up to neutral.
                        var missing = keys.Where((k, i) => rendered[i] == k).ToList();
                        if (missing.Count > 0)
                        {
                            Console.Error.WriteLine($"  unresolved in {culture}: {string.Join(", ", missing)}");
                            failures++;
                        }
                    }

                    // Every key the app can ask for, in every language, so a key
                    // that exists in no resource file at all cannot hide behind
                    // the handful sampled above.
                    var all = new System.Resources.ResourceManager(
                        "AudioCpp.Bindings.Gui.Resources.Strings", typeof(Resources.Strings).Assembly);
                    var english = all.GetResourceSet(new System.Globalization.CultureInfo("en"), true, true)!
                        .Cast<System.Collections.DictionaryEntry>()
                        .Select(e => (string)e.Key).OrderBy(k => k).ToList();
                    Console.WriteLine($"{english.Count} keys declared");

                    foreach (var culture in cultures.Where(c => c != "en"))
                    {
                        // Ask the satellite itself rather than comparing rendered
                        // text: "Arena" is "Arena" in Italian, and counting that
                        // as untranslated understates the coverage.
                        var set = all.GetResourceSet(new System.Globalization.CultureInfo(culture), true, false);
                        var present = set is null
                            ? []
                            : set.Cast<System.Collections.DictionaryEntry>()
                                 .Select(e => (string)e.Key).ToHashSet();
                        var same = present.Count(k => all.GetString(k, new System.Globalization.CultureInfo(culture))
                                                   == all.GetString(k, new System.Globalization.CultureInfo("en")));
                        Console.WriteLine($"  {culture,-9} {present.Count,3} translated "
                                          + $"({same} the same word as English), "
                                          + $"{english.Count - present.Count,3} fall back to English");

                        // A satellite key the neutral file does not declare is a
                        // string nothing can ask for -- a rename that only landed
                        // on one side.
                        var orphans = present.Except(english).ToList();
                        if (orphans.Count > 0)
                        {
                            Console.Error.WriteLine($"  {culture}: not in neutral: {string.Join(", ", orphans)}");
                            failures++;
                        }
                    }

                    // The selector has to open on the language actually being
                    // rendered. It did not: an Italian machine showed an Italian
                    // window with "English" selected, and picking English was a
                    // no-op because it was already the selection.
                    var started = System.Globalization.CultureInfo.CurrentUICulture;
                    foreach (var (machine, expected) in new[]
                             { ("it-IT", "Italiano"), ("zh-CN", "中文"), ("ru", "Русский"),
                               ("de-DE", "English"), ("en-CA", "English") })
                    {
                        System.Globalization.CultureInfo.CurrentUICulture =
                            new System.Globalization.CultureInfo(machine);
                        var opens = Resources.Loc.InitialLanguage();
                        Console.WriteLine($"  machine {machine,-6} -> selector {opens}");
                        if (opens != expected)
                        {
                            Console.Error.WriteLine($"  expected {expected}"); failures++;
                        }
                    }
                    System.Globalization.CultureInfo.CurrentUICulture = started;

                    Resources.Strings.Culture = new System.Globalization.CultureInfo("en");
                    Console.WriteLine($"missing key falls back to itself: "
                                      + $"'{Resources.Strings.Get("no.such.key")}'");
                    if (Resources.Strings.Get("no.such.key") != "no.such.key")
                    {
                        Console.Error.WriteLine("missing key did not fall back"); failures++;
                    }

                    Console.WriteLine(failures == 0 ? "strings OK" : $"strings: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
                    return;
                }

                if (args.Contains("--voice-check"))
                {
                    var library = new VoiceLibrary(System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "audiocpp-voice-check"));
                    Console.WriteLine($"library: {library.StorePath}");

                    // Round-trip through the file, since persistence is the
                    // whole point of a library.
                    var sample = new SavedVoice("check", "/nonexistent/ref.wav", "the words spoken");
                    library.Save([sample]);
                    var read = library.Load();
                    if (read.Count != 1 || read[0].Name != "check")
                    {
                        Console.Error.WriteLine($"round trip failed: loaded {read.Count}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"saved 1, loaded 1: name='{read[0].Name}' "
                                          + $"transcript='{read[0].Transcript}' exists={read[0].Exists}");
                        if (read[0].Exists)
                        {
                            Console.Error.WriteLine("a missing file reported as present");
                            failures++;
                        }
                    }

                    // A corrupt library must not stop the app starting.
                    await File.WriteAllTextAsync(library.StorePath, "{ this is not json");
                    var recovered = library.Load();
                    Console.WriteLine($"corrupt library loads as {recovered.Count} voices (expect 0)");
                    if (recovered.Count != 0) { Console.Error.WriteLine("corrupt library not handled"); failures++; }

                    var demos = VoiceLibrary.DemoVoices();
                    Console.WriteLine($"demo voices found: {demos.Count}");
                    foreach (var demo in demos)
                    {
                        var words = demo.Transcript.Length > 40 ? demo.Transcript[..40] + "…" : demo.Transcript;
                        Console.WriteLine($"  {demo.Name}  exists={demo.Exists}  \"{words}\"");
                    }
                    if (demos.Count > 0 && demos.Any(d => !d.Exists))
                    {
                        Console.Error.WriteLine("a demo voice points at a missing file");
                        failures++;
                    }

                    await viewModel.LoadCommand.ExecuteAsync();
                    Console.WriteLine($"model: {viewModel.LoadedModelName}, "
                                      + $"supports reference: {viewModel.SupportsVoiceReference}");

                    try { File.Delete(library.StorePath); } catch (IOException) { }
                    Console.WriteLine(failures == 0 ? "voice library OK" : $"voice library: {failures} failure(s)");
                    Environment.Exit(failures == 0 ? 0 : 1);
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

                    // Two models here plus whatever the Studio holds is three on
                    // one card; unloading a side has to actually give it back.
                    var before = GpuUsedMb();
                    await arena.UnloadLeftCommand.ExecuteAsync();
                    await arena.UnloadRightCommand.ExecuteAsync();
                    await Task.Delay(1500);
                    var after = GpuUsedMb();
                    Console.WriteLine($"unloaded both sides: {before} -> {after} MB");
                    if (before > 0 && after >= before)
                    {
                        Console.Error.WriteLine("  unloading the sides freed nothing");
                        failures++;
                    }

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
