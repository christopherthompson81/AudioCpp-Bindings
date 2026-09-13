using System.Text;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Subtitle export from word timestamps.
/// </summary>
/// <remarks>
/// A transcript with per-word timings is a subtitle track that has not been cut
/// into lines yet. Grouping is by gap and length rather than by sentence: the
/// engine does not punctuate reliably across families, and a reader can follow
/// a line break mid-sentence far more easily than a caption that runs long.
/// </remarks>
public static class Subtitles
{
    /// <summary>Start a new cue after a silence this long, in seconds.</summary>
    private const double GapSeconds = 0.8;

    /// <summary>Characters per cue, past which it is cut at the next word.</summary>
    private const int CueBudget = 84;

    /// <summary>No cue shorter than this, so a one-word line does not flash past.</summary>
    private const double MinimumCueSeconds = 0.6;

    public readonly record struct Cue(double Start, double End, string Text);

    public static List<Cue> Group(
        IEnumerable<(string Word, long StartSample, long EndSample)> words, int sampleRate)
    {
        var cues = new List<Cue>();
        if (sampleRate <= 0) return cues;

        var text = new StringBuilder();
        double start = 0, end = 0;
        var open = false;

        foreach (var (word, startSample, endSample) in words)
        {
            var wordStart = startSample / (double)sampleRate;
            var wordEnd = endSample / (double)sampleRate;

            var breakHere = open
                && (wordStart - end > GapSeconds || text.Length + word.Length + 1 > CueBudget);

            if (breakHere)
            {
                cues.Add(new Cue(start, Math.Max(end, start + MinimumCueSeconds), text.ToString()));
                text.Clear();
                open = false;
            }

            if (!open) { start = wordStart; open = true; }
            if (text.Length > 0) text.Append(' ');
            text.Append(word);
            end = wordEnd;
        }

        if (open) cues.Add(new Cue(start, Math.Max(end, start + MinimumCueSeconds), text.ToString()));
        return cues;
    }

    public static string ToSrt(IReadOnlyList<Cue> cues)
    {
        var output = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            output.Append(i + 1).Append('\n')
                  .Append(Stamp(cues[i].Start, ',')).Append(" --> ").Append(Stamp(cues[i].End, ','))
                  .Append('\n').Append(cues[i].Text).Append("\n\n");
        }
        return output.ToString();
    }

    public static string ToVtt(IReadOnlyList<Cue> cues)
    {
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            output.Append(Stamp(cue.Start, '.')).Append(" --> ").Append(Stamp(cue.End, '.'))
                  .Append('\n').Append(cue.Text).Append("\n\n");
        }
        return output.ToString();
    }

    /// <summary>hh:mm:ss,mmm for SRT and hh:mm:ss.mmm for WebVTT — the only difference.</summary>
    private static string Stamp(double seconds, char decimalMark)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}{decimalMark}{span.Milliseconds:D3}";
    }
}
