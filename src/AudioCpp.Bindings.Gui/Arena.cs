using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioCpp.Audio;
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
    private AudioPlayer? _player;

    public Arena()
    {
        LoadLeftCommand = new RelayCommand(() => LoadAsync(Left), () => !_busy);
        LoadRightCommand = new RelayCommand(() => LoadAsync(Right), () => !_busy);
        CompareCommand = new RelayCommand(CompareAsync, () => !_busy && Left.IsLoaded && Right.IsLoaded);
        UnloadLeftCommand = new RelayCommand(() => UnloadAsync(Left), () => Left.IsLoaded && !_busy);
        UnloadRightCommand = new RelayCommand(() => UnloadAsync(Right), () => Right.IsLoaded && !_busy);
        PlayLeftCommand = new RelayCommand(() => PlayAsync(Left), () => Left.Audio is { Length: > 0 });
        PlayRightCommand = new RelayCommand(() => PlayAsync(Right), () => Right.Audio is { Length: > 0 });
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
            RefreshCommands();
        }
    }

    /// <summary>The word-level comparison, ready to render as coloured runs.</summary>
    public ObservableCollection<DiffPiece> Diff { get; } = [];

    public RelayCommand LoadLeftCommand { get; }
    public RelayCommand LoadRightCommand { get; }
    public RelayCommand CompareCommand { get; }

    /// <summary>
    /// Free a side. Two models here sit alongside whatever the Studio holds, so
    /// three can be resident at once — enough to exhaust a card without an
    /// explicit way to give one back.
    /// </summary>
    public RelayCommand UnloadLeftCommand { get; }
    public RelayCommand UnloadRightCommand { get; }

    /// <summary>Hear a side's synthesis: comparing two voices means listening to both.</summary>
    public RelayCommand PlayLeftCommand { get; }
    public RelayCommand PlayRightCommand { get; }

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
            RefreshCommands();
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

    private async Task UnloadAsync(ArenaSlot slot)
    {
        StopPlayer();
        await System.Threading.Tasks.Task.Run(slot.Unload);
        Status = $"{slot.Title}: unloaded";
        RefreshCommands();
    }

    /// <summary>
    /// One player at a time: comparing two clips means hearing them one after
    /// the other, and two devices open would play them over each other.
    /// </summary>
    private Task PlayAsync(ArenaSlot slot)
    {
        StopPlayer();
        if (slot.Audio is not { Length: > 0 } samples) return System.Threading.Tasks.Task.CompletedTask;

        try
        {
            _player = AudioPlayer.Open(samples, slot.AudioRate, slot.AudioChannels);
            _player.Play();
            Status = $"Playing {slot.Title}.";
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or DllNotFoundException or ArgumentException)
        {
            Status = exception.Message;
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void StopPlayer()
    {
        _player?.Dispose();
        _player = null;
    }

    private void RefreshCommands()
    {
        LoadLeftCommand.RaiseCanExecuteChanged();
        LoadRightCommand.RaiseCanExecuteChanged();
        CompareCommand.RaiseCanExecuteChanged();
        UnloadLeftCommand.RaiseCanExecuteChanged();
        UnloadRightCommand.RaiseCanExecuteChanged();
        PlayLeftCommand.RaiseCanExecuteChanged();
        PlayRightCommand.RaiseCanExecuteChanged();
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
        StopPlayer();
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
