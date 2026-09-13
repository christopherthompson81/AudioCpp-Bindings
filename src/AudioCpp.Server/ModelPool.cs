using System.Collections.Concurrent;

namespace AudioCpp.Server;

/// <summary>
/// The models the server offers, loaded on demand and used one request at a
/// time.
/// </summary>
/// <remarks>
/// Its own registry, not the window's. The ABI does not promise a session is
/// safe to use from two threads, and a desktop app driving one from the UI
/// while Kestrel drives it from a request thread is exactly that. Keeping them
/// separate costs memory when both hold the same model and removes a class of
/// bug that would only appear under load.
///
/// One session per model id, and one request at a time against it: the
/// reference server does the same, because a session owns graph and cache state
/// that a second concurrent request would corrupt.
/// </remarks>
public sealed class ModelPool(ServerConfig config, Action<string> log) : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly AudioCppRegistry _registry = AudioCppRegistry.Create();
    private bool _disposed;

    private sealed class Entry(ServerModel spec)
    {
        public ServerModel Spec { get; } = spec;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AudioCppModel? Model { get; set; }
        public AudioCppSession? Session { get; set; }
    }

    public IReadOnlyList<ServerModel> Models => [.. config.Models];

    public bool Knows(string id) => config.Models.Any(m => m.Id == id);

    /// <summary>
    /// Run something with the model itself as well as its session, for a route
    /// that has to ask the model about its own contract.
    /// </summary>
    /// <summary>
    /// Run something against a model's session, with the model loaded if it is
    /// not already and nothing else using it.
    /// </summary>
    public Task<T> UseAsync<T>(string id, Func<AudioCppSession, T> work,
                               CancellationToken cancel = default) =>
        UseModelAsync(id, (_, session) => work(session), cancel);

    /// <summary>
    /// As <see cref="UseAsync"/>, for work that has to ask the model what it
    /// accepts before building the request.
    /// </summary>
    /// <remarks>
    /// The model comes from the entry rather than a lookup table beside it. An
    /// earlier version kept a dictionary of loaded models to answer this, which
    /// was a data race: the gate is per model, so two ids loading at once --
    /// which is exactly what a non-lazy config does on startup -- wrote to one
    /// unsynchronised dictionary.
    /// </remarks>
    public async Task<T> UseModelAsync<T>(string id, Func<AudioCppModel, AudioCppSession, T> work,
                                          CancellationToken cancel = default)
    {
        var spec = config.Models.FirstOrDefault(m => m.Id == id)
                   ?? throw new KeyNotFoundException($"no model with id '{id}'");
        var entry = _entries.GetOrAdd(id, _ => new Entry(spec));

        await entry.Gate.WaitAsync(cancel);
        try
        {
            if (entry.Session is null)
            {
                var started = DateTime.UtcNow;
                entry.Model ??= _registry.Load(spec.Path,
                    new ModelConfig(spec.Family.Length > 0 ? spec.Family : null));
                entry.Session = entry.Model.CreateSession(
                    spec.Task, spec.Mode,
                    new BackendConfig(config.Backend, config.Device, config.Threads));
                log($"loaded {id} ({entry.Model.Family}) in "
                    + $"{(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            }
            return work(entry.Model!, entry.Session);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Load everything up front, for a config that asked not to be lazy.</summary>
    public async Task WarmAsync(CancellationToken cancel = default)
    {
        if (config.LazyLoad) return;
        foreach (var model in config.Models)
        {
            try
            {
                await UseAsync(model.Id, _ => 0, cancel);
            }
            catch (Exception exception) when (exception is AudioCppException or IOException)
            {
                // One model that will not load must not stop the server: the
                // others are still serviceable, and the failure shows on the
                // first request for that id as well as here.
                log($"could not load {model.Id}: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values)
        {
            entry.Gate.Wait();
            try
            {
                entry.Session?.Dispose();
                entry.Model?.Dispose();
            }
            finally
            {
                entry.Gate.Release();
                entry.Gate.Dispose();
            }
        }
        _entries.Clear();
        _registry.Dispose();
    }
}
