namespace AudioCpp.Bindings.Gui;

/// <summary>One synthesised piece of a long text.</summary>
/// <param name="Index">1-based position in the text, for naming exported files.</param>
public readonly record struct AudioSegment(
    int Index, string Text, float[] Samples, int SampleRate, int Channels)
{
    public double Seconds => SampleRate > 0 && Channels > 0
        ? (double)Samples.Length / Channels / SampleRate
        : 0;
}

/// <summary>Joining synthesised segments into one clip.</summary>
public static class AudioJoin
{
    /// <summary>
    /// Concatenate segments in order.
    /// </summary>
    /// <remarks>
    /// Refuses to join segments whose sample rate or channel count disagree,
    /// as the reference does. Silently resampling would be worse than failing:
    /// the result would play at the wrong speed with no indication why, and a
    /// disagreement here means the family returned something unexpected rather
    /// than that the user did anything wrong.
    /// </remarks>
    public static (float[] Samples, int SampleRate, int Channels) Concatenate(
        IReadOnlyList<AudioSegment> segments)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("No audio was generated.");

        var rate = segments[0].SampleRate;
        var channels = segments[0].Channels;
        foreach (var segment in segments)
        {
            if (segment.SampleRate != rate || segment.Channels != channels)
            {
                throw new InvalidOperationException(
                    $"Segment {segment.Index} is {segment.SampleRate} Hz / {segment.Channels}ch "
                    + $"but the first is {rate} Hz / {channels}ch; these cannot be joined.");
            }
        }

        var total = segments.Sum(s => (long)s.Samples.Length);
        if (total > Array.MaxLength)
            throw new InvalidOperationException("The joined audio is too large to hold in one buffer.");

        var merged = new float[total];
        var offset = 0;
        foreach (var segment in segments)
        {
            segment.Samples.CopyTo(merged, offset);
            offset += segment.Samples.Length;
        }
        return (merged, rate, channels);
    }
}
