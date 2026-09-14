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

    public IReadOnlyList<ServerModel> Models { get; init; } = [];
}
