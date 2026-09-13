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

    // The configured models, which /v1/models/load can add to and change at
    // runtime. A copy rather than config.Models because the config is a record
    // and this list is not fixed once the server is up.
    private readonly List<ServerModel> _specs = [.. config.Models];
    private readonly Lock _specsLock = new();

    public IReadOnlyList<ServerModel> Models
    {
        get { lock (_specsLock) return [.. _specs]; }
    }

    public bool Knows(string id) => Spec(id) is not null;

    /// <summary>Whether this server allows models to be added or removed.</summary>
    public bool ManagementEnabled => config.UiManagement;

    /// <summary>
    /// Adds a model, or reconfigures one already registered under that id.
    /// </summary>
    /// <returns>
    /// Whether an existing entry was changed. A repeat of the same
    /// registration reports false and leaves a loaded model resident, which is
    /// what makes load idempotent rather than a way to evict by accident.
    /// </returns>
    public async Task<bool> RegisterAsync(ServerModel spec, CancellationToken cancel = default)
    {
        ServerModel? previous;
        lock (_specsLock)
        {
            previous = _specs.FirstOrDefault(m => m.Id == spec.Id);
        }

        if (previous is not null && previous == spec)
        {
            return false;
        }

        // Recorded before the old weights are released, not after. Unloading
        // first leaves a window in which the id is registered under the old
        // description with nothing resident, so a request arriving in it
        // reloads the *old* model -- producing exactly the mismatch this is
        // meant to prevent, and only under concurrency, which is the hardest
        // version of it to ever see again. Swapping first means a request in
        // that window loads the new description; the unload below then frees
        // it and the one after reloads it, which costs a load and is correct.
        lock (_specsLock)
        {
            var at = _specs.FindIndex(m => m.Id == spec.Id);
            // Replaced in place rather than appended, so a reconfiguration does
            // not shuffle the id to the end of /v1/models and make a client's
            // list look like it changed membership.
            if (at >= 0) _specs[at] = spec;
            else _specs.Add(spec);
        }
        if (previous is not null) await UnloadAsync(spec.Id, cancel);
        _refusesLanguage.TryRemove(spec.Id, out _);
        return previous is not null;
    }

    private readonly ConcurrentDictionary<string, bool> _refusesLanguage = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether this model has been seen to refuse a <c>language</c> request
    /// option, so the next request can leave it out instead of finding out
    /// again.
    /// </summary>
    /// <remarks>
    /// Learned rather than declared, because neither answer is available up
    /// front. The C ABI's set_text writes options["language"] as well as the
    /// transcript language, so the two cannot be sent separately; and a model's
    /// declared request options do not say which of them it needs:
    ///
    ///   - Parakeet TDT validates strictly against its spec, does not declare
    ///     "language", and refuses any request carrying it.
    ///   - Qwen3's forced aligner also does not declare "language" — and its
    ///     run() *requires* the transcript language, refusing without it.
    ///
    /// So "not declared" means "must not send" for one and "must send" for the
    /// other, and a guard built on the declaration alone breaks whichever it
    /// was not written for. Sending it and remembering a refusal serves both:
    /// the first request to a strict model pays one failed validation, which
    /// happens before any inference, and every request after it is clean.
    /// </remarks>
    public bool RefusesLanguage(string id) => _refusesLanguage.ContainsKey(id);

    /// <summary>Record that this model refused the language option.</summary>
    public void NoteLanguageRefused(string id)
    {
        if (_refusesLanguage.TryAdd(id, true))
        {
            log($"{id} refuses the 'language' request option; omitting it from now on");
        }
    }

    /// <summary>How a model was configured, or null if no such id.</summary>
    public ServerModel? Spec(string id)
    {
        lock (_specsLock) return _specs.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>Whether this model currently holds memory.</summary>
    public bool IsLoaded(string id) =>
        _entries.TryGetValue(id, out var entry) && entry.Session is not null;

    /// <summary>
    /// Releases a model's session and weights, waiting for any run in flight.
    /// </summary>
    /// <returns>Whether it was loaded; unloading an idle model is not an error.</returns>
    /// <remarks>
    /// Through the same gate a run takes, so an unload cannot land in the
    /// middle of one. The entry stays registered: the id is still known and the
    /// next request reloads it, which is what makes this a memory operation
    /// rather than a configuration one.
    /// </remarks>
    public async Task<bool> UnloadAsync(string id, CancellationToken cancel = default)
    {
        if (!_entries.TryGetValue(id, out var entry)) return false;

        await entry.Gate.WaitAsync(cancel);
        try
        {
            if (entry.Session is null) return false;
            entry.Session.Dispose();
            entry.Session = null;
            entry.Model?.Dispose();
            entry.Model = null;
            log($"unloaded {id}");
            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Unloads everything resident, and says what that was.</summary>
    public async Task<IReadOnlyList<string>> UnloadAllAsync(CancellationToken cancel = default)
    {
        var unloaded = new List<string>();
        foreach (var spec in Models)
        {
            if (await UnloadAsync(spec.Id, cancel)) unloaded.Add(spec.Id);
        }
        return unloaded;
    }

    /// <summary>Loads a model now rather than on its first request.</summary>
    public Task LoadAsync(string id, CancellationToken cancel = default) =>
        UseAsync(id, _ => 0, cancel);

    /// <summary>Drops a model from the registry entirely, unloading it first.</summary>
    public async Task<bool> ForgetAsync(string id, CancellationToken cancel = default)
    {
        if (Spec(id) is null) return false;
        await UnloadAsync(id, cancel);
        lock (_specsLock) _specs.RemoveAll(m => m.Id == id);
        _entries.TryRemove(id, out _);
        _refusesLanguage.TryRemove(id, out _);
        return true;
    }

    /// <summary>
    /// Voice names a client can put in a speech request, from every source
    /// that offers one.
    /// </summary>
    /// <remarks>
    /// An unknown id is an empty list rather than an error, and so is an
    /// omitted one when more than one model is configured — upstream's
    /// behaviour, and the reasonable one for a route whose whole job is to
    /// populate a picker. A picker that fails to load tells the user nothing;
    /// an empty one tells them this model has no named voices, which is often
    /// true.
    ///
    /// Sorted and deduplicated, because the same name can arrive from a
    /// configured preset and from the voice directory, and a picker showing it
    /// twice looks like two different voices.
    /// </remarks>
    public IReadOnlyList<string> VoicesFor(string id)
    {
        var voices = new SortedSet<string>(StringComparer.Ordinal);

        var spec = id.Length > 0
            ? Spec(id)
            : config.Models.Count == 1 ? config.Models[0] : null;
        if (spec is not null)
        {
            foreach (var preset in spec.VoicePresets ?? []) voices.Add(preset);

            // Families that keep voices as embeddings beside the weights.
            // Upstream looks under the configured path, which for it is a model
            // directory; ours is usually a .gguf file, so the file's own
            // directory is checked too. That is a superset of upstream's answer
            // for the same model, never a different one.
            AddStems(Path.Combine(spec.Path, "embeddings"), "*.safetensors", voices);
            var beside = Path.GetDirectoryName(spec.Path);
            if (!string.IsNullOrEmpty(beside))
            {
                AddStems(Path.Combine(beside, "embeddings"), "*.safetensors", voices);
            }
        }

        AddStems(config.VoiceDir, "*.wav", voices);
        return [.. voices];
    }

    private void AddStems(string directory, string pattern, SortedSet<string> into)
    {
        if (directory.Length == 0 || !Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                into.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A voice directory that cannot be read is an empty one. This route
            // populates a picker; failing the whole request because one of
            // three sources is unreadable would hide the two that worked.
            log($"could not read voices from {directory}: {error.Message}");
        }
    }

    /// <summary>
    /// Run something with the model itself as well as its session, for a route
    /// that has to ask the model about its own contract.
    /// </summary>
    /// <summary>
    /// Run something against a model's session, with the model loaded if it is
    /// not already and nothing else using it.
    /// </summary>
    public async Task<T> UseAsync<T>(string id, Func<AudioCppSession, T> work,
                                     CancellationToken cancel = default)
    {
        var spec = Spec(id) ?? throw new KeyNotFoundException($"no model with id '{id}'");
        var entry = _entries.GetOrAdd(id, _ => new Entry(spec));

        await entry.Gate.WaitAsync(cancel);
        try
        {
            if (entry.Session is null)
            {
                var started = DateTime.UtcNow;
                // spec, not entry.Spec: a reconfigured id keeps its entry (and
                // its gate, which callers may be queued on) while the
                // description it loads from changes underneath.
                entry.Model ??= _registry.Load(spec.Path,
                    new ModelConfig(spec.Family.Length > 0 ? spec.Family : null));
                entry.Session = entry.Model.CreateSession(
                    spec.Task, spec.Mode,
                    new BackendConfig(config.Backend, config.Device, config.Threads));
                log($"loaded {id} ({entry.Model.Family}) in "
                    + $"{(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            }
            return work(entry.Session);
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
