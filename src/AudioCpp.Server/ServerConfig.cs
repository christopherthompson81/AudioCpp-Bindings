namespace AudioCpp.Server;

/// <summary>
/// Limits the API enforces, which a client needs to know before it sends.
/// </summary>
public static class ServerLimits
{
    /// <summary>
    /// 5 MiB, as the reference server documents for inline voice references.
    /// </summary>
    /// <remarks>
    /// Public because it is part of the contract: a client deciding between an
    /// inline reference and staging a file needs the number, and a test
    /// checking the boundary should use the same one the server does rather
    /// than a copy that can drift.
    /// </remarks>
    public const int MaxInlineReferenceBytes = 5 * 1024 * 1024;

    /// <summary>2 GiB, as the reference server caps UI uploads.</summary>
    /// <remarks>
    /// Enforced by counting bytes as they are written rather than by trusting
    /// Content-Length, which a chunked upload does not send at all. Without a
    /// cap the route writes whatever it is given straight to disk, which on a
    /// machine whose models folder shares a volume with everything else is a
    /// way to fill it.
    /// </remarks>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// Bounds on a live-ingest request, which holds a model for as long as its
/// connection lasts.
/// </summary>
/// <remarks>
/// Every one of these exists because the live routes invert the usual
/// arrangement: the client decides when the request ends. Without bounds, a
/// client that opens a connection and then stops sending holds the model
/// indefinitely, and one that stops *reading* does the same — so the send
/// timeout matters as much as the idle one. Upstream's defaults, which suit one
/// dictation at a time.
///
/// <c>0</c> means disabled, upstream's convention, except for
/// <see cref="MaxChunkBytes"/>: a chunk is materialised in memory before it is
/// used, so an unbounded one is not implementable.
/// </remarks>
public sealed record LiveIngestLimits
{
    /// <summary>Longest wait for more data once the reader has asked for it.</summary>
    public int IdleTimeoutMs { get; init; } = 30_000;

    /// <summary>Checked every time the body advances, so it caps the request.</summary>
    public int TotalTimeoutMs { get; init; } = 600_000;

    /// <summary>Received body bytes, framing included.</summary>
    public long MaxBodyBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Largest single read handed to the model.</summary>
    public int MaxChunkBytes { get; init; } = 8 * 1024 * 1024;
}

/// <summary>One model the server offers, as server.json declares it.</summary>
/// <param name="Id">The name a client uses in a request.</param>
/// <param name="Family">Family hint, or empty to identify from the path.</param>
/// <param name="Path">Model file or directory.</param>
/// <param name="Task">Engine task: tts, asr, vc, ...</param>
/// <param name="Mode">offline or streaming.</param>
/// <param name="VoicePresets">
/// Named voices this model offers, which <c>GET /v1/audio/voices</c> lists.
/// These are a server-config concept upstream too, not something read out of
/// the model file — the C ABI exposes no way to enumerate a model's voices.
/// </param>
public sealed record ServerModel(
    string Id,
    string Family = "",
    string Path = "",
    string Task = "tts",
    string Mode = "offline",
    IReadOnlyList<string>? VoicePresets = null);

/// <summary>
/// What the server runs as, mirroring audio.cpp's server.json.
/// </summary>
/// <remarks>
/// The same field names, so a config written for the reference server means the
/// same thing here. Diverging on spelling would make the two impossible to
/// compare, which is the only way to tell whether this serves the same API.
/// </remarks>
public sealed record ServerConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8080;
    public string Backend { get; init; } = "cpu";
    public int Device { get; init; }
    public int Threads { get; init; } = 1;

    /// <summary>
    /// Load a model on first use rather than at startup.
    /// </summary>
    /// <remarks>
    /// On by default because a multi-model config would otherwise hold every
    /// model resident before the first request, which on a 24 GB card is a
    /// short list.
    /// </remarks>
    public bool LazyLoad { get; init; } = true;

    /// <summary>
    /// A directory of .wav files offered as voices, by basename.
    /// </summary>
    /// <remarks>
    /// Listed by the voices route, which is why it lands here rather than
    /// waiting for the rest of the config surface: a voices route that cannot
    /// see the voice library answers with a list that is wrong rather than
    /// incomplete, and a client picking from it names a voice the speech route
    /// then rejects.
    /// </remarks>
    public string VoiceDir { get; init; } = "";

    /// <summary>
    /// A voice name from the library, resolved to the wav that clones it and
    /// the transcript that goes with it.
    /// </summary>
    /// <remarks>
    /// <c>voice_dir/prompt_text</c> is a <c>&lt;basename&gt;|&lt;transcript&gt;</c>
    /// mapping, the same format the web UI uses. The transcript matters as much
    /// as the audio: a cloning model given a reference clip with no reference
    /// text either refuses outright or clones from a transcript it guessed.
    ///
    /// The name is reduced to a filename and the result checked to be inside
    /// the library, because it arrives in a request body.
    /// </remarks>
    public (string Wav, string Text)? ResolveLibraryVoice(string name)
    {
        if (VoiceDir.Length == 0 || name.Length == 0) return null;
        if (name is "." or ".." || name != Path.GetFileName(name)) return null;

        var root = Path.GetFullPath(VoiceDir);
        var wav = Path.GetFullPath(Path.Combine(root, name + ".wav"));
        if (!wav.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(wav))
        {
            return null;
        }

        var transcript = "";
        var prompts = Path.Combine(root, "prompt_text");
        if (File.Exists(prompts))
        {
            foreach (var line in File.ReadLines(prompts))
            {
                var separator = line.IndexOf('|');
                if (separator < 0) continue;
                if (line[..separator].TrimEnd() != name) continue;
                transcript = line[(separator + 1)..];
                break;
            }
        }
        return (wav, transcript);
    }

    /// <summary>
    /// Whether a client may add and remove models at runtime, through
    /// <c>/v1/models/load</c> and <c>/v1/models/unload</c>.
    /// </summary>
    /// <remarks>
    /// Off by default, as upstream has it, and the default is the safe one
    /// rather than the tidy one: those routes name a filesystem path for the
    /// server to load, so enabling them on a port that is reachable turns the
    /// API into a way to read files and spend the machine's memory. A config
    /// with no models is only valid when this is on, since otherwise the server
    /// could never serve anything.
    /// </remarks>
    public bool UiManagement { get; init; }

    /// <summary>
    /// Where packages are installed, and where the UI routes browse from.
    /// </summary>
    /// <remarks>
    /// Also the "default" the models-root route reports and resets to, so a
    /// client that has moved the folder can get back without knowing what it
    /// started as.
    /// </remarks>
    public string ModelsRoot { get; init; } = "";

    /// <summary>Directory of model_specs/*.json describing installable packages.</summary>
    public string ModelSpecsDirectory { get; init; } = "";

    /// <summary>Bounds on the live-ingest routes.</summary>
    public LiveIngestLimits LiveIngest { get; init; } = new();

    /// <summary>Whether the browser UI is served at all.</summary>
    public bool UiEnabled { get; init; } = true;

    /// <summary>
    /// Origins allowed to call this API from a browser, or empty for none.
    /// </summary>
    /// <remarks>
    /// Upstream calls this experimental and so is this. <c>*</c> is honoured
    /// because upstream honours it, and it means any page on the internet the
    /// user visits can drive this server — which is only safe because the
    /// default host is loopback. Enabling both a wildcard origin and a
    /// non-loopback host is the combination to avoid, and nothing here can stop
    /// someone configuring it.
    /// </remarks>
    public string CorsOrigins { get; init; } = "";

    /// <summary>Log each request's body, truncated.</summary>
    /// <remarks>
    /// Off by default, and not only for noise: a speech request body carries
    /// the text being synthesised and may carry a base64 voice reference, so
    /// turning this on puts user content in the log.
    /// </remarks>
    public bool LogRequestBody { get; init; }

    /// <summary>Largest request body accepted, on the routes that buffer one.</summary>
    public long MaxRequestBodyBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// How long a request waits for a model another request is using before it
    /// gives up with a 503.
    /// </summary>
    /// <remarks>
    /// A model runs one request at a time. If an inference wedges the device —
    /// a CUDA call that never returns cannot be cancelled from userspace —
    /// every later request would wait forever, so past this bound they fail
    /// fast instead of parking a thread each. Must exceed the slowest
    /// legitimate single run; music generation takes minutes. 0 restores
    /// unbounded waiting.
    /// </remarks>
    public int BusyTimeoutMs { get; init; } = 300_000;

    /// <summary>
    /// How many models may be resident at once, or 0 for no limit.
    /// </summary>
    /// <remarks>
    /// Loading one past the limit first unloads the least recently used idle
    /// model, whose next request reloads it. That is what lets a multi-model
    /// config run on a device that does not fit all of them at once.
    /// </remarks>
    public int MaxLoadedModels { get; init; }

    /// <summary>
    /// Config keys this implementation does not model, kept verbatim.
    /// </summary>
    /// <remarks>
    /// So that saving a config never silently deletes a field it did not
    /// understand — see <see cref="ServerConfigFile"/>.
    /// </remarks>
    public System.Text.Json.Nodes.JsonObject Extra { get; init; } = [];

    public IReadOnlyList<ServerModel> Models { get; init; } = [];
}
