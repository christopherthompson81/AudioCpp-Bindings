namespace AudioCpp.Bindings.Gui;

/// <summary>
/// What a save dialog should offer for a given file.
/// </summary>
/// <remarks>
/// Derived from the name the caller suggests rather than passed alongside it:
/// every caller already names the file it means to write, and a separate
/// parameter is one a future caller can forget. Forgetting it is exactly what
/// happened — the dialog was hardcoded for audio, so saving a transcript
/// offered "Save generated audio", a .wav default extension and a WAV-only
/// filter over a file called transcript.srt.
///
/// Split out of the window so the mapping can be checked without driving a
/// file dialog.
/// </remarks>
public readonly record struct SaveKind(string Title, string Description, string Extension)
{
    private static readonly Dictionary<string, (string Title, string Description)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["wav"] = ("Save audio", "WAV audio"),
            ["srt"] = ("Save subtitles", "SubRip subtitles"),
            ["vtt"] = ("Save subtitles", "WebVTT subtitles"),
            ["json"] = ("Save data", "JSON"),
            ["txt"] = ("Save text", "Text"),
        };

    public static SaveKind For(string suggestedFileName)
    {
        var extension = System.IO.Path.GetExtension(suggestedFileName).TrimStart('.');
        return Known.TryGetValue(extension, out var known)
            ? new SaveKind(known.Title, known.Description, extension)
            // An unknown extension still beats the audio default: the name is
            // right, so offer that type rather than overriding it with WAV.
            : new SaveKind("Save file", extension.ToUpperInvariant(), extension);
    }

    public string Pattern => $"*.{Extension}";
}
