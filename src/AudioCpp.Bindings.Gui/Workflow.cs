namespace AudioCpp.Bindings.Gui;

/// <summary>
/// A group of engine tasks presented as one choice.
/// </summary>
/// <remarks>
/// The engine's task ids are not what a person picks between. "vad", "diar" and
/// "align" are three ways of analysing a recording, and a user choosing what to
/// do reaches for "audio analysis" and then a model; the id follows from the
/// model, not the other way round.
///
/// These are the web UI's groups, kept in its order and with its membership, so
/// the two interfaces offer the same choices and the imported labels apply
/// without a mapping. Getting this wrong in both directions is what #36 was:
/// four workflows missing entirely, three engine tasks promoted to top-level
/// choices they are not, and six tasks with no way to reach them at all.
/// </remarks>
public sealed record Workflow(string Id, IReadOnlyList<string> Tasks)
{
    /// <summary>Label and blurb keys, which are upstream's for this id.</summary>
    public string LabelKey => $"workflow.{Id}";
    public string BlurbKey => $"studio.subtitle.{Id}";

    public static readonly IReadOnlyList<Workflow> All =
    [
        new("tts", ["tts", "clon"]),
        new("asr", ["asr"]),
        new("music", ["gen"]),
        // Upstream calls this workflow 'conversion' but keys its label
        // 'workflow.vc'; the id here is the one the label and blurb keys use.
        new("vc", ["vc", "svc", "s2s"]),
        new("sep", ["sep"]),
        new("analysis", ["vad", "diar", "align", "spk", "midi"]),
        new("design", ["vdes"]),
    ];

    public static Workflow For(string id) =>
        All.FirstOrDefault(w => w.Id == id) ?? All[1];

    /// <summary>The workflow an engine task belongs to.</summary>
    public static Workflow ForTask(string task) =>
        All.FirstOrDefault(w => w.Tasks.Contains(task)) ?? All[1];

    /// <summary>Every task any workflow offers, in workflow order.</summary>
    public static IReadOnlyList<string> AllTasks { get; } =
        All.SelectMany(w => w.Tasks).ToList();
}
