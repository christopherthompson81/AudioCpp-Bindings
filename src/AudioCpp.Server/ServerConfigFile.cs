using System.Text.Json;
using System.Text.Json.Nodes;

namespace AudioCpp.Server;

/// <summary>
/// Reading and writing <c>server.json</c>, in the reference server's shape.
/// </summary>
/// <remarks>
/// The same file, so a config written for audio.cpp's server runs here and one
/// written here runs there. That is the only way to tell whether this serves
/// the same API: a config that means something different is a difference no
/// test of the routes would catch.
///
/// Unknown keys are preserved on save rather than dropped. A config may carry
/// fields this implementation has no opinion about — upstream's
/// <c>frontend_options</c>, or a key added after this was written — and
/// silently deleting them when a user presses Save would be the worst kind of
/// data loss: invisible, and only discovered by whatever stopped working.
/// </remarks>
public static class ServerConfigFile
{
    public static ServerConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException("server config must be a JSON object");
        // Relative paths in a config mean "beside the config", which is what
        // makes a config directory movable. Resolving against the working
        // directory instead would make the same file mean different things
        // depending on where the server happened to be started.
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return FromJson(root, baseDirectory);
    }

    public static ServerConfig FromJson(JsonObject root, string baseDirectory)
    {
        var fallback = new ServerConfig();

        string Text(string name, string fallbackValue) =>
            root[name] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text : fallbackValue;

        bool Flag(string name, bool fallbackValue) =>
            root[name] is JsonValue value && value.TryGetValue<bool>(out var flag)
                ? flag : fallbackValue;

        int Number(string name, int fallbackValue) =>
            root[name] is JsonValue value && value.TryGetValue<int>(out var number)
                ? number : fallbackValue;

        string Resolve(string name)
        {
            var value = Text(name, "");
            if (value.Length == 0) return "";
            return Path.IsPathRooted(value) || baseDirectory.Length == 0
                ? value
                : Path.GetFullPath(Path.Combine(baseDirectory, value));
        }

        var live = fallback.LiveIngest;
        if (root["live_ingest"] is JsonObject ingest)
        {
            int Bound(string name, int fallbackValue) =>
                ingest[name] is JsonValue value && value.TryGetValue<int>(out var number)
                    ? number : fallbackValue;
            live = new LiveIngestLimits
            {
                IdleTimeoutMs = Bound("idle_timeout_ms", live.IdleTimeoutMs),
                TotalTimeoutMs = Bound("total_timeout_ms", live.TotalTimeoutMs),
                MaxChunkBytes = Bound("max_chunk_bytes", live.MaxChunkBytes),
                MaxBodyBytes = ingest["max_body_bytes"] is JsonValue body
                               && body.TryGetValue<long>(out var bytes)
                    ? bytes : live.MaxBodyBytes,
            };
        }

        var models = new List<ServerModel>();
        if (root["models"] is JsonArray declared)
        {
            foreach (var entry in declared.OfType<JsonObject>())
            {
                string Field(string name, string fallbackValue = "") =>
                    entry[name] is JsonValue value && value.TryGetValue<string>(out var text)
                        ? text : fallbackValue;

                var modelPath = Field("path");
                if (modelPath.Length > 0 && !Path.IsPathRooted(modelPath) && baseDirectory.Length > 0)
                {
                    modelPath = Path.GetFullPath(Path.Combine(baseDirectory, modelPath));
                }

                models.Add(new ServerModel(
                    Field("id"),
                    Field("family"),
                    modelPath,
                    Field("task", "tts"),
                    Field("mode", "offline"),
                    entry["voice_presets"] is JsonObject presets
                        ? [.. presets.Select(pair => pair.Key)]
                        : null));
            }
        }

        return new ServerConfig
        {
            Host = Text("host", fallback.Host),
            Port = Number("port", fallback.Port),
            Backend = Text("backend", fallback.Backend),
            Device = Number("device", fallback.Device),
            Threads = Number("threads", fallback.Threads),
            LazyLoad = Flag("lazy_load", fallback.LazyLoad),
            // "ui" upstream, not "ui_enabled": the struct field and the JSON key
            // differ there, and the JSON key is the contract.
            UiEnabled = Flag("ui", fallback.UiEnabled),
            UiManagement = Flag("ui_management", fallback.UiManagement),
            CorsOrigins = Text("cors_origins", fallback.CorsOrigins),
            LogRequestBody = Flag("log_request_body", fallback.LogRequestBody),
            MaxRequestBodyBytes = root["max_request_body_bytes"] is JsonValue limit
                                  && limit.TryGetValue<long>(out var maxBody)
                ? maxBody : fallback.MaxRequestBodyBytes,
            BusyTimeoutMs = Number("busy_timeout_ms", fallback.BusyTimeoutMs),
            MaxLoadedModels = Number("max_loaded_models", fallback.MaxLoadedModels),
            VoiceDir = Resolve("voice_dir"),
            ModelsRoot = Resolve("models_root"),
            ModelSpecsDirectory = Resolve("model_specs"),
            LiveIngest = live,
            Models = models,
            Extra = Preserve(root),
        };
    }

    /// <summary>
    /// The keys this implementation does not model, kept so a save does not
    /// delete them.
    /// </summary>
    private static JsonObject Preserve(JsonObject root)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "host", "port", "backend", "device", "threads", "lazy_load", "ui",
            "ui_management", "cors_origins", "log_request_body",
            "max_request_body_bytes", "busy_timeout_ms", "max_loaded_models",
            "voice_dir", "models_root", "model_specs", "live_ingest", "models",
        };
        var extra = new JsonObject();
        foreach (var pair in root)
        {
            if (known.Contains(pair.Key)) continue;
            extra[pair.Key] = pair.Value?.DeepClone();
        }
        return extra;
    }

    public static void Save(string path, ServerConfig config)
    {
        var root = new JsonObject();
        // Unknown keys first, so the ones this server understands are written
        // over them rather than under: a value we model wins over a stale copy
        // of itself that happened to survive in Extra.
        foreach (var pair in config.Extra) root[pair.Key] = pair.Value?.DeepClone();

        root["host"] = config.Host;
        root["port"] = config.Port;
        root["backend"] = config.Backend;
        root["device"] = config.Device;
        root["threads"] = config.Threads;
        root["lazy_load"] = config.LazyLoad;
        root["ui"] = config.UiEnabled;
        root["ui_management"] = config.UiManagement;
        root["cors_origins"] = config.CorsOrigins;
        root["log_request_body"] = config.LogRequestBody;
        root["max_request_body_bytes"] = config.MaxRequestBodyBytes;
        root["busy_timeout_ms"] = config.BusyTimeoutMs;
        root["max_loaded_models"] = config.MaxLoadedModels;
        if (config.VoiceDir.Length > 0) root["voice_dir"] = config.VoiceDir;
        if (config.ModelsRoot.Length > 0) root["models_root"] = config.ModelsRoot;
        if (config.ModelSpecsDirectory.Length > 0) root["model_specs"] = config.ModelSpecsDirectory;

        root["live_ingest"] = new JsonObject
        {
            ["idle_timeout_ms"] = config.LiveIngest.IdleTimeoutMs,
            ["total_timeout_ms"] = config.LiveIngest.TotalTimeoutMs,
            ["max_body_bytes"] = config.LiveIngest.MaxBodyBytes,
            ["max_chunk_bytes"] = config.LiveIngest.MaxChunkBytes,
        };

        var models = new JsonArray();
        foreach (var model in config.Models)
        {
            var entry = new JsonObject
            {
                ["id"] = model.Id,
                ["path"] = model.Path,
                ["task"] = model.Task,
                ["mode"] = model.Mode,
            };
            if (model.Family.Length > 0) entry["family"] = model.Family;
            if (model.VoicePresets is { Count: > 0 } presets)
            {
                var written = new JsonObject();
                foreach (var preset in presets) written[preset] = new JsonObject();
                entry["voice_presets"] = written;
            }
            models.Add(entry);
        }
        root["models"] = models;

        // Written beside the target and moved into place, so an interrupted
        // save leaves the previous config rather than half of a new one. A
        // server config that does not parse is a server that will not start.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}");
        File.WriteAllText(temporary,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
