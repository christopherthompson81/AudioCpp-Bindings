using System.Text.Json;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// What the window remembers between launches.
/// </summary>
/// <remarks>
/// Every field is nullable, and absent means "leave the default alone". A
/// settings file written by an older build is missing the newest fields, and a
/// file written by a newer one carries fields this build does not know; neither
/// should stop the app from starting, and neither should overwrite a default
/// with a zero or an empty string.
///
/// Only choices are here. A loaded model, a run's results and the status line
/// are consequences of those choices, and restoring them would either be wrong
/// by the time the window opens or would load a model nobody asked for.
/// </remarks>
public sealed record Settings
{
    /// <summary>
    /// The workflow tab, not the engine task: the task follows from the model,
    /// so saving it would restore a choice the next model may overrule.
    /// </summary>
    public string? CurrentWorkflow { get; init; }
    public string? Theme { get; init; }
    public string? Language { get; init; }
    public string? Backend { get; init; }
    public int? Threads { get; init; }

    public string? ModelPath { get; init; }
    public string? FamilyHint { get; init; }
    public string? ModelsRoot { get; init; }

    public string? VadModelPath { get; init; }
    public string? VadAssetPath { get; init; }
    public double? MinSegmentSpan { get; init; }
    public double? MaxSegmentSpan { get; init; }

    public bool? UseBuiltInChunking { get; init; }
    public double? ChunkSeconds { get; init; }
    public bool? SplitLongText { get; init; }
    public int? ChunkBudget { get; init; }

    public string? AudioPath { get; init; }
    public string? VoiceId { get; init; }
    public bool? ShowAllOptions { get; init; }
}

/// <summary>
/// The settings file, beside the voice library.
/// </summary>
/// <remarks>
/// Same directory and the same write-and-move as <see cref="VoiceLibrary"/>:
/// a settings file is smaller but not less important, and a half-written one
/// that fails to parse on the next start is the failure worth designing out.
/// </remarks>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string? directory = null)
    {
        var root = directory ?? AppData.Root(Environment.SpecialFolder.ApplicationData);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public string StorePath => _path;

    /// <summary>What went wrong reading the file, or null if nothing did.</summary>
    /// <remarks>
    /// Reported rather than swallowed: settings silently reverting to defaults
    /// looks like the app forgetting them, which is the bug this whole file
    /// exists to fix. The session log gets to say it was a bad file.
    /// </remarks>
    public string? LoadProblem { get; private set; }

    public Settings Load()
    {
        LoadProblem = null;
        try
        {
            if (!File.Exists(_path)) return new Settings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? new Settings();
        }
        catch (Exception exception) when (exception is JsonException or IOException
                                          or UnauthorizedAccessException)
        {
            LoadProblem = $"{Path.GetFileName(_path)}: {exception.Message}";
            return new Settings();
        }
    }

    public void Save(Settings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true });
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A settings write that fails must not take a run down with it.
            // The next change tries again.
        }
    }
}
