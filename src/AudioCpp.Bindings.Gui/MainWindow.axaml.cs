using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace AudioCpp.Bindings.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel viewModel) return;
            viewModel.PickPath = PickPathAsync;
            viewModel.PickSavePath = PickSavePathAsync;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnSaveStream(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: NamedAudioEntry stream }
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SaveStreamAsync(stream);
        }
    }

    /// <summary>
    /// Release the microphone on close. Without this it stays open until the
    /// process exits, which on most desktops leaves a recording indicator lit
    /// after the window is gone.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) await viewModel.StopAudioAsync();
        base.OnClosing(e);
    }

    private async Task<string?> PickPathAsync(string title, bool directory)
    {
        if (directory)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async Task<string?> PickSavePathAsync(string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save generated audio",
            SuggestedFileName = suggested,
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }],
        });
        return file?.Path.LocalPath;
    }

    private async void OnPickModelFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) await viewModel.PickModelAsync(directory: false);
    }

    private async void OnPickVadModel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            var picked = await viewModel.PickPath!("Silero VAD model directory", true);
            if (picked is not null) viewModel.VadModelPath = picked;
        }
    }

    private async void OnPickModelFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) await viewModel.PickModelAsync(directory: true);
    }

    private async void OnPickAudio(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) await viewModel.PickAudioAsync();
    }
}
