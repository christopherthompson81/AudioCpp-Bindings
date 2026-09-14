using Avalonia;

namespace AudioCpp.Bindings.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A GUI cannot be driven headlessly, but the view model can, and it is where
        // every ABI call lives. --smoke exercises the same path the window does, so
        // "the demo works" is checkable without a display.
        if (args.Length > 0 && args[0] == "--smoke")
        {
            return Smoke.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        }

        // Every --*-check runs against a scratch config directory. The server
        // page writes a real server.json when it starts, so without this a
        // check would overwrite the config of whoever ran it -- and would then
        // inherit whatever the last check left behind, which is how a check
        // that passed on its own started failing in sequence.
        //
        // Set before Avalonia builds anything, because the view model restores
        // that config in its constructor.
        if (args.Any(argument => argument.EndsWith("-check", StringComparison.Ordinal)))
        {
            var scratch = Path.Combine(Path.GetTempPath(), $"audiocpp-check-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", scratch);
        }

        // Settings round-trip, in a scratch directory so a check never writes
        // over the settings of the person running it.
        if (args.Contains("--settings-check"))
        {
            return SettingsCheck.Run();
        }

        // Pure logic, so it runs without a display. The save dialog itself
        // cannot be driven headlessly; what broke was the mapping in front of
        // it, and that can be.
        if (args.Contains("--save-check"))
        {
            var failures = 0;
            foreach (var (suggested, title, description, extension) in new[]
                     {
                         ("output.wav", "Save audio", "WAV audio", "wav"),
                         ("vocals.wav", "Save audio", "WAV audio", "wav"),
                         ("transcript.srt", "Save subtitles", "SubRip subtitles", "srt"),
                         ("transcript.vtt", "Save subtitles", "WebVTT subtitles", "vtt"),
                         ("result.json", "Save data", "JSON", "json"),
                         ("result.mid", "Save MIDI", "MIDI file", "mid"),
                         ("notes.unknown", "Save file", "UNKNOWN", "unknown"),
                     })
            {
                var kind = SaveKind.For(suggested);
                Console.WriteLine($"{suggested,-18} {kind.Title,-15} {kind.Description,-18} {kind.Pattern}");
                if (kind.Title != title || kind.Description != description
                    || kind.Extension != extension)
                {
                    Console.Error.WriteLine($"  expected {title} / {description} / *.{extension}");
                    failures++;
                }
            }
            Console.WriteLine(failures == 0 ? "save kinds OK" : $"save kinds: {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
