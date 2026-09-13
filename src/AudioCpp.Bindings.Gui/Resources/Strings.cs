using System.Globalization;
using System.Resources;

namespace AudioCpp.Bindings.Gui.Resources;

/// <summary>
/// User-facing text, looked up by key so it can be translated.
/// </summary>
/// <remarks>
/// Hand-written rather than generated. A .resx designer file is produced by an
/// IDE and drifts when edited anywhere else; this is the same lookup with the
/// keys visible, which matters more when the point is that a translator can see
/// what exists.
///
/// Keys shared with the audio.cpp web UI keep upstream's names — the same key
/// for the same string — so its translations import without a mapping table
/// and a later upstream catalogue can be diffed against ours. Keys prefixed
/// "action.", "label." and "section." are ours alone; upstream has no
/// equivalent, so they stay English in every language.
///
/// Only settled text lives here. Status messages still change with most
/// features, and extracting them now would mean churning the resource file
/// every time one is reworded.
/// </remarks>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("AudioCpp.Bindings.Gui.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// The culture used for lookups. Setting it re-reads every binding, so the
    /// UI changes language without a restart.
    /// </summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// The text for a key, or the key itself when it is missing — a visible
    /// key is a better bug report than a blank label.
    /// </summary>
    /// <remarks>
    /// A key absent from the chosen language is not missing: ResourceManager
    /// walks up to the neutral resources, so an untranslated key renders in
    /// English rather than as a key.
    /// </remarks>
    public static string Get(string key)
    {
        try
        {
            return Manager.GetString(key, Culture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    // Navigation and chrome.
    public static string AppSubtitle => Get("app.nativeStudio");
    public static string NavStudio => Get("nav.studio");
    public static string NavArena => Get("nav.arena");
    public static string NavRuntime => Get("nav.runtime");
    public static string LanguageLabel => Get("language.label");
    public static string ThemeLabel => Get("theme.label");

    // Sections.
    public static string SectionModel => Get("studio.model");
    public static string SectionSession => Get("section.session");
    public static string SectionLongAudio => Get("section.longAudio");
    public static string SectionRequest => Get("request.label");
    public static string SectionResult => Get("result.label");
    public static string SectionInput => Get("arena.input.label");
    public static string SectionState => Get("section.state");
    public static string SectionEvents => Get("section.events");

    // Page headings.
    public static string StudioEyebrow => Get("studio.eyebrow");
    public static string RequestTitle => Get("request.title");
    public static string ResultTitle => Get("result.title");
    public static string RuntimeEyebrow => Get("runtime.eyebrow");
    public static string RuntimeTitle => Get("runtime.title");
    public static string ArenaEyebrow => Get("arena.eyebrow");

    // Controls.
    public static string Run => Get("run.run");
    public static string Cancel => Get("common.cancel");
    public static string Delete => Get("common.delete");
    public static string SaveWav => Get("result.saveWav");
    public static string Load => Get("action.load");
    public static string Unload => Get("action.unload");
    public static string Install => Get("action.install");
    public static string Save => Get("action.save");
    public static string Play => Get("action.play");
    public static string Pause => Get("action.pause");
    public static string Record => Get("action.record");
    public static string Stop => Get("action.stop");
    public static string Compare => Get("action.compare");

    // Input labels.
    public static string LabelText => Get("request.text");
    public static string TextPlaceholder => Get("request.textPlaceholder");
    public static string SplitLongText => Get("request.splitLongText");
    public static string CharsPerChunk => Get("request.charactersPerChunk");
    public static string LabelAudioInput => Get("request.sourceAudio");
    public static string LabelReferenceVoice => Get("voice.reference");
    public static string RecommendedForCloning => Get("voice.recommendedClone");
    public static string LibraryName => Get("voice.libraryName");
    public static string ChooseSavedVoice => Get("voice.chooseSaved");
    public static string DefaultLabel => Get("models.default");
    public static string LabelVoiceId => Get("label.voiceId");
    public static string LabelMicrophone => Get("label.microphone");
    public static string BuiltInChunking => Get("label.builtInChunking");

    /// <summary>
    /// A workflow's tab label and the blurb under the hero.
    /// </summary>
    /// <remarks>
    /// Both are upstream keys for the same workflow id, so there is no mapping
    /// table to keep in step — adding a workflow upstream means adding an id.
    /// </remarks>
    public static string WorkflowLabel(Workflow workflow) => Get(workflow.LabelKey);

    public static string WorkflowBlurb(Workflow workflow) => Get(workflow.BlurbKey);

    /// <summary>
    /// The name of one engine task.
    /// </summary>
    /// <remarks>
    /// Falls back to the id. Upstream's analysis workflow lists a "spk" task
    /// its own catalogue has no label for, and showing "spk" is better than
    /// showing nothing or pretending the task does not exist.
    /// </remarks>
    public static string TaskName(string task)
    {
        var text = Get($"task.{task}");
        return text == $"task.{task}" ? task : text;
    }

    /// <summary>What the hero says when no model has been chosen yet.</summary>
    public static string StudioTitle => Get("studio.title");
}