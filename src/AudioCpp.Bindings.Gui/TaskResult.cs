namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Everything one run produced, kept so it can be put back.
/// </summary>
/// <remarks>
/// A result belongs to the task that made it. Left in place across a task
/// switch it reads as this task's output — a transcript still sitting in the
/// Output panel after switching to text-to-speech looks like something the
/// synthesis produced.
///
/// Stashed rather than cleared, because clearing throws away work: coming back
/// to transcription should show the transcript that was there, not an empty
/// panel and a re-run.
///
/// The samples array is shared, not copied. Nothing mutates it in place — a
/// run replaces the reference — so a copy per stash would be megabytes for
/// nothing.
/// </remarks>
internal sealed record TaskResult(
    string Transcript,
    string ResultJson,
    string TimingBreakdown,
    string SegmentedSummary,
    double LastRunSeconds,
    IReadOnlyList<ResultRow> Rows,
    IReadOnlyList<(string Word, long StartSample, long EndSample)> Words,
    IReadOnlyList<NamedAudioEntry> Streams,
    IReadOnlyList<ArtifactEntry> Artifacts,
    IReadOnlyList<AudioSegment> Segments,
    float[]? OutputSamples,
    int OutputSampleRate,
    int OutputChannels)
{
    public static readonly TaskResult Empty = new(
        "", "", "", "", 0, [], [], [], [], [], null, 0, 1);

    /// <summary>Whether this is worth stashing at all.</summary>
    public bool HasAnything =>
        Transcript.Length > 0 || Rows.Count > 0 || OutputSamples is { Length: > 0 }
        || Streams.Count > 0 || Artifacts.Count > 0;
}
