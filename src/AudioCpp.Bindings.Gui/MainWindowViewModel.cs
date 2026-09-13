using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Runtime.CompilerServices;
using AudioCpp.Native;

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
    private CancellationTokenSource? _cancel;
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

    /// <summary>Whatever the loaded family declares, read at runtime rather than hardcoded.</summary>
    public ObservableCollection<DeclaredOption> Options { get; } = [];
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
        }
    }

    private static string TitleFor(string task) => task switch
    {
        "asr" => "ASR / Transcription",
        "tts" => "Text to speech",
        "vad" => "Voice activity",
        "diar" => "Diarisation",
        "sep" => "Source separation",
        "align" => "Forced alignment",
        _ => task,
    };

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
    public string TaskTitle => _task switch
    {
        "asr" => "Transcription",
        "tts" => "Text to speech",
        "vad" => "Voice activity",
        "diar" => "Diarisation",
        "sep" => "Source separation",
        "align" => "Forced alignment",
        _ => _task,
    };

    public string TaskBlurb => _task switch
    {
        "asr" => "Transcribe spoken audio into text, with language and timestamp controls when supported.",
        "tts" => "Generate speech from text, with voice presets and cloning when supported.",
        "vad" => "Find the stretches of a recording that contain speech.",
        "diar" => "Attribute speech to speakers across a recording.",
        "sep" => "Split a mixture into its constituent sources.",
        "align" => "Align a known transcript to the audio it was spoken in.",
        _ => "",
    };

    public string TaskBadge => _task.ToUpperInvariant();

    /// <summary>Backend in the top-right pill, which reads CUDA once a session is live.</summary>
    public string BackendBadge => _backend.ToUpperInvariant();

    public string LoadedModelName => _model?.Family ?? "No model loaded";

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
            CancelCommand.RaiseCanExecuteChanged();
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
                        option.Required));
                }
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
            IsLoaded = true;
            Status = $"Loaded {model.Family} — {Options.Count} declared option(s), read from the model."
                   + (_vadModel is not null ? $"  VAD: {_vadModel.Family}." : "");
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
        _cancel = new CancellationTokenSource();
        CancelCommand.RaiseCanExecuteChanged();
        try
        {
            var started = DateTime.UtcNow;
            var rows = new List<ResultRow>();
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
                if (_session is null || _sessionKey != key)
                {
                    _session?.Dispose();
                    _session = _model!.CreateSession(
                        Task, "offline", new BackendConfig(Backend, 0, Threads),
                        sessionOptions.Count > 0 ? sessionOptions : null);
                    _sessionKey = key;
                }

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
                    request.SetText(Text, "en-us");
                    if (VoiceId.Length > 0) request.SetVoiceId(VoiceId);
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
            Notify(nameof(RunState));
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
            Status = Describe(exception);
        }
        finally
        {
            Busy = false;
            SaveWavCommand.RaiseCanExecuteChanged();
        }
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

    private Task CancelAsync()
    {
        _cancel?.Cancel();
        Status = "Cancelling after the current segment…";
        return System.Threading.Tasks.Task.CompletedTask;
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
    string scope, string name, string type, string @default, string range, bool required)
    : INotifyPropertyChanged
{
    private string _value = "";

    public string Scope { get; } = scope;
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string Default { get; } = @default;
    public string Range { get; } = range;
    public bool Required { get; } = required;

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

public readonly record struct ResultRow(string Kind, string Span, string Value, string Detail);

/// <summary>One entry in the task selector: a display title and how many of the
/// loaded model's tasks it covers.</summary>
public sealed record TaskChip(string Task, string Title, int Count);
