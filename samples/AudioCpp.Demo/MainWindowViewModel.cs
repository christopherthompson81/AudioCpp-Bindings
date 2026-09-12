using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioCpp.Native;

namespace AudioCpp.Demo;

/// <summary>
/// Drives the sample. Everything here goes through the C ABI: the model is loaded
/// once, its options are read out of the model rather than hardcoded, and the
/// session is kept alive across runs — which is the whole reason to embed rather
/// than shell out to the CLI.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private AudioCppRegistry? _registry;
    private AudioCppModel? _model;
    private AudioCppSession? _session;
    private string _sessionKey = "";

    private string _modelPath = "";
    private string _familyHint = "";
    private string _backend = "cpu";
    private int _threads = Environment.ProcessorCount;
    private string _task = "asr";
    private string _audioPath = "";
    private string _text = "The quick brown fox jumps over the lazy dog.";
    private string _voiceId = "";
    private string _status = "Pick a model file or directory, then Load.";
    private bool _busy;
    private bool _isLoaded;
    private string _modelSummary = "";
    private string _transcript = "";
    private float[]? _outputSamples;
    private int _outputSampleRate;
    private int _outputChannels = 1;
    private double _lastRunSeconds;

    public MainWindowViewModel()
    {
        LoadCommand = new RelayCommand(LoadAsync, () => !_busy && ModelPath.Length > 0);
        RunCommand = new RelayCommand(RunAsync, () => !_busy && _isLoaded);
        SaveWavCommand = new RelayCommand(SaveWavAsync, () => !_busy && _outputSamples is { Length: > 0 });
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand SaveWavCommand { get; }

    /// <summary>Set by the view so file pickers can be opened from here.</summary>
    public Func<string, bool, Task<string?>>? PickPath { get; set; }

    /// <summary>Set by the view so the save dialog can be opened from here.</summary>
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    public IReadOnlyList<string> Backends { get; } = ["cpu", "cuda", "hip", "vulkan", "metal", "best"];
    public IReadOnlyList<string> Tasks { get; } = ["asr", "tts", "vad", "diar", "sep", "align"];

    /// <summary>Whatever the loaded family declares, read at runtime rather than hardcoded.</summary>
    public ObservableCollection<DeclaredOption> Options { get; } = [];
    public ObservableCollection<ResultRow> Rows { get; } = [];

    public string ModelPath
    {
        get => _modelPath;
        set { if (Set(ref _modelPath, value)) LoadCommand.RaiseCanExecuteChanged(); }
    }

    public string FamilyHint { get => _familyHint; set => Set(ref _familyHint, value); }
    public string Backend { get => _backend; set => Set(ref _backend, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public string Task { get => _task; set => Set(ref _task, value); }
    public string AudioPath { get => _audioPath; set => Set(ref _audioPath, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string VoiceId { get => _voiceId; set => Set(ref _voiceId, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ModelSummary { get => _modelSummary; set => Set(ref _modelSummary, value); }
    public string Transcript { get => _transcript; set => Set(ref _transcript, value); }
    public bool IsLoaded { get => _isLoaded; private set => Set(ref _isLoaded, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            LoadCommand.RaiseCanExecuteChanged();
            RunCommand.RaiseCanExecuteChanged();
            SaveWavCommand.RaiseCanExecuteChanged();
        }
    }

    public string AbiVersion
    {
        get
        {
            try
            {
                var packed = AudioCppRegistry.AbiVersion;
                return $"ABI {packed >> 16}.{(packed >> 8) & 0xFF}.{packed & 0xFF}  ·  " +
                       $"audio.cpp {AudioCppRegistry.BuildVersion}";
            }
            catch (Exception exception)
            {
                return $"libaudiocpp not loaded: {exception.Message}";
            }
        }
    }

    /// <summary>The generated waveform, for the preview strip. Empty when a run produced no audio.</summary>
    public float[] OutputSamples => _outputSamples ?? [];
    public string OutputSummary => _outputSamples is { Length: > 0 }
        ? $"{(double)_outputSamples.Length / Math.Max(_outputChannels, 1) / Math.Max(_outputSampleRate, 1):F2}s " +
          $"at {_outputSampleRate} Hz, {_outputChannels}ch"
        : "";
    public string Timing => _lastRunSeconds > 0 ? $"{_lastRunSeconds * 1000:F0} ms" : "";

    public async Task PickModelAsync(bool directory)
    {
        if (PickPath is null) return;
        var picked = await PickPath("Select a model", directory);
        if (picked is not null) ModelPath = picked;
    }

    public async Task PickAudioAsync()
    {
        if (PickPath is null) return;
        var picked = await PickPath("Select a WAV file", false);
        if (picked is not null) AudioPath = picked;
    }

    private async Task LoadAsync()
    {
        Busy = true;
        Status = "Loading…";
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                // Freed in reverse, though the ABI keeps parents alive so order is free.
                _session?.Dispose();
                _model?.Dispose();
                _registry?.Dispose();
                _session = null;
                _sessionKey = "";

                _registry = AudioCppRegistry.Create();
                _model = _registry.Load(ModelPath,
                    new ModelConfig(FamilyHint.Length > 0 ? FamilyHint : null));
            });

            var model = _model!;
            var supported = Tasks.Where(task => model.Supports(task, "offline")).ToList();
            ModelSummary =
                $"{model.Family}\n{model.Description}\n\n" +
                $"tasks: {(supported.Count > 0 ? string.Join(", ", supported) : "none advertised")}\n" +
                $"timestamps: {model.SupportsTimestamps}   speaker ref: {model.SupportsSpeakerReference}   " +
                $"style: {model.SupportsStyleCondition}\n" +
                $"languages: {(model.Languages.Count > 0 ? string.Join(", ", model.Languages.Take(12)) : "unspecified")}";

            Options.Clear();
            foreach (var scope in Enum.GetValues<AudioCppOptionScope>())
            {
                foreach (var option in model.GetOptions(scope))
                {
                    Options.Add(new DeclaredOption(
                        scope.ToString(), option.Name, option.ValueName, option.DefaultValue,
                        option.MinValue.Length > 0 || option.MaxValue.Length > 0
                            ? $"[{option.MinValue},{option.MaxValue}]"
                            : "",
                        option.Required));
                }
            }

            if (supported.Count > 0 && !supported.Contains(Task)) Task = supported[0];
            IsLoaded = true;
            Status = $"Loaded {model.Family} — {Options.Count} declared option(s), read from the model.";
        }
        catch (Exception exception)
        {
            IsLoaded = false;
            ModelSummary = "";
            Options.Clear();
            Status = Describe(exception);
        }
        finally
        {
            Busy = false;
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunAsync()
    {
        Busy = true;
        Status = "Running…";
        Rows.Clear();
        Transcript = "";
        _outputSamples = null;
        try
        {
            var started = DateTime.UtcNow;
            var rows = new List<ResultRow>();
            string transcript = "";

            await System.Threading.Tasks.Task.Run(() =>
            {
                // One session per task/backend/threads combination, reused across runs.
                var key = $"{Task}|{Backend}|{Threads}";
                if (_session is null || _sessionKey != key)
                {
                    _session?.Dispose();
                    _session = _model!.CreateSession(Task, "offline", new BackendConfig(Backend, 0, Threads));
                    _sessionKey = key;
                }

                using var request = new AudioCppRequest();
                if (Task == "tts")
                {
                    request.SetText(Text, "en-us");
                    if (VoiceId.Length > 0) request.SetVoiceId(VoiceId);
                }
                else
                {
                    if (AudioPath.Length == 0) throw new InvalidOperationException("Select a WAV file first.");
                    var clip = Wav.Read(AudioPath);
                    request.SetAudio(clip.Samples, clip.SampleRate, clip.Channels);
                }

                using var result = _session!.Run(request);

                if (result.Text is { } text)
                {
                    transcript = text.Text;
                }
                if (result.Audio is { } audio)
                {
                    _outputSamples = audio.Samples;
                    _outputSampleRate = audio.SampleRate;
                    _outputChannels = audio.Channels;
                }
                foreach (var segment in result.Segments)
                {
                    rows.Add(new ResultRow("segment", Span(segment.StartSample, segment.EndSample),
                        segment.Text, segment.Confidence.ToString("F3")));
                }
                foreach (var turn in result.SpeakerTurns)
                {
                    rows.Add(new ResultRow("speaker", Span(turn.StartSample, turn.EndSample),
                        turn.SpeakerId, turn.Confidence.ToString("F3")));
                }
                foreach (var word in result.Words)
                {
                    rows.Add(new ResultRow("word", Span(word.StartSample, word.EndSample),
                        word.Word, word.Confidence.ToString("F3")));
                }
                foreach (var stream in result.NamedAudio)
                {
                    rows.Add(new ResultRow("stream", $"{stream.Duration:F2}s", stream.Id,
                        $"{stream.SampleRate} Hz"));
                }
                foreach (var artifact in result.Artifacts)
                {
                    rows.Add(new ResultRow("artifact", $"{artifact.Payload.Length} B",
                        artifact.Id, artifact.Kind.ToString()));
                }
            });

            _lastRunSeconds = (DateTime.UtcNow - started).TotalSeconds;
            Transcript = transcript;
            foreach (var row in rows) Rows.Add(row);
            Notify(nameof(OutputSamples));
            Notify(nameof(OutputSummary));
            Notify(nameof(Timing));
            Status = rows.Count == 0 && transcript.Length == 0 && _outputSamples is null
                ? "Ran, but the family produced no output for this input."
                : $"Done in {_lastRunSeconds * 1000:F0} ms.";
        }
        catch (Exception exception)
        {
            Status = Describe(exception);
        }
        finally
        {
            Busy = false;
            SaveWavCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SaveWavAsync()
    {
        if (_outputSamples is null || PickSavePath is null) return;
        var path = await PickSavePath("output.wav");
        if (path is null) return;
        try
        {
            Wav.Write(path, _outputSamples, _outputSampleRate, _outputChannels);
            Status = $"Wrote {path}";
        }
        catch (Exception exception)
        {
            Status = Describe(exception);
        }
    }

    private static string Span(long start, long end) => $"{start}–{end}";

    /// <summary>
    /// A failed ABI call carries the native detail, which is far more useful than
    /// the managed exception type alone.
    /// </summary>
    private static string Describe(Exception exception) => exception switch
    {
        AudioCppException native => $"{native.Operation} failed: {native.Detail} [{native.Status}]",
        DllNotFoundException => "libaudiocpp was not found. Set AUDIOCPP_NATIVE_DIR to the directory "
                               + "containing it, or place it beside this executable.",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

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

public readonly record struct DeclaredOption(
    string Scope, string Name, string Type, string Default, string Range, bool Required);

public readonly record struct ResultRow(string Kind, string Span, string Value, string Detail);
