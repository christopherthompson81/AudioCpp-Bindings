namespace AudioCpp.Server;

/// <summary>
/// A model was held by another request for longer than the configured bound.
/// </summary>
/// <remarks>
/// Its own type because it is a 503, not a 500: nothing is wrong with the
/// request and retrying it later is the right thing to do, which is exactly
/// what 503 tells a client and 500 does not.
/// </remarks>
public sealed class ServerBusyException(string message) : Exception(message);
