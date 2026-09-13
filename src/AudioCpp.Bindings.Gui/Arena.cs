using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace AudioCpp.Bindings.Gui;

/// <summary>One run of a word in the comparison, coloured by which side had it.</summary>
public sealed record DiffPiece(string Text, IBrush Brush, string Side);

/// <summary>
/// Two configurations, one input, results side by side.
/// </summary>
/// <remarks>
/// This is the question the repository exists to answer: is a quantisation, a
/// backend or a family good enough to use? A number on its own does not settle
/// that, so the comparison shows agreement, per-side timing, and which words
/// the two actually disagreed about.
/// </remarks>
public sealed class Arena : INotifyPropertyChanged
{
    private string _audioPath = "";
    private string _text = "The quick brown fox jumps over the lazy dog.";
    private string _task = "asr";
    private string _status = "Configure both sides, then Compare.";
    private string _summary = "";
    private bool _busy;

    public Arena()
    {
        LoadLeftCommand = new RelayCommand(() => LoadAsync(Left), () => !_busy);
        LoadRightCommand = new RelayCommand(() => LoadAsync(Right), () => !_busy);
        CompareCommand = new RelayCommand(CompareAsync, () => !_busy && Left.IsLoaded && Right.IsLoaded);
    }

    public ArenaSlot Left { get; } = new("A");
    public ArenaSlot Right { get; } = new("B");

    public IReadOnlyList<string> Tasks { get; } = ["asr", "tts", "diar", "sep", "align", "vad"];

    public string AudioPath { get => _audioPath; set => Set(ref _audioPath, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Task { get => _task; set => Set(ref _task, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Agreement and the word counts behind it.</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            LoadLeftCommand.RaiseCanExecuteChanged();
            LoadRightCommand.RaiseCanExecuteChanged();
            CompareCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The word-level comparison, ready to render as coloured runs.</summary>
    public ObservableCollection<DiffPiece> Diff { get; } = [];

    public RelayCommand LoadLeftCommand { get; }
    public RelayCommand LoadRightCommand { get; }
    public RelayCommand CompareCommand { get; }

    private async Task LoadAsync(ArenaSlot slot)
    {
        Busy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(slot.Load);
            Status = $"{slot.Title}: {slot.Status}";
        }
        catch (Exception exception) when (exception is AudioCppException or InvalidOperationException)
        {
            slot.Status = exception.Message;
            Status = $"{slot.Title}: {exception.Message}";
        }
        finally
        {
            Busy = false;
            CompareCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task CompareAsync()
    {
        Busy = true;
        Diff.Clear();
        Summary = "";
        try
        {
            (float[] Samples, int SampleRate, int Channels)? clip = null;
            if (Task != "tts")
            {
                if (AudioPath.Length == 0) { Status = "Select a WAV first."; return; }
                clip = Wav.Read(AudioPath);
            }

            // Sequential, not parallel: two models on one GPU would contend and
            // the timings would measure the contention rather than the models.
            await System.Threading.Tasks.Task.Run(() =>
            {
                Left.Run(Task, clip, Text);
                Right.Run(Task, clip, Text);
            });

            BuildDiff();
            Status = "Compared.";
        }
        catch (Exception exception) when (exception is AudioCppException
                                          or InvalidOperationException or IOException)
        {
            Status = exception.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void BuildDiff()
    {
        Diff.Clear();
        if (Left.Transcript.Length == 0 && Right.Transcript.Length == 0)
        {
            Summary = "Neither side produced text; compare the audio instead.";
            return;
        }

        var agreement = TextDiff.Agreement(Left.Transcript, Right.Transcript);
        Summary = $"{agreement * 100:F1}% agreement · A {Left.WordCount} words · B {Right.WordCount} words";

        foreach (var piece in TextDiff.Compare(Left.Transcript, Right.Transcript))
        {
            var brush = piece.Mark switch
            {
                TextDiff.Mark.OnlyLeft => Brushes.Orange,
                TextDiff.Mark.OnlyRight => Brushes.DeepSkyBlue,
                _ => Brushes.Gray,
            };
            var side = piece.Mark switch
            {
                TextDiff.Mark.OnlyLeft => "A only",
                TextDiff.Mark.OnlyRight => "B only",
                _ => "both",
            };
            Diff.Add(new DiffPiece(piece.Text, brush, side));
        }
    }

    public void Dispose()
    {
        Left.Unload();
        Right.Unload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
