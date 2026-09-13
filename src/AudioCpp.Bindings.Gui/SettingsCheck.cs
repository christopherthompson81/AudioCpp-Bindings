using System.Reflection;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Checks that settings survive a restart, and that nothing quietly stops
/// being saved.
/// </summary>
/// <remarks>
/// The round trip is the easy half. The half worth automating is the
/// bookkeeping: a property added to <see cref="Settings"/> and forgotten in
/// Capture, or renamed and left stale in PersistedProperties, fails silently
/// and looks exactly like the app forgetting a setting.
/// </remarks>
internal static class SettingsCheck
{
    public static int Run()
    {
        var failures = 0;
        var directory = Path.Combine(Path.GetTempPath(),
            "audiocpp-settings-check-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // 1. Every persisted name is a real property, and every Settings
            //    field is one this view model actually captures.
            var properties = typeof(MainWindowViewModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).ToHashSet();
            foreach (var name in MainWindowViewModel.PersistedProperties.OrderBy(n => n))
            {
                if (!properties.Contains(name))
                {
                    Console.Error.WriteLine($"persisted property does not exist: {name}");
                    failures++;
                }
            }

            var settingsFields = typeof(Settings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract")
                .Select(p => p.Name).ToList();
            foreach (var field in settingsFields)
            {
                if (!MainWindowViewModel.PersistedProperties.Contains(field))
                {
                    Console.Error.WriteLine($"Settings.{field} is saved but never watched for changes");
                    failures++;
                }
            }
            Console.WriteLine($"{settingsFields.Count} settings, "
                              + $"{MainWindowViewModel.PersistedProperties.Count} watched properties");

            // 2. A first run with no file must not fail, and must write one
            //    once something changes.
            var store = new SettingsStore(directory);
            var first = new MainWindowViewModel(store);
            Console.WriteLine($"no file yet: theme={first.Theme} workflow={first.CurrentWorkflow} "
                              + $"task={first.Task} threads={first.Threads}");

            first.Theme = "Dark";
            first.CurrentWorkflow = "vc";
            first.Backend = "cuda";
            first.Threads = 3;
            first.ChunkBudget = 777;
            first.SplitLongText = false;
            first.FamilyHint = "parakeet_tdt";
            first.VoiceId = "af_heart";
            Thread.Sleep(1200);   // past the debounce

            if (!File.Exists(store.StorePath))
            {
                Console.Error.WriteLine("nothing was written"); failures++;
            }

            // 3. A second window reads them back.
            var second = new MainWindowViewModel(new SettingsStore(directory));
            foreach (var (what, got, want) in new (string, object?, object?)[]
                     {
                         ("theme", second.Theme, "Dark"),
                         ("workflow", second.CurrentWorkflow, "vc"),
                         ("backend", second.Backend, "cuda"),
                         ("threads", second.Threads, 3),
                         ("chunk budget", second.ChunkBudget, 777),
                         ("split long text", second.SplitLongText, false),
                         ("family hint", second.FamilyHint, "parakeet_tdt"),
                         ("voice id", second.VoiceId, "af_heart"),
                     })
            {
                var ok = Equals(got, want);
                Console.WriteLine($"  {what,-16} {got}  {(ok ? "" : $"(expected {want})")}");
                if (!ok) failures++;
            }

            // 4. A burst of changes must leave the last one on disk, and must
            //    not trip over its own debounce: each change disposes the
            //    source the previous one is waiting on.
            for (var i = 0; i < 2000; i++) first.ChunkBudget = 1000 + i;
            Thread.Sleep(1200);
            var burst = new MainWindowViewModel(new SettingsStore(directory));
            Console.WriteLine($"after 2000 rapid changes: chunk budget={burst.ChunkBudget} (want 2999)");
            if (burst.ChunkBudget != 2999) { Console.Error.WriteLine("the last change was lost"); failures++; }

            // The engine task is not saved — it follows from the workflow and
            // the model, so a saved one would be a choice the next model
            // overrules. Restoring the workflow has to leave a task inside it.
            Console.WriteLine($"  restored workflow {second.CurrentWorkflow} -> task {second.Task}");
            if (!Workflow.For(second.CurrentWorkflow).Tasks.Contains(second.Task))
            {
                Console.Error.WriteLine("the restored task is not in the restored workflow");
                failures++;
            }

            // The picker's choice is remembered per workflow, and across a
            // restart. Selecting in one tab, looking at another and coming back
            // has to bring the choice back with it.
            var picker = new MainWindowViewModel(new SettingsStore(directory));
            picker.CurrentWorkflow = "asr";
            var asrPick = picker.CatalogEntries.Skip(2).FirstOrDefault();
            picker.SelectedEntry = asrPick;
            picker.CurrentWorkflow = "tts";
            var ttsPick = picker.CatalogEntries.Skip(1).FirstOrDefault();
            picker.SelectedEntry = ttsPick;

            Console.WriteLine($"picked asr={asrPick?.Key} tts={ttsPick?.Key}");
            if (asrPick is null || ttsPick is null)
            {
                Console.Error.WriteLine("catalogue had nothing to pick"); failures++;
            }
            else
            {
                picker.CurrentWorkflow = "asr";
                var backToAsr = picker.SelectedEntry?.Key;
                picker.CurrentWorkflow = "tts";
                var backToTts = picker.SelectedEntry?.Key;
                Console.WriteLine($"  after switching away and back: asr={backToAsr} tts={backToTts}");
                if (backToAsr != asrPick.Key || backToTts != ttsPick.Key)
                {
                    Console.Error.WriteLine("a tab switch lost the selection"); failures++;
                }

                Thread.Sleep(1200);   // past the debounce
                var reopened = new MainWindowViewModel(new SettingsStore(directory));
                var restored = reopened.SelectedEntry?.Key;
                Console.WriteLine($"  after a restart, on {reopened.CurrentWorkflow}: {restored}");
                if (restored != ttsPick.Key)
                {
                    Console.Error.WriteLine($"a restart lost the selection "
                                            + $"(wanted {ttsPick.Key})");
                    failures++;
                }

                // And the other tab's choice comes back too, not just the one
                // that happened to be open when the app closed.
                reopened.CurrentWorkflow = "asr";
                Console.WriteLine($"  the other tab after a restart: {reopened.SelectedEntry?.Key}");
                if (reopened.SelectedEntry?.Key != asrPick.Key)
                {
                    Console.Error.WriteLine("a restart kept only the open tab's selection");
                    failures++;
                }
            }

            // A model loaded from a path is its own choice. Restoring a
            // remembered package must not overwrite it: selecting a package
            // sets ModelPath as a side effect, and that side effect is right
            // when a person clicks, wrong when it happens during a load that is
            // also restoring a saved path.
            var manual = new MainWindowViewModel(new SettingsStore(directory));
            manual.CurrentWorkflow = "asr";
            // An installed one: only an installed package sets ModelPath, so
            // picking any entry would pass this without exercising the clobber.
            var installed = manual.CatalogEntries.FirstOrDefault(e => e.IsInstalled);
            if (installed is null)
            {
                Console.WriteLine("  no installed package; the model-path clobber is not staged");
            }
            manual.SelectedEntry = installed ?? manual.CatalogEntries.FirstOrDefault();
            manual.ModelPath = "/models/hand-picked.gguf";
            Thread.Sleep(1200);

            var reloaded = new MainWindowViewModel(new SettingsStore(directory));
            Console.WriteLine($"  saved path '/models/hand-picked.gguf' came back as "
                              + $"'{reloaded.ModelPath}'");
            if (installed is not null && reloaded.ModelPath != "/models/hand-picked.gguf")
            {
                Console.Error.WriteLine("restoring the package overwrote the saved model path");
                failures++;
            }

            // 5. Reading settings must not itself write settings: applying a
            //    saved value raises PropertyChanged like any other assignment,
            //    and a save from inside the load would race the file it read.
            //
            //    Let whatever the checks above queued land first, then take the
            //    stamp, then load. Stamping while a legitimate save from an
            //    earlier step was still in its debounce made this fail on the
            //    test's own ordering rather than on the thing it checks.
            Thread.Sleep(1200);
            var stamp = File.GetLastWriteTimeUtc(store.StorePath);
            var loader = new MainWindowViewModel(new SettingsStore(directory));
            Console.WriteLine($"  a loading window opened on {loader.CurrentWorkflow}, "
                              + $"selection {loader.SelectedEntry?.Key ?? "(none)"}, "
                              + $"{loader.SelectedPackages.Count} remembered");
            Thread.Sleep(1200);
            if (File.GetLastWriteTimeUtc(store.StorePath) != stamp)
            {
                Console.Error.WriteLine("loading settings wrote the file back"); failures++;
            }

            // 6. A corrupt file falls back to defaults, says so, and does not throw.
            File.WriteAllText(store.StorePath, "{ this is not json");
            var broken = new SettingsStore(directory);
            var recovered = broken.Load();
            Console.WriteLine($"corrupt file: problem='{broken.LoadProblem?[..Math.Min(40, broken.LoadProblem.Length)]}…'");
            if (broken.LoadProblem is null || recovered.Theme is not null)
            {
                Console.Error.WriteLine("a corrupt file was not reported as one"); failures++;
            }
            var third = new MainWindowViewModel(new SettingsStore(directory));
            Console.WriteLine($"  after corruption: theme={third.Theme} (a default)");
            if (third.Theme != "System") { Console.Error.WriteLine("did not fall back"); failures++; }

            // 7. A file from a build that knew fewer settings leaves the rest alone.
            File.WriteAllText(store.StorePath, "{\"Theme\":\"Light\"}");
            var partial = new MainWindowViewModel(new SettingsStore(directory));
            Console.WriteLine($"  partial file: theme={partial.Theme} "
                              + $"workflow={partial.CurrentWorkflow} threads={partial.Threads}");
            if (partial.Theme != "Light" || partial.CurrentWorkflow != "asr")
            {
                Console.Error.WriteLine("a partial file did not leave defaults alone"); failures++;
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine(failures == 0 ? "settings OK" : $"settings: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
