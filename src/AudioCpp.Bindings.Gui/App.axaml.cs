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
        base.OnFrameworkInitializationCompleted();
    }
}
