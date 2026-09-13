using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioCpp;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// One side of a comparison: its own model, backend and threads, holding its
/// session open between runs so a repeat costs inference and not loading.
/// </summary>
public sealed class ArenaSlot(string title) : INotifyPropertyChanged
{
    private AudioCppRegistry? _registry;
    private AudioCppModel? _model;
    private AudioCppSession? _session;
    private string _sessionKey = "";

    private string _modelPath = "";
    private string _familyHint = "";
    private string _backend = "cuda";
    private int _threads = 8;
    private string _status = "No model";
    private string _transcript = "";
    private string _timing = "";
    private float[]? _audio;
    private int _audioRate;
    private int _audioChannels = 1;

    public string Title { get; } = title;

    public IReadOnlyList<string> Backends { get; } = ["cpu", "cuda", "hip", "vulkan", "metal", "best"];

    public string ModelPath { get => _modelPath; set => Set(ref _modelPath, value); }
    public string FamilyHint { get => _familyHint; set => Set(ref _familyHint, value); }
    public string Backend { get => _backend; set => Set(ref _backend, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Transcript { get => _transcript; set => Set(ref _transcript, value); }
    public string Timing { get => _timing; set => Set(ref _timing, value); }

    public bool IsLoaded => _model is not null;
    public float[]? Audio => _audio;
    public int AudioRate => _audioRate;
    public int AudioChannels => _audioChannels;

    /// <summary>Words this slot produced, for the count beside each side.</summary>
    public int WordCount => _transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public void Load()
    {
        Unload();
        _registry = AudioCppRegistry.Create();
        _model = _registry.Load(ModelPath, new ModelConfig(FamilyHint.Length > 0 ? FamilyHint : null));
        Status = $"{_model.Family} loaded";
        Notify(nameof(IsLoaded));
    }

    /// <summary>
    /// Run this slot against shared input, reusing the session when the
    /// configuration has not changed.
    /// </summary>
    public void Run(string task, (float[] Samples, int SampleRate, int Channels)? clip, string text)
    {
        if (_model is null) { Status = "No model"; return; }

        var key = $"{task}|{Backend}|{Threads}";
        var sessionWatch = System.Diagnostics.Stopwatch.StartNew();
        if (_session is null || _sessionKey != key)
        {
            _session?.Dispose();
            _session = _model.CreateSession(task, "offline", new BackendConfig(Backend, 0, Threads));
            _sessionKey = key;
        }
        var sessionMs = sessionWatch.ElapsedMilliseconds;

        using var request = new AudioCppRequest();
        if (task == "tts") request.SetText(text, "en-us");
        else if (clip is { } input) request.SetAudio(input.Samples, input.SampleRate, input.Channels);
        else { Status = "No input"; return; }

        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        using var result = _session.Run(request);
        var runMs = runWatch.ElapsedMilliseconds;

        Transcript = result.Text?.Text ?? "";
        if (result.Audio is { } audio)
        {
            _audio = audio.Samples;
            _audioRate = audio.SampleRate;
            _audioChannels = audio.Channels;
        }

        Timing = sessionMs > 0
            ? $"session {sessionMs} ms · run {runMs} ms"
            : $"run {runMs} ms";
        Status = $"{_model.Family} · {WordCount} words";
        Notify(nameof(WordCount));
    }

    public void Unload()
    {
        _session?.Dispose();
        _model?.Dispose();
        _registry?.Dispose();
        _session = null;
        _model = null;
        _registry = null;
        _sessionKey = "";
        _audio = null;
        Status = "No model";
        Notify(nameof(IsLoaded));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
