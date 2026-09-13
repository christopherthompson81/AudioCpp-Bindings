namespace AudioCpp.Server;

/// <summary>One model the server offers, as server.json declares it.</summary>
/// <param name="Id">The name a client uses in a request.</param>
/// <param name="Family">Family hint, or empty to identify from the path.</param>
/// <param name="Path">Model file or directory.</param>
/// <param name="Task">Engine task: tts, asr, vc, ...</param>
/// <param name="Mode">offline or streaming.</param>
public sealed record ServerModel(
    string Id,
    string Family = "",
    string Path = "",
    string Task = "tts",
    string Mode = "offline");

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

    public IReadOnlyList<ServerModel> Models { get; init; } = [];
}
