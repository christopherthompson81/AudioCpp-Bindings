using System.Text.Json;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// A reference voice: the audio a cloning family imitates, and the words spoken
/// in it.
/// </summary>
/// <remarks>
/// The transcript is not optional decoration. Cloning families want to know what
/// the reference says, and giving them the wrong words degrades the result in a
/// way that looks like the model being bad rather than the input being wrong.
/// </remarks>
public sealed record SavedVoice(string Name, string AudioPath, string Transcript)
{
    public bool Exists => File.Exists(AudioPath);

    public string Summary => Exists
        ? (Transcript.Length > 0 ? Transcript : "(no transcript)")
        : "file is missing";
}

/// <summary>
/// Reference voices kept on disk.
/// </summary>
/// <remarks>
/// The reference UI keeps these in browser IndexedDB, which has no desktop
/// equivalent and would not survive a reinstall anyway. A directory under the
/// platform's application-data path is the native equivalent: it persists, it is
/// inspectable, and a user can copy it between machines.
///
/// Audio is referenced by path rather than copied. A voice library that silently
/// duplicated every clip would grow without bound, and a user who moves their
/// audio would rather be told the file is missing than keep a stale copy.
/// </remarks>
public sealed class VoiceLibrary
{
    private readonly string _path;

    public VoiceLibrary(string? directory = null)
    {
        var root = directory ?? AppData.Root(Environment.SpecialFolder.ApplicationData);
        Directory.CreateDirectory(root);
        _path = System.IO.Path.Combine(root, "voices.json");
    }

    /// <summary>Where the library lives, shown so a user can find or back it up.</summary>
    public string StorePath => _path;

    public List<SavedVoice> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<SavedVoice>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A corrupt library should not stop the app loading; an empty list
            // is recoverable, a crash on startup is not.
            return [];
        }
    }

    /// <summary>
    /// The reference's bundled demo voices, if an audio.cpp checkout can be
    /// found.
    /// </summary>
    /// <remarks>
    /// Found rather than copied, using the same search as the native library
    /// and model specs. Three of the four transcripts are Chinese, which is
    /// worth knowing before using one as a cloning reference for English —
    /// the transcript is shown alongside so the choice is informed rather than
    /// a surprise in the output.
    /// </remarks>
    public static List<SavedVoice> DemoVoices()
    {
        var directory = FindDemoVoices();
        if (directory is null) return [];

        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var promptFile = System.IO.Path.Combine(directory, "prompt_text");
        if (File.Exists(promptFile))
        {
            foreach (var line in File.ReadLines(promptFile))
            {
                var split = line.IndexOf('|');
                if (split > 0) prompts[line[..split]] = line[(split + 1)..];
            }
        }

        var voices = new List<SavedVoice>();
        foreach (var wav in Directory.EnumerateFiles(directory, "*.wav").OrderBy(p => p, StringComparer.Ordinal))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(wav);
            voices.Add(new SavedVoice($"demo · {stem}", wav, prompts.GetValueOrDefault(stem, "")));
        }
        return voices;
    }

    private static string? FindDemoVoices()
    {
        var relative = System.IO.Path.Combine("webui", "native", "demo_voices");

        var native = Environment.GetEnvironmentVariable("AUDIOCPP_NATIVE_DIR");
        for (var dir = native is { Length: > 0 } ? new DirectoryInfo(native) : null;
             dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            // The pinned engine first, then a sibling checkout for anyone
            // building against their own.
            foreach (var candidate in new[]
                     {
                         System.IO.Path.Combine(dir.FullName, "external", "audio.cpp", relative),
                         System.IO.Path.Combine(dir.FullName, "audio.cpp", relative),
                     })
            {
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public void Save(IEnumerable<SavedVoice> voices)
    {
        var json = JsonSerializer.Serialize(voices,
            new JsonSerializerOptions { WriteIndented = true });

        // Write beside and move, so an interrupted save cannot leave a
        // half-written library that fails to parse on the next start.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}
