using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Runtime.CompilerServices;
using AudioCpp.Native;
using AudioCpp.Packages;
using AudioCpp.Audio;
using Avalonia.Threading;

namespace AudioCpp.Bindings.Gui;

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
    private AudioCppModel? _vadModel;
    private AudioCppSession? _vadSession;
    private string _vadSessionKey = "";

    private string _modelPath = "";
    private string _familyHint = "";
    private string _backend = "cpu";
    private int _threads = PhysicalCoreCount();
    private bool _useBuiltInChunking = true;
    private double _chunkSeconds = 10;
    private string _vadAssetPath = "";
    private string _resultJson = "";
    private readonly PackageInstaller _installer = new();
    private CatalogEntry? _selectedEntry;
    private string _modelsRoot = DefaultModelsRoot();
    private double _installFraction;
    private string _installStatus = "";
    private bool _splitLongText = true;
    private int _chunkBudget = 1000;
    private readonly List<AudioSegment> _segments = [];
    private CancellationTokenSource? _cancel;
    private LiveTranscription? _live;
    private bool _togglingRecording;
    private CaptureDeviceInfo? _captureDevice;
    private bool _isRecording;
    private float _inputLevel;
    private string _liveStatus = "";
    private AudioPlayer? _player;
    private DispatcherTimer? _playTimer;
    private double _playProgress = -1;
    private ResultRow? _selectedRow;
    private bool _showAllOptions;
    private readonly List<(string Word, long StartSample, long EndSample)> _words = [];
    // Word offsets from a live run are in the capture rate, not a guess. Tied to
    // the one constant the pump and the device both use.
    private int _resultSampleRate = LiveTranscription.SampleRate;
    private string _timingBreakdown = "";
    private string _page = "Studio";
    private string _theme = "System";
    private string _language = Resources.Loc.InitialLanguage();
    private readonly VoiceLibrary _voices = new();
    private SavedVoice? _selectedVoice;
    private string _voiceAudioPath = "";
    private string _voiceTranscript = "";
    private string _voiceName = "";
    private string _playStatus = "";
    private string _task = "asr";
    private string _audioPath = "";
    private string _text = "The quick brown fox jumps over the lazy dog.";
    private string _voiceId = "";
    private string _vadModelPath = "";
    private double _minSegmentSpan = 15;
    private double _maxSegmentSpan = 28;
    private string _status = "Pick a model file or directory, then Load.";
    private bool _busy;
    private bool _isLoaded;
    private string _modelSummary = "";
    private string _transcript = "";
    private float[]? _outputSamples;
    private int _outputSampleRate;
    private int _outputChannels = 1;
    private double _lastRunSeconds;
    private string _segmentedSummary = "";

    public MainWindowViewModel()
    {
        LoadCommand = new RelayCommand(LoadAsync, () => !_busy && ModelPath.Length > 0);
        RunCommand = new RelayCommand(RunAsync, () => !_busy && _isLoaded);
        SaveWavCommand = new RelayCommand(SaveWavAsync, () => !_busy && _outputSamples is { Length: > 0 });
        CancelCommand = new RelayCommand(CancelAsync, () => _busy && _cancel is not null);

        // Present before any model is loaded, as the web UI's are. Counts fill in
        // on load; an empty row would read as a broken layout rather than an
        // unloaded one.
        foreach (var task in Tasks) TaskChips.Add(new TaskChip(task, TitleFor(task), 0));
        RelabelChoices();

        InstallCommand = new RelayCommand(
            InstallAsync, () => !_busy && _selectedEntry is { IsInstalled: false }
                                && _selectedEntry.Package.Download.Supported);
        DeleteCommand = new RelayCommand(
            DeleteAsync, () => !_busy && _selectedEntry is { State: not InstallState.Missing });

        RecordCommand = new RelayCommand(
            ToggleRecordingAsync,
            () => _isRecording || (!_busy && _isLoaded && _task == "asr"));

        PlayCommand = new RelayCommand(TogglePlaybackAsync, () => HasPreviewAudio);
        UnloadCommand = new RelayCommand(UnloadAsync, () => _isLoaded && !_busy);
        SaveVoiceCommand = new RelayCommand(SaveVoiceAsync,
            () => _voiceName.Length > 0 && _voiceAudioPath.Length > 0);
        DeleteVoiceCommand = new RelayCommand(DeleteVoiceAsync, () => _selectedVoice is not null);
        // Demo voices first, then the user's own: a library that starts empty
        // gives no way to try cloning without finding a clip yourself.
        foreach (var voice in VoiceLibrary.DemoVoices()) Voices.Add(voice);
        foreach (var voice in _voices.Load()) Voices.Add(voice);
        SaveSrtCommand = new RelayCommand(() => SaveSubtitlesAsync("srt"), () => HasWordTimings);
        SaveVttCommand = new RelayCommand(() => SaveSubtitlesAsync("vtt"), () => HasWordTimings);

        LoadCatalog();
        LoadCaptureDevices();
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand SaveWavCommand { get; }

    /// <summary>
    /// Stops a run between segments. A single audiocpp_session_run() cannot be
    /// interrupted -- the call is synchronous and the ABI offers no abort -- so
    /// this takes effect only where a run is a loop: VAD-segmented ASR, and
    /// long-text synthesis. Disabled when there is nothing loop-shaped to stop.
    /// </summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Fetch the selected package's files.</summary>
    public RelayCommand InstallCommand { get; }

    /// <summary>Remove an installed package's files from the models root.</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>Start or stop microphone transcription.</summary>
    public RelayCommand RecordCommand { get; }

    /// <summary>Play or pause the preview.</summary>
    public RelayCommand PlayCommand { get; }

    /// <summary>Free the loaded model and everything built from it.</summary>
    public RelayCommand UnloadCommand { get; }

    /// <summary>Set by the view so file pickers can be opened from here.</summary>
    public Func<string, bool, Task<string?>>? PickPath { get; set; }

    /// <summary>Set by the view so the save dialog can be opened from here.</summary>
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    public IReadOnlyList<string> Backends { get; } = ["cpu", "cuda", "hip", "vulkan", "metal", "best"];
    public IReadOnlyList<string> Tasks { get; } = ["asr", "tts", "vad", "diar", "sep", "align"];

    /// <summary>
    /// The task selector along the top. Count is how many of the loaded model's
    /// tasks match -- 0 or 1 here, since this app holds one model, where the web
    /// UI counts across a whole catalog. Same shape, honest number.
    /// </summary>
    public ObservableCollection<TaskChip> TaskChips { get; } = [];

    public TaskChip? SelectedChip
    {
        get => TaskChips.FirstOrDefault(c => c.Task == _task);
        set { if (value is not null) Task = value.Task; }
    }

    /// <summary>Capture devices the system offers, refreshed on demand.</summary>
    public ObservableCollection<CaptureDeviceInfo> CaptureDevices { get; } = [];

    /// <summary>Null means the system default, which is what most users want.</summary>
    public CaptureDeviceInfo? CaptureDevice
    {
        get => _captureDevice;
        set => Set(ref _captureDevice, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!Set(ref _isRecording, value)) return;
            Notify(nameof(RecordLabel));
            RecordCommand.RaiseCanExecuteChanged();
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    public string RecordLabel => _isRecording ? Resources.Strings.Stop : Resources.Strings.Record;

    /// <summary>
    /// Something to play: generated output, or the loaded input clip. Output
    /// wins, since after a TTS run that is what the user just made.
    /// </summary>
    public bool HasPreviewAudio => _outputSamples is { Length: > 0 } || _inputClip is not null;

    public string PlayLabel => _player?.IsPlaying == true
        ? Resources.Strings.Pause : Resources.Strings.Play;

    /// <summary>Playhead as a fraction, or negative when there is nothing to show.</summary>
    public double PlayProgress { get => _playProgress; private set => Set(ref _playProgress, value); }

    public string PlayStatus { get => _playStatus; private set => Set(ref _playStatus, value); }

    /// <summary>
    /// Selecting a result row seeks the preview to it.
    /// </summary>
    /// <remarks>
    /// The reason the rows carry sample offsets rather than only a formatted
    /// span: a transcript line is a position in the audio, and being able to
    /// jump to it is what makes a result inspectable rather than a wall of text.
    /// </remarks>
    public ResultRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            if (value is not { StartSample: >= 0 } row) return;
            SeekToSample(row.StartSample);
        }
    }

    /// <summary>
    /// Seek the preview to an absolute sample offset in the source audio.
    /// Opens a player if none is running, so a click works before Play is hit.
    /// </summary>
    private void SeekToSample(long sample)
    {
        if (_player is null && !TryOpenPlayer()) return;
        if (_player is null) return;

        // Offsets are in source frames; the player may hold a different clip
        // only if the output replaced the input, in which case they still share
        // a sample rate for every family seen so far.
        _player.Position = sample;
        UpdatePlayProgress();
    }

    /// <summary>Handed to the waveform so a click seeks.</summary>
    public Action<double> SeekTo => fraction =>
    {
        if (_player is null) return;
        _player.PositionSeconds = fraction * _player.LengthSeconds;
        UpdatePlayProgress();
    };

    /// <summary>Peak level 0..1 for the meter.</summary>
    public float InputLevel { get => _inputLevel; private set => Set(ref _inputLevel, value); }

    /// <summary>Device, backend, and any dropped frames.</summary>
    public string LiveStatus { get => _liveStatus; private set => Set(ref _liveStatus, value); }

    /// <summary>Every installable package, read from audio.cpp's model_specs.</summary>
    public List<CatalogEntry> AllEntries { get; } = [];

    /// <summary>
    /// The packages the picker shows: those whose family declares the selected
    /// task. Listing all 235 regardless meant scrolling past 37 TTS models to
    /// find an ASR one.
    /// </summary>
    public ObservableCollection<CatalogEntry> CatalogEntries { get; } = [];

    /// <summary>
    /// Picking a package fills in the path and family, which is the whole point:
    /// the reference UI is a dropdown where this app had a path box.
    /// </summary>
    public CatalogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!Set(ref _selectedEntry, value)) return;
            InstallCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            Notify(nameof(SelectedSummary));
            if (value is null) return;

            FamilyHint = value.Family.Family;
            if (value.IsInstalled) ModelPath = value.ResolvePath(ModelsRoot);
        }
    }

    public string ModelsRoot
    {
        get => _modelsRoot;
        set { if (Set(ref _modelsRoot, value)) RefreshCatalogState(); }
    }

    /// <summary>0 to 1 while installing; drives the progress bar.</summary>
    public double InstallFraction { get => _installFraction; private set => Set(ref _installFraction, value); }

    public string InstallStatus { get => _installStatus; private set => Set(ref _installStatus, value); }

    public string SelectedSummary => _selectedEntry is null
        ? "No package selected"
        : _selectedEntry.Note.Length > 0
            ? _selectedEntry.Note
            : $"{_selectedEntry.StateText}   {_selectedEntry.SizeText}".Trim();

    /// <summary>Whatever the loaded family declares, read at runtime rather than hardcoded.</summary>
    public ObservableCollection<DeclaredOption> Options { get; } = [];

    /// <summary>
    /// Per-run options, as typed controls. Separated from session options
    /// because changing one of those rebuilds the session, which is a different
    /// promise to make to a user mid-experiment.
    /// </summary>
    public ObservableCollection<DeclaredOption> RequestOptions { get; } = [];

    public ObservableCollection<DeclaredOption> SessionOptions { get; } = [];

    /// <summary>Two configurations compared on one input.</summary>
    public Arena Arena { get; } = new();

    /// <summary>
    /// Pages and themes as (identity, label) pairs.
    /// </summary>
    /// <remarks>
    /// The identity is what the rest of the app switches on and what a
    /// screenshot run passes on the command line; the label is what the chip
    /// shows, and changes with the language. Keeping them as one string meant
    /// the nav chips stayed English in every translation, which was the first
    /// thing visible in a Russian screenshot.
    /// </remarks>
    public ObservableCollection<Choice> Pages { get; } =
        [new("Studio", ""), new("Arena", ""), new("Runtime", "")];

    public Choice? SelectedPage
    {
        get => Pages.FirstOrDefault(p => p.Id == _page);
        set { if (value is not null) Page = value.Id; }
    }

    public ObservableCollection<Choice> Themes { get; } =
        [new("System", ""), new("Light", ""), new("Dark", "")];

    public Choice? SelectedTheme
    {
        get => Themes.FirstOrDefault(t => t.Id == _theme);
        set { if (value is not null) Theme = value.Id; }
    }

    private void RelabelChoices()
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            Pages[i] = Pages[i] with { Label = Resources.Strings.Get($"nav.{Pages[i].Id.ToLowerInvariant()}") };
        }
        for (var i = 0; i < Themes.Count; i++)
        {
            Themes[i] = Themes[i] with { Label = Resources.Strings.Get($"theme.{Themes[i].Id.ToLowerInvariant()}") };
        }
        Notify(nameof(SelectedPage));
        Notify(nameof(SelectedTheme));
    }

    public IReadOnlyList<string> Languages => Resources.Loc.Available;

    /// <summary>
    /// Interface language. The pseudo-locale is not a translation — it is
    /// English with brackets and accents, which makes an unextracted string
    /// obvious and shows where a layout assumed English width.
    /// </summary>
    public string Language
    {
        get => _language;
        set
        {
            if (!Set(ref _language, value)) return;
            Resources.Loc.Use(value);
            // Task titles and blurbs are computed, so they need a nudge too.
            Notify(nameof(TaskTitle));
            Notify(nameof(TaskBlurb));
            RebuildTaskChips();
            RelabelChoices();
            // Both toggle between two resource strings, so neither is reached by
            // the indexer refresh that covers the XAML bindings.
            Notify(nameof(PlayLabel));
            Notify(nameof(RecordLabel));
        }
    }

    private void RebuildTaskChips()
    {
        for (var i = 0; i < TaskChips.Count; i++)
        {
            TaskChips[i] = TaskChips[i] with { Title = TitleFor(TaskChips[i].Task) };
        }
    }

    /// <summary>Reference voices kept on disk between sessions.</summary>
    public ObservableCollection<SavedVoice> Voices { get; } = [];

    /// <summary>
    /// Whether this family can imitate a reference at all. Asked of the model
    /// rather than assumed: a third of the families declare speaker reference
    /// and the rest would silently ignore one.
    /// </summary>
    public bool SupportsVoiceReference => _model?.SupportsSpeakerReference ?? false;

    public SavedVoice? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (!Set(ref _selectedVoice, value) || value is null) return;
            VoiceAudioPath = value.AudioPath;
            VoiceTranscript = value.Transcript;
            VoiceName = value.Name;
        }
    }

    /// <summary>The clip a cloning family should imitate.</summary>
    public string VoiceAudioPath
    {
        get => _voiceAudioPath;
        set { if (Set(ref _voiceAudioPath, value)) Notify(nameof(VoiceWarning)); }
    }

    /// <summary>What the reference clip says, which cloning families want.</summary>
    public string VoiceTranscript
    {
        get => _voiceTranscript;
        set { if (Set(ref _voiceTranscript, value)) Notify(nameof(VoiceWarning)); }
    }

    public string VoiceName { get => _voiceName; set => Set(ref _voiceName, value); }

    /// <summary>Where the library is stored, so a user can find or back it up.</summary>
    public string VoiceLibraryPath => _voices.StorePath;

    /// <summary>
    /// Warn before the engine does. A reference clip without its transcript is
    /// rejected outright by at least one family, and "requires reference_text
    /// option" is not a message a user can act on without knowing the ABI.
    /// </summary>
    public string VoiceWarning =>
        VoiceAudioPath.Length > 0 && VoiceTranscript.Length == 0
            ? "Add what the reference says — cloning families require it."
            : "";

    public RelayCommand SaveVoiceCommand { get; }
    public RelayCommand DeleteVoiceCommand { get; }

    /// <summary>
    /// Which palette to use. Both were defined as ThemeVariant dictionaries when
    /// the design was adopted, so this is a variant switch rather than a
    /// restyle — the point of having built it that way.
    /// </summary>
    public string Theme
    {
        get => _theme;
        set
        {
            if (!Set(ref _theme, value)) return;
            Notify(nameof(SelectedTheme));
            ApplyTheme();
        }
    }

    /// <summary>
    /// Follow the OS unless told otherwise. Avalonia's Default resolves to
    /// whatever the platform reports, which is what a user expects before they
    /// have expressed a preference.
    /// </summary>
    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is not { } app) return;
        app.RequestedThemeVariant = _theme switch
        {
            "Light" => Avalonia.Styling.ThemeVariant.Light,
            "Dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    /// <summary>Which page the window shows.</summary>
    public string Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Notify(nameof(IsStudio));
            Notify(nameof(IsArena));
            Notify(nameof(IsRuntime));
            Notify(nameof(SelectedPage));
            Notify(nameof(RuntimeSummary));
        }
    }

    public bool IsStudio => _page == "Studio";
    public bool IsArena => _page == "Arena";
    public bool IsRuntime => _page == "Runtime";

    /// <summary>
    /// What the app asked the ABI to do, in order.
    /// </summary>
    /// <remarks>
    /// The status line shows one thing at a time and the last one wins, so a
    /// load that warned before a run that failed leaves no trace of the
    /// warning. For a binding sample the sequence is the interesting part.
    /// </remarks>
    public ObservableCollection<LogEntry> SessionLog { get; } = [];

    /// <summary>Backend, library and models resident right now.</summary>
    public string RuntimeSummary =>
        $"{AbiVersion}\nbackend {Backend}, {Threads} threads\n"
        + $"studio: {(_model is null ? "nothing loaded" : LoadedModelName)}\n"
        + $"arena A: {(Arena.Left.IsLoaded ? "loaded" : "empty")}   "
        + $"arena B: {(Arena.Right.IsLoaded ? "loaded" : "empty")}";

    private static string TimingBreakdownFor(long sessionMs, long runMs) =>
        sessionMs > 0 ? $"session {sessionMs} ms, run {runMs} ms" : $"run {runMs} ms";

    private void Log(string kind, string message)
    {
        SessionLog.Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm:ss"), kind, message));
        // A log that grows without bound is a leak with a nice name.
        while (SessionLog.Count > 300) SessionLog.RemoveAt(SessionLog.Count - 1);
        Notify(nameof(RuntimeSummary));
    }

    /// <summary>Audio streams a run produced, each separately playable and saveable.</summary>
    public ObservableCollection<NamedAudioEntry> OutputStreams { get; } = [];

    /// <summary>Non-audio outputs a run produced — alignments, tokens, whatever a family emits.</summary>
    public ObservableCollection<ArtifactEntry> Artifacts { get; } = [];

    /// <summary>True when the result carries word timings, so subtitles can be exported.</summary>
    public bool HasWordTimings => _words.Count > 0;

    /// <summary>
    /// Where the time went.
    /// </summary>
    /// <remarks>
    /// Measured here, not reported by the engine: the ABI exposes nothing about
    /// timing, so these are this app's own stopwatches around load, session
    /// construction and the run itself. Useful for telling "the model is slow"
    /// apart from "loading it is slow", which are different problems.
    /// </remarks>
    public string TimingBreakdown { get => _timingBreakdown; private set => Set(ref _timingBreakdown, value); }

    /// <summary>Save the transcript as SubRip.</summary>
    public RelayCommand SaveSrtCommand { get; }

    /// <summary>Save the transcript as WebVTT.</summary>
    public RelayCommand SaveVttCommand { get; }

    /// <summary>Show every declared option as a raw grid, not just typed controls.</summary>
    public bool ShowAllOptions
    {
        get => _showAllOptions;
        set => Set(ref _showAllOptions, value);
    }
    public ObservableCollection<ResultRow> Rows { get; } = [];

    public string ModelPath
    {
        get => _modelPath;
        set { if (Set(ref _modelPath, value)) LoadCommand.RaiseCanExecuteChanged(); }
    }

    public string FamilyHint { get => _familyHint; set => Set(ref _familyHint, value); }
    public string Backend
    {
        get => _backend;
        set { if (Set(ref _backend, value)) Notify(nameof(BackendBadge)); }
    }
    public int Threads { get => _threads; set => Set(ref _threads, value); }

    /// <summary>Let the engine segment on speech rather than handing it the whole clip.</summary>
    public bool UseBuiltInChunking
    {
        get => _useBuiltInChunking;
        set { if (Set(ref _useBuiltInChunking, value)) Notify(nameof(ChunkingApplies)); }
    }

    /// <summary>
    /// Whether the chunking controls can do anything: the preset only engages past
    /// <see cref="FullContextCeilingSeconds"/>, and a VAD model set above means this app
    /// segments instead. Without this the window offers a toggle that silently does
    /// nothing on a short clip.
    /// </summary>
    public bool ChunkingApplies => _useBuiltInChunking && !HasVadModel;

    /// <summary>Seconds per chunk. The engine's own default of 2 slices mid-utterance.</summary>
    public double ChunkSeconds { get => _chunkSeconds; set => Set(ref _chunkSeconds, value); }

    /// <summary>
    /// Synthesise long text in pieces and join the result, rather than handing a
    /// family more text than it can take in one request.
    /// </summary>
    public bool SplitLongText { get => _splitLongText; set => Set(ref _splitLongText, value); }

    /// <summary>Characters per piece. Defaults per family, as the reference does.</summary>
    public int ChunkBudget { get => _chunkBudget; set => Set(ref _chunkBudget, value); }

    /// <summary>
    /// The pieces of the last synthesis, kept rather than only the joined clip:
    /// per-segment audio is what a caption or dubbing workflow actually needs.
    /// </summary>
    public IReadOnlyList<AudioSegment> Segments => _segments;

    /// <summary>Directory holding silero_vad_16k.safetensors; blank means go looking.</summary>
    public string VadAssetPath { get => _vadAssetPath; set => Set(ref _vadAssetPath, value); }
    public string Task
    {
        get => _task;
        set
        {
            if (!Set(ref _task, value)) return;
            Notify(nameof(SelectedChip));
            Notify(nameof(TaskTitle));
            Notify(nameof(TaskBlurb));
            Notify(nameof(TaskBadge));
            Notify(nameof(ShowText));
            Notify(nameof(ShowVoice));
            Notify(nameof(ShowTextChunking));
            Notify(nameof(ShowAudioInput));
            Notify(nameof(ShowAsrAudioControls));
            RecordCommand.RaiseCanExecuteChanged();
            FilterCatalog();
        }
    }

    /// <summary>
    /// Narrow the picker to the current task, keeping the selection if it still
    /// applies. Families declare their own task names, which are the same tokens
    /// the session takes.
    /// </summary>
    private void FilterCatalog()
    {
        if (AllEntries.Count == 0) return;

        var keep = _selectedEntry;
        CatalogEntries.Clear();
        foreach (var entry in AllEntries.Where(e => e.SupportsTask(_task)))
        {
            CatalogEntries.Add(entry);
        }

        SelectedEntry = keep is not null && CatalogEntries.Contains(keep) ? keep : null;
        Notify(nameof(CatalogCount));
    }

    public string CatalogCount =>
        $"{CatalogEntries.Count} package(s) for {_task}, {AllEntries.Count} in all.";

    private static string TitleFor(string task) => Resources.Strings.TaskChip(task);

    /// <summary>
    /// The structured result as JSON, which the web UI shows beside the plain
    /// transcript. Hand-built rather than serialised: the rows are already flat
    /// strings, and a serialiser would pull in a dependency for one string.
    /// </summary>
    private string BuildResultJson(List<ResultRow> rows, string transcript)
    {
        var text = new StringBuilder();
        text.Append("{\n  \"text\": ").Append(Quote(transcript)).Append(",\n");
        text.Append("  \"seconds\": ").Append(_lastRunSeconds.ToString("F3",
            System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
        text.Append("  \"rows\": [\n");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            text.Append("    {\"kind\": ").Append(Quote(row.Kind))
                .Append(", \"span\": ").Append(Quote(row.Span))
                .Append(", \"value\": ").Append(Quote(row.Value))
                .Append(", \"detail\": ").Append(Quote(row.Detail)).Append('}');
            if (i < rows.Count - 1) text.Append(',');
            text.Append('\n');
        }
        text.Append("  ]\n}");
        return text.ToString();

        static string Quote(string value)
        {
            var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                               .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
            return $"\"{escaped}\"";
        }
    }

    /// <summary>Headline for the current task, as the web UI's hero block shows it.</summary>
    public string TaskTitle => Resources.Strings.TaskTitle(_task);

    public string TaskBlurb => Resources.Strings.TaskBlurb(_task);

    public string TaskBadge => _task.ToUpperInvariant();

    // Which inputs a task actually takes. The engine does not declare this --
    // the reference UI hardcodes it per task too -- so it lives in one place
    // here rather than being spread through the layout.

    /// <summary>Synthesis needs text; alignment needs the transcript to align.</summary>
    public bool ShowText => _task is "tts" or "align";

    /// <summary>A voice only means something to a family that synthesises one.</summary>
    public bool ShowVoice => _task == "tts";

    /// <summary>Long-text splitting is a property of synthesis, not of audio input.</summary>
    public bool ShowTextChunking => _task == "tts";

    /// <summary>Everything except synthesis reads audio.</summary>
    public bool ShowAudioInput => _task != "tts";

    /// <summary>
    /// Live transcription and long-clip chunking are both ASR concerns. Showing
    /// them for diarisation or separation invites setting a control that is
    /// silently ignored.
    /// </summary>
    public bool ShowAsrAudioControls => _task == "asr";

    /// <summary>Backend in the top-right pill, which reads CUDA once a session is live.</summary>
    public string BackendBadge => _backend.ToUpperInvariant();

    public string LoadedModelName => _model?.Family ?? "No model loaded";

    /// <summary>
    /// What the loaded weights cost on disk.
    /// </summary>
    /// <remarks>
    /// On disk, not in memory: the ABI reports no runtime footprint -- there is
    /// no audiocpp_*_memory anywhere in the header -- so claiming a VRAM figure
    /// would mean inventing one. The file size is what can honestly be shown.
    /// </remarks>
    public string LoadedModelWeights
    {
        get
        {
            if (_model is null) return "";
            var entry = AllEntries.FirstOrDefault(e => e.IsInstalled && e.Family.Family == _model.Family);
            if (entry is { Bytes: > 0 }) return $"{entry.Bytes / 1024.0 / 1024.0:F0} MB on disk";

            try
            {
                if (File.Exists(ModelPath)) return $"{new FileInfo(ModelPath).Length / 1024.0 / 1024.0:F0} MB on disk";
            }
            catch (IOException) { /* a path we cannot stat is not worth reporting */ }
            return "";
        }
    }

    /// <summary>RESIDENT once weights are in memory, matching the web UI's wording.</summary>
    public string LoadedModelState => _model is null ? "NONE" : _session is null ? "AVAILABLE" : "RESIDENT";

    /// <summary>Run status shown after the buttons: Ready, Running…, or a completion time.</summary>
    public string RunState => _busy ? "Running…"
        : _lastRunSeconds > 0 ? $"Complete in {_lastRunSeconds:F2}s."
        : "Ready";

    /// <summary>The structured result, which the web UI shows beside the transcript.</summary>
    public string ResultJson { get => _resultJson; private set => Set(ref _resultJson, value); }
    public string AudioPath { get => _audioPath; set => Set(ref _audioPath, value); }

    /// <summary>
    /// Optional Silero VAD model. When set, ASR runs segment-by-segment instead of
    /// handing the whole clip to the model at once.
    /// </summary>
    public string VadModelPath
    {
        get => _vadModelPath;
        set
        {
            if (!Set(ref _vadModelPath, value)) return;
            Notify(nameof(HasVadModel));
            Notify(nameof(ChunkingApplies));
        }
    }

    /// <summary>The group-span controls only mean anything once a VAD model is set.</summary>
    public bool HasVadModel => _vadModelPath.Length > 0;

    /// <summary>Minimum seconds of speech to accumulate before transcribing a group.</summary>
    public double MinSegmentSpan { get => _minSegmentSpan; set => Set(ref _minSegmentSpan, value); }

    /// <summary>Ceiling on a group, so one unbroken stretch cannot become a huge graph.</summary>
    public double MaxSegmentSpan { get => _maxSegmentSpan; set => Set(ref _maxSegmentSpan, value); }
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
            Notify(nameof(RunState));
            UnloadCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
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
                // Describe(), not exception.Message -- the runtime's own text for a
                // missing native library is a twenty-line list of every path it probed,
                // which fills the status box and says nothing the user can act on.
                return Describe(exception);
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
            // A recording holds a session built from the model about to be freed.
            // The ABI keeps parents alive so this would not crash, but the pump
            // would go on feeding a model the user believes they replaced.
            await StopRecordingAsync();

            await System.Threading.Tasks.Task.Run(() =>
            {
                // Freed in reverse, though the ABI keeps parents alive so order is free.
                _vadSession?.Dispose();
                _vadModel?.Dispose();
                _session?.Dispose();
                _model?.Dispose();
                _registry?.Dispose();
                _vadSession = null;
                _vadModel = null;
                _vadSessionKey = "";
                _session = null;
                _sessionKey = "";

                _registry = AudioCppRegistry.Create();
                _model = _registry.Load(ModelPath,
                    new ModelConfig(FamilyHint.Length > 0 ? FamilyHint : null));
                if (VadModelPath.Length > 0)
                {
                    _vadModel = _registry.Load(VadModelPath, "silero_vad");
                }
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
                        option.Required, option.Description, option.MinValue, option.MaxValue));
                }
            }

            RequestOptions.Clear();
            SessionOptions.Clear();
            foreach (var option in Options)
            {
                (option.Scope == nameof(AudioCppOptionScope.Request)
                    ? RequestOptions : SessionOptions).Add(option);
            }

            for (var i = 0; i < TaskChips.Count; i++)
            {
                var chip = TaskChips[i];
                TaskChips[i] = chip with { Count = supported.Contains(chip.Task) ? 1 : 0 };
            }

            if (supported.Count > 0 && !supported.Contains(Task)) Task = supported[0];
            Notify(nameof(SelectedChip));
            Notify(nameof(LoadedModelName));
            Notify(nameof(LoadedModelState));
            Notify(nameof(LoadedModelWeights));
            Notify(nameof(SupportsVoiceReference));
            UnloadCommand.RaiseCanExecuteChanged();
            ChunkBudget = TextChunker.DefaultBudget(model.Family);
            IsLoaded = true;
            Status = $"Loaded {model.Family} — {Options.Count} declared option(s), read from the model."
                   + (_vadModel is not null ? $"  VAD: {_vadModel.Family}." : "");
            Log("load", $"{model.Family} · {Options.Count} options · {LoadedModelWeights}");
        }
        catch (Exception exception)
        {
            IsLoaded = false;
            ModelSummary = "";
            Options.Clear();
            Fail(exception, "load");
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
        _words.Clear();
        OutputStreams.Clear();
        Artifacts.Clear();
        TimingBreakdown = "";
        _outputSamples = null;
        ResetPlayer();
        _cancel = new CancellationTokenSource();
        CancelCommand.RaiseCanExecuteChanged();
        try
        {
            var started = DateTime.UtcNow;
            var rows = new List<ResultRow>();
            var words = new List<(string Word, long StartSample, long EndSample)>();
            var streams = new List<NamedAudioEntry>();
            var artifacts = new List<ArtifactEntry>();
            var sessionMs = 0L;
            var runMs = 0L;
            string transcript = "";

            await System.Threading.Tasks.Task.Run(() =>
            {
                // Read the audio before the session is built: how long the clip is decides
                // whether the chunking preset applies, and the preset carries session
                // options, which have to be settled before CreateSession.
                (float[] Samples, int SampleRate, int Channels)? clip = null;
                if (Task != "tts")
                {
                    if (AudioPath.Length == 0)
                        throw new InvalidOperationException("Select a WAV file first.");
                    clip = Wav.Read(AudioPath);
                    _inputClip = clip;
                }

                // One session per task/backend/threads combination, reused across runs.
                var sessionOptions = Options
                    .Where(o => o.Scope == nameof(AudioCppOptionScope.Session) && o.Value.Length > 0)
                    .Select(o => new KeyValuePair<string, string>(o.Name, o.Value))
                    .ToList();

                var seconds = clip is { } c && c.SampleRate > 0
                    ? (double)c.Samples.Length / c.Channels / c.SampleRate
                    : 0;
                var chunking = ChunkingPreset(seconds);
                foreach (var option in chunking.Session)
                {
                    if (!sessionOptions.Any(o => o.Key == option.Key)) sessionOptions.Add(option);
                }

                // Option values are part of the session's identity: a session is built
                // with them, so changing one has to rebuild it or the edit silently
                // does nothing.
                var key = $"{Task}|{Backend}|{Threads}|"
                        + string.Join(";", sessionOptions.Select(o => $"{o.Key}={o.Value}"));
                var sessionStarted = System.Diagnostics.Stopwatch.StartNew();
                if (_session is null || _sessionKey != key)
                {
                    _session?.Dispose();
                    _session = _model!.CreateSession(
                        Task, "offline", new BackendConfig(Backend, 0, Threads),
                        sessionOptions.Count > 0 ? sessionOptions : null);
                    _sessionKey = key;
                }
                sessionMs = sessionStarted.ElapsedMilliseconds;

                using var request = new AudioCppRequest();
                foreach (var option in chunking.Request)
                {
                    request.SetOption(option.Key, option.Value);
                }
                foreach (var option in Options.Where(o =>
                             o.Scope == nameof(AudioCppOptionScope.Request) && o.Value.Length > 0))
                {
                    request.SetOption(option.Name, option.Value);   // an explicit edit wins
                }
                if (Task == "tts")
                {
                    // Long text goes piece by piece against one session; handing a
                    // family more than it can take in a request either truncates or
                    // throws, depending on the family.
                    var pieces = SplitLongText
                        ? TextChunker.Split(Text, Math.Max(1, ChunkBudget))
                        : [Text];

                    if (pieces.Count > 1)
                    {
                        transcript = RunLongText(pieces, rows);
                        return;
                    }

                    request.SetText(Text, "en-us");
                    if (VoiceId.Length > 0) request.SetVoiceId(VoiceId);
                    ApplyVoiceReference(request);
                }
                else
                {
                    var input = clip!.Value;
                    if (_vadModel is not null)
                    {
                        // Segment first, transcribe each stretch of speech separately.
                        // Handing a long recording to the model whole means one encoder
                        // graph over the entire clip, which is both the slower path and
                        // the one that runs out of memory on anything lengthy.
                        transcript = RunSegmented(input, rows);
                        return;
                    }
                    request.SetAudio(input.Samples, input.SampleRate, input.Channels);

                    // Forced alignment needs both: the audio and the transcript to
                    // align against it. Showing a text box whose value was never
                    // sent is the same defect as showing a control a task ignores.
                    if (Task == "align" && Text.Length > 0) request.SetText(Text, "en-us");
                }

                var runStarted = System.Diagnostics.Stopwatch.StartNew();
                using var result = _session!.Run(request);
                runMs = runStarted.ElapsedMilliseconds;

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
                        segment.Text, segment.Confidence.ToString("F3"),
                        segment.StartSample, segment.EndSample));
                }
                foreach (var turn in result.SpeakerTurns)
                {
                    rows.Add(new ResultRow("speaker", Span(turn.StartSample, turn.EndSample),
                        turn.SpeakerId, turn.Confidence.ToString("F3"),
                        turn.StartSample, turn.EndSample));
                }
                foreach (var word in result.Words)
                {
                    rows.Add(new ResultRow("word", Span(word.StartSample, word.EndSample),
                        word.Word, word.Confidence.ToString("F3"),
                        word.StartSample, word.EndSample));
                    words.Add((word.Word, word.StartSample, word.EndSample));
                }
                foreach (var stream in result.NamedAudio)
                {
                    rows.Add(new ResultRow("stream", $"{stream.Duration:F2}s", stream.Id,
                        $"{stream.SampleRate} Hz"));
                    streams.Add(new NamedAudioEntry(
                        stream.Id, stream.Samples, stream.SampleRate, stream.Channels));
                }
                foreach (var artifact in result.Artifacts)
                {
                    rows.Add(new ResultRow("artifact", $"{artifact.Payload.Length} B",
                        artifact.Id, artifact.Kind.ToString()));
                    artifacts.Add(new ArtifactEntry(
                        artifact.Id, artifact.Kind.ToString(), artifact.Payload,
                        string.Join(", ", artifact.Metadata.Select(m => $"{m.Key}={m.Value}"))));
                }
            });

            _lastRunSeconds = (DateTime.UtcNow - started).TotalSeconds;

            _words.Clear();
            _words.AddRange(words);
            OutputStreams.Clear();
            foreach (var stream in streams) OutputStreams.Add(stream);
            Artifacts.Clear();
            foreach (var artifact in artifacts) Artifacts.Add(artifact);

            Log("run", $"{Task} · {TimingBreakdownFor(sessionMs, runMs)} · "
                       + $"{rows.Count} row(s), {streams.Count} stream(s)");

            TimingBreakdown = sessionMs > 0
                ? $"session {sessionMs} ms · run {runMs} ms · total {_lastRunSeconds * 1000:F0} ms"
                : $"run {runMs} ms · total {_lastRunSeconds * 1000:F0} ms";

            Notify(nameof(HasWordTimings));
            SaveSrtCommand.RaiseCanExecuteChanged();
            SaveVttCommand.RaiseCanExecuteChanged();
            Notify(nameof(RunState));
            Notify(nameof(HasPreviewAudio));
            PlayCommand.RaiseCanExecuteChanged();
            ResultJson = BuildResultJson(rows, transcript);
            Transcript = transcript;
            foreach (var row in rows) Rows.Add(row);
            Notify(nameof(OutputSamples));
            Notify(nameof(OutputSummary));
            Notify(nameof(Timing));
            Status = rows.Count == 0 && transcript.Length == 0 && _outputSamples is null
                ? "Ran, but the family produced no output for this input."
                : _segmentedSummary.Length > 0
                    ? $"{_segmentedSummary}  Done in {_lastRunSeconds * 1000:F0} ms."
                    : $"Done in {_lastRunSeconds * 1000:F0} ms.";
        }
        catch (Exception exception)
        {
            Fail(exception, "run");
        }
        finally
        {
            Busy = false;
            SaveWavCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Synthesise each piece of a long text against the one session and join the
    /// results. Segments are kept individually as well as joined -- a caption or
    /// dubbing workflow wants the pieces, not only the merged clip.
    /// </summary>
    private string RunLongText(List<string> pieces, List<ResultRow> rows)
    {
        _segments.Clear();
        var cancelled = false;

        for (var index = 0; index < pieces.Count; index++)
        {
            if (_cancel?.IsCancellationRequested == true) { cancelled = true; break; }

            var piece = pieces[index];
            using var request = new AudioCppRequest();
            foreach (var option in Options.Where(o =>
                         o.Scope == nameof(AudioCppOptionScope.Request) && o.Value.Length > 0))
            {
                request.SetOption(option.Name, option.Value);
            }
            request.SetText(piece, "en-us");
            if (VoiceId.Length > 0) request.SetVoiceId(VoiceId);
            // Every piece needs the reference, not just the first: each is its
            // own request, and a piece without one comes back in a different
            // voice from its neighbours.
            ApplyVoiceReference(request);

            using var result = _session!.Run(request);
            if (result.Audio is not { } audio) continue;

            var segment = new AudioSegment(
                index + 1, piece, audio.Samples, audio.SampleRate, audio.Channels);
            _segments.Add(segment);
            rows.Add(new ResultRow(
                "segment",
                $"{segment.Seconds:F2}s",
                piece.Length > 80 ? piece[..80] + "…" : piece,
                $"{segment.Samples.Length} samples"));
        }

        if (_segments.Count == 0)
        {
            _segmentedSummary = "Synthesis produced no audio.";
            return "";
        }

        var (samples, rate, channels) = AudioJoin.Concatenate(_segments);
        _outputSamples = samples;
        _outputSampleRate = rate;
        _outputChannels = channels;

        var seconds = _segments.Sum(seg => seg.Seconds);
        _segmentedSummary = $"{_segments.Count} of {pieces.Count} piece(s), {seconds:F1}s"
                          + (cancelled ? ".  Cancelled part-way." : ".");
        return string.Join(" ", _segments.Select(seg => seg.Text));
    }

    /// <summary>
    /// Silero VAD, then ASR over each group of speech. Groups accumulate adjacent
    /// segments until they clear <see cref="MinSegmentSpan"/> -- a couple of words on
    /// their own transcribe badly, the model wants context -- and are split at
    /// <see cref="MaxSegmentSpan"/> so one unbroken stretch cannot build a huge graph.
    /// Word timings come back relative to each window and are shifted to absolute here.
    /// </summary>
    private string RunSegmented(
        (float[] Samples, int SampleRate, int Channels) clip,
        List<ResultRow> rows)
    {
        if (clip.Channels != 1)
            throw new InvalidOperationException($"VAD needs mono audio, got {clip.Channels} channels.");

        _segmentedSummary = "";
        var rate = clip.SampleRate;
        var vadKey = $"{Backend}|{Threads}";
        if (_vadSession is null || _vadSessionKey != vadKey)
        {
            _vadSession?.Dispose();
            _vadSession = _vadModel!.CreateSession("vad", "offline", new BackendConfig(Backend, 0, Threads));
            _vadSessionKey = vadKey;
        }

        List<(long Start, long End)> segments;
        using (var vadRequest = new AudioCppRequest())
        {
            vadRequest.SetAudio(clip.Samples, rate, 1);
            using var vadResult = _vadSession!.Run(vadRequest);
            segments = vadResult.Segments.Select(s => (s.StartSample, s.EndSample)).ToList();
        }

        var groups = GroupSegments(segments, rate, MinSegmentSpan, MaxSegmentSpan, clip.Samples.Length);
        if (groups.Count == 0)
        {
            _segmentedSummary = "VAD found no speech in this file.";
            return "";
        }

        var pieces = new List<string>();
        var cancelled = false;
        foreach (var (start, end) in groups)
        {
            if (_cancel?.IsCancellationRequested == true) { cancelled = true; break; }

            var window = new float[end - start];
            Array.Copy(clip.Samples, (int)start, window, 0, window.Length);

            using var request = new AudioCppRequest();
            request.SetAudio(window, rate, 1);
            foreach (var option in Options.Where(o =>
                         o.Scope == nameof(AudioCppOptionScope.Request) && o.Value.Length > 0))
            {
                request.SetOption(option.Name, option.Value);
            }
            using var result = _session!.Run(request);

            if (result.Text is { } text && text.Text.Length > 0) pieces.Add(text.Text.Trim());
            foreach (var word in result.Words)
            {
                rows.Add(new ResultRow(
                    "word",
                    $"{(start + word.StartSample) / (double)rate:F2}–{(start + word.EndSample) / (double)rate:F2}s",
                    word.Word,
                    word.Confidence.ToString("F3")));
            }
        }

        var speech = groups.Sum(g => (g.End - g.Start)) / (double)rate;
        _segmentedSummary = $"{groups.Count} group(s) from {segments.Count} VAD segment(s), "
                          + $"{speech:F1}s of speech."
                          + (cancelled ? "  Cancelled part-way." : "");
        return string.Join(" ", pieces);
    }

    private static List<(long Start, long End)> GroupSegments(
        List<(long Start, long End)> segments, int rate, double minSpan, double maxSpan, int totalSamples)
    {
        var groups = new List<(long Start, long End)>();
        if (segments.Count == 0) return groups;

        var limit = (long)(maxSpan * rate);
        void Add(long start, long end)
        {
            end = Math.Min(end, totalSamples);
            for (var s = start; s < end; s += limit) groups.Add((s, Math.Min(s + limit, end)));
        }

        var (gs, ge) = segments[0];
        for (var i = 1; i < segments.Count; i++)
        {
            var span = (ge - gs) / (double)rate;
            var wouldBe = (segments[i].End - gs) / (double)rate;
            if (span >= minSpan || wouldBe > maxSpan)
            {
                Add(gs, ge);
                (gs, ge) = segments[i];
            }
            else ge = segments[i].End;
        }
        Add(gs, ge);
        return groups;
    }

    /// <summary>
    /// Default models root. Follows the platform's application-data convention
    /// rather than writing beside the executable, which is often read-only when
    /// an app is installed properly.
    /// </summary>
    private static string DefaultModelsRoot()
    {
        var configured = Environment.GetEnvironmentVariable("AUDIOCPP_MODELS_ROOT");
        if (configured is { Length: > 0 }) return configured;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "audiocpp", "models");
    }

    /// <summary>
    /// Find audio.cpp's model_specs. Same resolution problem as the native
    /// library: an explicit setting, else a checkout beside this one.
    /// </summary>
    private static string? FindModelSpecs()
    {
        var configured = Environment.GetEnvironmentVariable("AUDIOCPP_MODEL_SPECS");
        if (configured is { Length: > 0 } && Directory.Exists(configured)) return configured;

        var roots = new List<string>();
        var native = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        for (var dir = native is { Length: > 0 } ? new DirectoryInfo(native) : null;
             dir is not null; dir = dir.Parent)
        {
            roots.Add(Path.Combine(dir.FullName, "model_specs"));
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            roots.Add(Path.Combine(dir.FullName, "audio.cpp", "model_specs"));
            if (dir.Parent is not null)
                roots.Add(Path.Combine(dir.Parent.FullName, "audio.cpp", "model_specs"));
        }
        return roots.FirstOrDefault(Directory.Exists);
    }

    private void LoadCatalog()
    {
        var specs = FindModelSpecs();
        if (specs is null)
        {
            InstallStatus = "No model_specs found; the package list is unavailable. "
                          + "Set AUDIOCPP_MODEL_SPECS to enable it.";
            return;
        }

        var catalog = Catalog.Load(specs);
        foreach (var family in catalog.Families.OrderBy(f => f.DisplayName, StringComparer.Ordinal))
        {
            foreach (var package in family.Packages)
            {
                AllEntries.Add(new CatalogEntry(family, package));
            }
        }

        RefreshCatalogState();
        FilterCatalog();
        InstallStatus = catalog.Unreadable.Count == 0
            ? $"{AllEntries.Count} packages across {catalog.Families.Count} families."
            : $"{AllEntries.Count} packages; {catalog.Unreadable.Count} spec(s) unreadable.";
    }

    /// <summary>
    /// Re-read what is on disk. Local only -- no network -- so it stays instant
    /// and works offline; download sizes are probed on demand instead.
    /// </summary>
    private void RefreshCatalogState()
    {
        foreach (var entry in AllEntries)
        {
            entry.State = PackageInstaller.StateOf(ModelsRoot, entry.Package);
            if (entry.IsInstalled)
            {
                entry.Bytes = PackageInstaller.BytesOnDisk(ModelsRoot, entry.Package);
            }
        }
        Notify(nameof(SelectedSummary));
    }

    private async Task InstallAsync()
    {
        if (_selectedEntry is not { } entry) return;

        Busy = true;
        InstallFraction = 0;
        _cancel = new CancellationTokenSource();
        CancelCommand.RaiseCanExecuteChanged();
        try
        {
            var probe = await _installer.ProbeAsync(entry.Package, _cancel.Token);
            if (!probe.Available)
            {
                // The spec declares packages the repository does not serve. Say so
                // plainly rather than failing part-way through a download.
                entry.Note = $"Unavailable: {probe.Problem}";
                InstallStatus = entry.Note;
                Notify(nameof(SelectedSummary));
                return;
            }

            entry.Bytes = probe.Bytes;
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallFraction = p.Fraction;
                InstallStatus = $"{p.File}  ({p.FileIndex}/{p.FileCount})  "
                              + $"{p.BytesDone / 1024.0 / 1024.0:F0} of "
                              + $"{p.BytesTotal / 1024.0 / 1024.0:F0} MB";
            });

            await _installer.InstallAsync(ModelsRoot, entry.Package, progress, _cancel.Token);

            entry.State = PackageInstaller.StateOf(ModelsRoot, entry.Package);
            entry.Bytes = PackageInstaller.BytesOnDisk(ModelsRoot, entry.Package);
            ModelPath = entry.ResolvePath(ModelsRoot);
            InstallStatus = $"Installed {entry.Title}.";
        }
        catch (OperationCanceledException)
        {
            // Leave the partials: CleanPartials is the user's call, and a resumed
            // install would otherwise start from zero.
            entry.State = PackageInstaller.StateOf(ModelsRoot, entry.Package);
            InstallStatus = "Install cancelled.";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or InvalidOperationException or IOException)
        {
            entry.State = PackageInstaller.StateOf(ModelsRoot, entry.Package);
            InstallStatus = $"Install failed: {exception.Message}";
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            InstallFraction = 0;
            Busy = false;
            InstallCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            Notify(nameof(SelectedSummary));
        }
    }

    private Task DeleteAsync()
    {
        if (_selectedEntry is not { } entry) return System.Threading.Tasks.Task.CompletedTask;

        try
        {
            PackageInstaller.Delete(ModelsRoot, entry.Package);
            entry.State = PackageInstaller.StateOf(ModelsRoot, entry.Package);
            entry.Bytes = 0;
            InstallStatus = $"Deleted {entry.Title}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            InstallStatus = $"Delete failed: {exception.Message}";
        }
        InstallCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        Notify(nameof(SelectedSummary));
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Stop and release a live recording if one is running. Safe to call when
    /// none is, and used both by the Record button and on shutdown -- without
    /// the latter the microphone stays open until the process exits.
    /// </summary>
    /// <summary>
    /// Free the model, its sessions and the registry.
    /// </summary>
    /// <remarks>
    /// Loading a different model already frees the previous one, but only at the
    /// moment the next load succeeds. Without an explicit unload there is no way
    /// to give back a 3 GB model except by closing the app, which matters on a
    /// machine where something else wants the GPU.
    /// </remarks>
    private async Task UnloadAsync()
    {
        await StopAudioAsync();

        await System.Threading.Tasks.Task.Run(() =>
        {
            _vadSession?.Dispose();
            _vadModel?.Dispose();
            _session?.Dispose();
            _model?.Dispose();
            _registry?.Dispose();
            _vadSession = null;
            _vadModel = null;
            _vadSessionKey = "";
            _session = null;
            _sessionKey = "";
            _model = null;
            _registry = null;
        });

        Options.Clear();
        RequestOptions.Clear();
        SessionOptions.Clear();
        Rows.Clear();
        Transcript = "";
        ResultJson = "";
        ModelSummary = "";
        // Output belongs to a run and goes with the model. The input clip is the
        // user's own file and does not, so it survives an unload and stays
        // previewable.
        _outputSamples = null;
        IsLoaded = false;

        for (var i = 0; i < TaskChips.Count; i++) TaskChips[i] = TaskChips[i] with { Count = 0 };

        Notify(nameof(LoadedModelName));
        Notify(nameof(LoadedModelState));
        Notify(nameof(LoadedModelWeights));
        Notify(nameof(HasPreviewAudio));
        UnloadCommand.RaiseCanExecuteChanged();
        RunCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        Status = "Unloaded. The model and its sessions are freed.";
        Log("unload", "model, sessions and registry freed");
    }

    /// <summary>Release audio devices held for preview and capture.</summary>
    public async Task StopAudioAsync()
    {
        ResetPlayer();
        Arena.Dispose();
        await StopRecordingAsync();
    }

    public async Task StopRecordingAsync()
    {
        if (_live is null) return;

        var live = _live;
        _live = null;
        try { await live.StopAsync(); }
        catch (AudioCppException) { /* nothing to finish */ }
        live.Dispose();

        IsRecording = false;
        InputLevel = 0;
    }

    /// <summary>
    /// The clip currently loaded for input, kept so it can be previewed without
    /// reading the file again.
    /// </summary>
    private (float[] Samples, int SampleRate, int Channels)? _inputClip;

    private async Task TogglePlaybackAsync()
    {
        if (_player is { IsPlaying: true })
        {
            _player.Pause();
            StopPlayTimer();
            Notify(nameof(PlayLabel));
            return;
        }

        if (_player is null && !TryOpenPlayer()) return;
        if (_player is null) return;

        _player.Play();
        StartPlayTimer();
        Notify(nameof(PlayLabel));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private bool TryOpenPlayer()
    {
        var clip = _outputSamples is { Length: > 0 }
            ? (_outputSamples, _outputSampleRate, _outputChannels)
            : _inputClip;
        if (clip is null) return false;

        try
        {
            _player = AudioPlayer.Open(clip.Value.Samples, clip.Value.SampleRate, clip.Value.Channels);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or DllNotFoundException or ArgumentException)
        {
            PlayStatus = Describe(exception);
            return false;
        }
    }

    /// <summary>
    /// The playhead is driven from a UI timer rather than a callback: position
    /// lives on the audio thread, and 20 fps is enough for a cursor while
    /// costing nothing.
    /// </summary>
    private void StartPlayTimer()
    {
        _playTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playTimer.Tick -= OnPlayTick;
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
    }

    private void StopPlayTimer() => _playTimer?.Stop();

    private void OnPlayTick(object? sender, EventArgs e)
    {
        UpdatePlayProgress();
        if (_player?.IsPlaying != true)
        {
            StopPlayTimer();
            Notify(nameof(PlayLabel));
        }
    }

    private void UpdatePlayProgress()
    {
        if (_player is null) { PlayProgress = -1; PlayStatus = ""; return; }
        var length = _player.LengthSeconds;
        PlayProgress = length > 0 ? _player.PositionSeconds / length : -1;
        PlayStatus = $"{_player.PositionSeconds:F1} / {length:F1}s";
    }

    /// <summary>
    /// Drop the player so the next preview picks up whatever is current. Called
    /// when the audio underneath changes, which is the only time a stale player
    /// would otherwise keep playing the previous clip.
    /// </summary>
    private void ResetPlayer()
    {
        StopPlayTimer();
        _player?.Dispose();
        _player = null;
        PlayProgress = -1;
        PlayStatus = "";
        Notify(nameof(PlayLabel));
        Notify(nameof(HasPreviewAudio));
        PlayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Attach the reference clip and its transcript, when the family can use
    /// one. Sending a reference to a family that cannot is not an error the
    /// engine reports — it is simply ignored — so it is gated here.
    /// </summary>
    private void ApplyVoiceReference(AudioCppRequest request)
    {
        if (!SupportsVoiceReference || VoiceAudioPath.Length == 0) return;

        try
        {
            var reference = Wav.Read(VoiceAudioPath);
            request.SetVoiceAudio(reference.Samples, reference.SampleRate, reference.Channels);

            // A request option, not a style tag. audio8_tts rejects inline
            // reference audio without it outright -- "requires reference_text
            // option" -- so a clip alone is not a usable reference.
            if (VoiceTranscript.Length > 0) request.SetOption("reference_text", VoiceTranscript);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Status = $"Reference voice: {exception.Message}";
        }
    }

    private Task SaveVoiceAsync()
    {
        var voice = new SavedVoice(VoiceName, VoiceAudioPath, VoiceTranscript);
        var existing = Voices.FirstOrDefault(v => v.Name == voice.Name);
        if (existing is not null) Voices[Voices.IndexOf(existing)] = voice;
        else Voices.Add(voice);

        try
        {
            _voices.Save(Voices.Where(v => !v.Name.StartsWith("demo · ", StringComparison.Ordinal)));
            Status = $"Saved voice '{voice.Name}'.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = Describe(exception);
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private Task DeleteVoiceAsync()
    {
        if (_selectedVoice is null) return System.Threading.Tasks.Task.CompletedTask;
        Voices.Remove(_selectedVoice);
        SelectedVoice = null;
        try
        {
            _voices.Save(Voices.Where(v => !v.Name.StartsWith("demo · ", StringComparison.Ordinal)));
            Status = "Voice removed.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = Describe(exception);
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void LoadCaptureDevices()
    {
        try
        {
            CaptureDevices.Clear();
            foreach (var device in AudioCapture.Devices()) CaptureDevices.Add(device);
            CaptureDevice = CaptureDevices.FirstOrDefault(d => d.IsDefault);
            LiveStatus = $"{CaptureDevices.Count} input(s) via {AudioCapture.Backend}";
        }
        catch (DllNotFoundException)
        {
            // Capture is optional: the rest of the app works without it.
            LiveStatus = "audio capture unavailable (build native/audioio)";
        }
    }

    /// <summary>
    /// Toggle live transcription.
    /// </summary>
    /// <remarks>
    /// The pump runs on its own thread and reports through callbacks, so every
    /// one of them hops to the UI thread before touching bound state. Avalonia
    /// will not always throw when this is got wrong -- it sometimes just stops
    /// updating -- which is why it is done explicitly rather than by habit.
    /// </remarks>
    private async Task ToggleRecordingAsync()
    {
        // Stopping awaits the pump, and the button stays live during that await.
        // A second press would otherwise stop the same session twice.
        if (_togglingRecording) return;
        _togglingRecording = true;
        try
        {
            await ToggleRecordingCoreAsync();
        }
        finally
        {
            _togglingRecording = false;
            RecordCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ToggleRecordingCoreAsync()
    {
        if (_live is not null)
        {
            var live = _live;
            _live = null;
            var final = "";
            try { final = await live.StopAsync(); }
            finally { live.Dispose(); }
            IsRecording = false;
            InputLevel = 0;

            if (final.Length > 0) Transcript = final;
            Status = final.Length > 0
                ? $"Recorded {final.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words."
                : "Stopped; nothing was transcribed.";
            return;
        }

        if (_model is null) { Status = "Load a model first."; return; }

        try
        {
            Transcript = "";
            Rows.Clear();

            _live = LiveTranscription.Start(_model, Backend, Threads, CaptureDevice);
            _live.OnText = text => Dispatcher.UIThread.Post(() => Transcript = text);
            _live.OnLevel = (peak, dropped) => Dispatcher.UIThread.Post(() =>
            {
                InputLevel = peak;
                if (dropped > 0) LiveStatus = $"dropped {dropped} frames — the UI is not draining fast enough";
            });
            _live.OnError = error => Dispatcher.UIThread.Post(() =>
            {
                Status = Describe(error);
                _ = ToggleRecordingAsync();
            });

            IsRecording = true;
            Status = $"Recording from {CaptureDevice?.Name ?? "the default input"}…";
        }
        catch (Exception exception) when (exception is AudioCppException
                                          or InvalidOperationException or DllNotFoundException)
        {
            _live?.Dispose();
            _live = null;
            IsRecording = false;
            Status = Describe(exception);
        }
    }

    private Task CancelAsync()
    {
        _cancel?.Cancel();
        Status = "Cancelling after the current segment…";
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Write the transcript as subtitles, grouped from word timings.
    /// </summary>
    /// <remarks>
    /// The sample rate is the input's, not the output's: word offsets are
    /// positions in the audio that was transcribed, and using an output rate
    /// here would shift every cue.
    /// </remarks>
    private async Task SaveSubtitlesAsync(string format)
    {
        if (_words.Count == 0 || PickSavePath is null) return;

        var path = await PickSavePath($"transcript.{format}");
        if (path is null) return;

        try
        {
            var rate = _inputClip?.SampleRate ?? _resultSampleRate;
            var cues = Subtitles.Group(_words, rate);
            var text = format == "vtt" ? Subtitles.ToVtt(cues) : Subtitles.ToSrt(cues);
            await File.WriteAllTextAsync(path, text);
            Status = $"Wrote {cues.Count} cue(s) to {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = Describe(exception);
        }
    }

    /// <summary>
    /// The subtitle text a save would write. Exposed so the grouping can be
    /// checked without driving a file dialog.
    /// </summary>
    public string SubtitlePreview(string format)
    {
        var rate = _inputClip?.SampleRate ?? _resultSampleRate;
        var cues = Subtitles.Group(_words, rate);
        return format == "vtt" ? Subtitles.ToVtt(cues) : Subtitles.ToSrt(cues);
    }

    /// <summary>Write one of a run's named streams, for a separation result's stems.</summary>
    public async Task SaveStreamAsync(NamedAudioEntry stream)
    {
        if (PickSavePath is null) return;
        var path = await PickSavePath($"{stream.Id}.wav");
        if (path is null) return;

        try
        {
            Wav.Write(path, stream.Samples, stream.SampleRate, stream.Channels);
            Status = $"Wrote {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = Describe(exception);
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
    /// <summary>
    /// Report a failure to the status line and the log.
    /// </summary>
    /// <remarks>
    /// A failure is the case the log exists for: the status line keeps only the
    /// last thing that happened, so a load that failed before a later success
    /// would otherwise vanish entirely.
    /// </remarks>
    private void Fail(Exception exception, string kind)
    {
        var message = Describe(exception);
        Status = message;
        Log(kind, message);
    }

    private static string Describe(Exception exception) => exception switch
    {
        AudioCppException native => $"{native.Operation} failed: {native.Detail} [{native.Status}]",
        DllNotFoundException => "libaudiocpp was not found. Set AUDIOCPP_NATIVE_DIR to the "
                               + "directory holding it (an audio.cpp build's bin/), or copy it "
                               + "beside this executable, then restart.",
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

    /// <summary>
    /// Clip length past which the engine's own path cannot cope, so the preset takes over.
    /// </summary>
    /// <remarks>
    /// The encoder builds a relative positional encoding sized by max_position_embeddings,
    /// 5000 frames in the shipped Parakeet config, and throws outright above it. One
    /// encoder frame is 80ms after the FastConformer's 8x subsampling at a 10ms hop, so
    /// the wall is 400s; 300s leaves room for a checkpoint configured differently.
    ///
    /// Under it, leave the engine alone -- it already switches to bounded windows past
    /// audio_chunk_threshold_sec. Measured on a 90s clip, the untouched path transcribed
    /// 236 words in 1.4s and the preset 226 in 1.9s, so applying it everywhere would buy
    /// nothing and cost accuracy on exactly the clips that never needed it.
    /// </remarks>
    private const double FullContextCeilingSeconds = 300;

    /// <summary>
    /// Options that put the engine's own VAD chunking in charge of a long recording.
    /// </summary>
    /// <remarks>
    /// Measured on a 10-minute clip, CUDA: 5.7s and 1483 words, against 35s for
    /// segmenting in this app and calling ASR per group, and an outright failure for the
    /// engine's untouched defaults.
    ///
    /// audio_chunk_duration_sec is set because the engine's own default of 2s slices
    /// mid-utterance badly enough to lose words.
    ///
    /// offline_mode=long_form is load-bearing and not obviously so. The engine sizes the
    /// encoder graph in prepare(), which consults offline_mode but not audio_chunk_mode,
    /// so asking for vad chunking alone still sizes the graph for the whole recording and
    /// throws on anything past the full-context ceiling. long_form makes prepare() agree
    /// with the path the run will take.
    ///
    /// Hardcoded per family rather than discovered, which is the exception to how the
    /// rest of this window works. The shipped Parakeet GGUF embeds a model spec predating
    /// these controls, so it advertises neither them nor offline_mode — the options
    /// function, they are simply absent from what the model says about itself. Drop this
    /// once the package is regenerated and the Options grid will carry them on its own.
    /// </remarks>
    private (List<KeyValuePair<string, string>> Session, List<KeyValuePair<string, string>> Request)
        ChunkingPreset(double seconds)
    {
        var session = new List<KeyValuePair<string, string>>();
        var request = new List<KeyValuePair<string, string>>();

        // Only for audio tasks, only when this app is not segmenting already, and only
        // for a family known to implement it.
        if (Task == "tts" || _vadModel is not null || !UseBuiltInChunking) return (session, request);
        if (_model?.Family != "parakeet_tdt") return (session, request);
        if (seconds < FullContextCeilingSeconds) return (session, request);

        session.Add(new("parakeet_tdt.offline_mode", "long_form"));

        // vad chunking cuts between utterances; fixed cuts on a timer and loses words at
        // every boundary. Measured on the same 10-minute clip, vad transcribed 1483 words
        // and fixed 1372, dropping whole clauses. So prefer vad, but it needs a Silero
        // checkpoint on disk that the engine looks for relative to the process's working
        // directory -- fall back rather than fail when this app is not launched from
        // beside one.
        var vadAsset = ResolveVadAsset();
        if (vadAsset is null)
        {
            request.Add(new("audio_chunk_mode", "fixed"));
        }
        else
        {
            request.Add(new("audio_chunk_mode", "vad"));
            session.Add(new("parakeet_tdt.vad_model_path", vadAsset));
        }

        request.Add(new("audio_chunk_duration_sec", ChunkSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
        return (session, request);
    }

    /// <summary>
    /// Locate the Silero checkpoint the engine's vad chunker loads, or null if this
    /// machine has no copy where we can find one.
    /// </summary>
    /// <remarks>
    /// The engine defaults to the relative path assets/framework/models/silero_vad, which
    /// resolves only when the process runs from an audio.cpp checkout. VadAssetPath lets
    /// the window say where it really is; otherwise look beside the native library, since
    /// a build tree sits next to the assets directory it was built from.
    /// </remarks>
    private string? ResolveVadAsset()
    {
        static string? Check(string? directory) =>
            directory is { Length: > 0 }
            && File.Exists(Path.Combine(directory, "silero_vad_16k.safetensors"))
                ? directory : null;

        if (Check(VadAssetPath) is { } explicitPath) return explicitPath;

        var relative = Path.Combine("assets", "framework", "models", "silero_vad");
        var roots = new List<string> { Directory.GetCurrentDirectory() };

        var native = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        for (var dir = native is { Length: > 0 } ? new DirectoryInfo(native) : null;
             dir is not null; dir = dir.Parent)
        {
            roots.Add(dir.FullName);
        }

        // Also look for a sibling audio.cpp checkout, as the native library
        // lookup does. Without this the app starts fine without any environment
        // set but quietly falls back to fixed chunking, which is worse: the
        // difference is a silent 111 words on a 10-minute clip, not an error.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            roots.Add(Path.Combine(dir.FullName, "audio.cpp"));
        }

        return roots.Select(root => Check(Path.Combine(root, relative)))
                    .FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// Physical cores, which is what ggml wants. Environment.ProcessorCount reports
    /// logical CPUs, and running one thread per SMT sibling measured 1.8x SLOWER than
    /// one per core on an 8-core/16-thread machine -- the siblings contend for the same
    /// execution units and caches while the per-graph-node barriers spin.
    /// </summary>
    private static int PhysicalCoreCount()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                var seen = new HashSet<string>();
                string physical = "", core = "";
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("physical id")) physical = line;
                    else if (line.StartsWith("core id")) core = line;
                    else if (line.Length == 0 && core.Length > 0) { seen.Add(physical + core); physical = core = ""; }
                }
                if (core.Length > 0) seen.Add(physical + core);
                if (seen.Count > 0) return seen.Count;
            }
        }
        catch (IOException)
        {
            // Unreadable cpuinfo is not worth failing over; fall through.
        }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One declared option, as the model describes itself at runtime, plus whatever the
/// user has typed for it. <see cref="Value"/> is the only mutable part: leave it empty
/// and the model's own default applies.
/// </summary>
public sealed class DeclaredOption(
    string scope, string name, string type, string @default, string range, bool required,
    string description = "", string min = "", string max = "")
    : INotifyPropertyChanged
{
    private string _value = "";

    public string Scope { get; } = scope;
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string Default { get; } = @default;
    public string Range { get; } = range;
    public bool Required { get; } = required;

    /// <summary>What the model says the option is for; shown as a tooltip.</summary>
    public string Description { get; } = description;

    public string Min { get; } = min;
    public string Max { get; } = max;

    /// <summary>
    /// Which editor suits this option, worked out from what the model declares
    /// rather than from a curated list.
    /// </summary>
    /// <remarks>
    /// audio.cpp ships webui/configs/model_params.json with hand-written
    /// controls, but it is TTS-specific by its own description, covers 48 of the
    /// 77 families, carries part-Chinese labels, and has no entry at all for
    /// parakeet_tdt -- the ASR model this app is most used with. The ABI already
    /// reports type, default, min, max and a description for every option of
    /// every family, so the editors are derived from that instead. It works
    /// everywhere and cannot drift from the engine.
    /// </remarks>
    public OptionEditor Editor => Type.Contains('|') ? OptionEditor.Choice
        : Type is "bool" ? OptionEditor.Toggle
        : Type is "int" or "float" or "number"
            ? (Min.Length > 0 && Max.Length > 0 ? OptionEditor.Slider : OptionEditor.Number)
            : OptionEditor.Text;

    public bool IsChoice => Editor == OptionEditor.Choice;
    public bool IsToggle => Editor == OptionEditor.Toggle;
    public bool IsSlider => Editor == OptionEditor.Slider;
    public bool IsNumber => Editor == OptionEditor.Number;
    public bool IsText   => Editor == OptionEditor.Text;

    /// <summary>The alternatives for a choice option, from its pipe-separated type.</summary>
    public IReadOnlyList<string> Choices =>
        Type.Contains('|') ? Type.Split('|', StringSplitOptions.RemoveEmptyEntries) : [];

    /// <summary>
    /// The model's default with any JSON quoting removed -- enum defaults arrive
    /// as "native" rather than native, which would never match a choice.
    /// </summary>
    public string DefaultDisplay => Default.Trim('"');

    /// <summary>
    /// The selected choice: the user's value if set, else the model's default.
    /// Setting it back to the default clears the value, so the request carries
    /// only what was actually changed.
    /// </summary>
    public string? Choice
    {
        get => _value.Length > 0 ? _value : (DefaultDisplay.Length > 0 ? DefaultDisplay : null);
        set => Value = value is null || value == DefaultDisplay ? "" : value;
    }

    public bool Toggle
    {
        get => bool.TryParse(_value.Length > 0 ? _value : DefaultDisplay, out var on) && on;
        set => Value = value.ToString().ToLowerInvariant() == DefaultDisplay.ToLowerInvariant()
            ? "" : value.ToString().ToLowerInvariant();
    }

    public double NumberValue
    {
        get => double.TryParse(_value.Length > 0 ? _value : DefaultDisplay,
                   System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
        set => Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public double NumberMin =>
        double.TryParse(Min, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : double.MinValue;

    public double NumberMax =>
        double.TryParse(Max, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : double.MaxValue;

    /// <summary>Integers step by one; floats need something finer.</summary>
    public double NumberStep => Type == "int" ? 1 : 0.1;

    /// <summary>Name plus the default, which is what a user needs to see beside a control.</summary>
    public string Label => DefaultDisplay.Length > 0 ? $"{Name}   (default {DefaultDisplay})" : Name;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One line of a result.
/// </summary>
/// <param name="StartSample">
/// Kept alongside the formatted <paramref name="Span"/> so a click can seek.
/// Formatting the offsets and discarding them is what made the rows
/// unactionable in the first place; -1 means the row has no position.
/// </param>
/// <summary>One audio stream a run produced, beyond the single main output.</summary>
public sealed record NamedAudioEntry(string Id, float[] Samples, int SampleRate, int Channels)
{
    public double Seconds => SampleRate > 0 && Channels > 0
        ? (double)Samples.Length / Channels / SampleRate : 0;

    public string Summary => $"{Seconds:F2}s · {SampleRate} Hz · {Channels}ch";
}

/// <summary>One non-audio output, with whatever the family attached to it.</summary>
public sealed record ArtifactEntry(string Id, string Kind, byte[] Payload, string Metadata)
{
    public string Summary => $"{Kind} · {Payload.Length} bytes"
                             + (Metadata.Length > 0 ? $" · {Metadata}" : "");

    /// <summary>Text artifacts are worth showing inline; binary ones are not.</summary>
    public string Preview
    {
        get
        {
            if (Payload.Length == 0) return "";
            // A payload with no control bytes beyond whitespace is text worth showing.
            foreach (var b in Payload.Take(512))
            {
                if (b < 0x09 || (b > 0x0D && b < 0x20)) return "(binary)";
            }
            var text = System.Text.Encoding.UTF8.GetString(Payload);
            return text.Length > 2000 ? text[..2000] + "…" : text;
        }
    }
}

/// <summary>Which control suits a declared option.</summary>
public enum OptionEditor { Choice, Toggle, Slider, Number, Text }

/// <summary>One line of the session log.</summary>
public readonly record struct LogEntry(string Time, string Kind, string Message);

public readonly record struct ResultRow(
    string Kind, string Span, string Value, string Detail,
    long StartSample = -1, long EndSample = -1);

/// <summary>One entry in the task selector: a display title and how many of the
/// loaded model's tasks it covers.</summary>
public sealed record TaskChip(string Task, string Title, int Count);

/// <summary>An option whose stable identity and shown label differ.</summary>
/// <remarks>
/// ToString is overridden because a ComboBox renders its items with it, and a
/// record's generated ToString would put the whole record in the drop-down.
/// </remarks>
public sealed record Choice(string Id, string Label)
{
    public override string ToString() => Label;
}
