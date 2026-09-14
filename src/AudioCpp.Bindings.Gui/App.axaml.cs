using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AudioCpp.Bindings.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // --live-check drives the microphone path with a window up, which is
            // the only way to verify anything that reports through the
            // dispatcher; see LiveCheck.
            var args = desktop.Args ?? [];
            if (args.Contains("--live-check"))
            {
                LiveCheck.Arm(viewModel, args, seconds: 8);
            }
        }
        // macOS reads an app's Dock icon from its .app bundle, and this is a bare
        // executable unless package-macos.sh built one, so it would otherwise show
        // the generic .NET rocket. ApplicationIcon is a Windows PE resource and
        // Window.Icon is the title-bar proxy, so neither covers the Dock tile.
        // No-op everywhere else.
        if (OperatingSystem.IsMacOS())
            MacDockIcon.Set("avares://AudioCpp.Bindings.Gui/Assets/AppIcon.png");

        base.OnFrameworkInitializationCompleted();
    }
}
