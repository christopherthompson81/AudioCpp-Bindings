namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Translates the task names a model spec declares into the task tokens the
/// ABI takes.
/// </summary>
/// <remarks>
/// They are two different vocabularies for the same set of kinds. A spec says
/// "music", "clone", "design"; the ABI takes "gen", "clon", "vdes". Comparing
/// one against the other silently matches nothing, which is why the Music
/// generation tab was empty while nine families could serve it, and why Voice
/// design offered one package out of seven.
///
/// Both halves of the table are upstream's: the spec-side names come from
/// src/framework/model_spec/metadata.cpp, the ABI tokens from
/// to_string(VoiceTaskKind) in src/framework/runtime/session.cpp. Several spec
/// names collapse onto one token — music, sound effects and editing are all
/// audio generation as far as the engine is concerned.
/// </remarks>
internal static class SpecTasks
{
    private static readonly Dictionary<string, string> ToAbi = new(StringComparer.Ordinal)
    {
        ["vad"] = "vad",
        ["asr"] = "asr",
        ["diar"] = "diar",
        ["sep"] = "sep",
        ["audio_generation"] = "gen",
        ["music"] = "gen",
        ["sfx"] = "gen",
        ["edit"] = "gen",
        ["tts"] = "tts",
        ["clone"] = "clon",
        ["vc"] = "vc",
        ["s2s"] = "s2s",
        ["align"] = "align",
        ["design"] = "vdes",
        ["speaker"] = "spk",
        ["svc"] = "svc",
        ["midi"] = "midi",
    };

    /// <summary>
    /// Spec task names that deliberately have no ABI token, and why.
    /// </summary>
    /// <remarks>
    /// Both are names the engine cannot turn into a session, so a package
    /// offering one would fail on load. Not appearing is the better failure.
    /// Listed rather than ignored so a genuinely new name upstream is still
    /// caught.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> Unmappable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codec"] = "in the spec schema's task list, but metadata.cpp has no "
                      + "branch for it, so parse_task throws",
            ["vdes"] = "the ABI's own token, used by mistake in a spec where the "
                     + "spec-side name is \"design\"; the schema does not allow it "
                     + "and parse_task throws (moss_voicegen)",
        };

    /// <summary>The ABI token for a spec task name, or null if it has none.</summary>
    public static string? Abi(string specTask) => ToAbi.GetValueOrDefault(specTask);

    /// <summary>Every spec name this knows, for checking against upstream.</summary>
    public static IReadOnlyDictionary<string, string> All => ToAbi;
}
