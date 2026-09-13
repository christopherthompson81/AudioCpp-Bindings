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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
