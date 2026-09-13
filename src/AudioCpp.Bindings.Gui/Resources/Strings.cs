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
    public static string AppSubtitle => Get("app.subtitle");
    public static string NavStudio => Get("nav.studio");
    public static string NavArena => Get("nav.arena");
    public static string NavRuntime => Get("nav.runtime");
    public static string ThemeLabel => Get("theme.label");

    // Sections.
    public static string SectionModel => Get("section.model");
    public static string SectionSession => Get("section.session");
    public static string SectionLongAudio => Get("section.longAudio");
    public static string SectionRequest => Get("section.request");
    public static string SectionResult => Get("section.result");
    public static string SectionInput => Get("section.input");
    public static string SectionState => Get("section.state");
    public static string SectionEvents => Get("section.events");

    // Titles.
    public static string RequestTitle => Get("request.title");
    public static string ResultTitle => Get("result.title");
    public static string StudioEyebrow => Get("studio.eyebrow");

    // Controls.
    public static string Run => Get("action.run");
    public static string Cancel => Get("action.cancel");
    public static string Load => Get("action.load");
    public static string Unload => Get("action.unload");
    public static string Install => Get("action.install");
    public static string Delete => Get("action.delete");
    public static string Save => Get("action.save");
    public static string Play => Get("action.play");
    public static string Pause => Get("action.pause");
    public static string Record => Get("action.record");
    public static string Stop => Get("action.stop");
    public static string Compare => Get("action.compare");
    public static string SaveWav => Get("action.saveWav");

    // Input labels.
    public static string LabelText => Get("label.text");
    public static string LabelVoiceId => Get("label.voiceId");
    public static string LabelAudioInput => Get("label.audioInput");
    public static string LabelMicrophone => Get("label.microphone");
    public static string LabelReferenceVoice => Get("label.referenceVoice");
    public static string SplitLongText => Get("label.splitLongText");
    public static string CharsPerChunk => Get("label.charsPerChunk");
    public static string BuiltInChunking => Get("label.builtInChunking");

    // Task names and blurbs, which the hero block shows.
    public static string TaskTitle(string task) => Get($"task.{task}.title");
    public static string TaskBlurb(string task) => Get($"task.{task}.blurb");
    public static string TaskChip(string task) => Get($"task.{task}.chip");
}
