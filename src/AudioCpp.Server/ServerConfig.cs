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

    public IReadOnlyList<ServerModel> Models { get; init; } = [];
}
