using System.Text.RegularExpressions;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Splits long text into pieces a TTS family can synthesise in one request.
///
/// Ported from the web UI's lib/text.ts so both surfaces cut text the same way.
/// Worth keeping faithful: the sentence terminators include CJK punctuation
/// (。！？；…) as well as ASCII, and "Speaker 1:" prefixes are carried onto every
/// piece a line is split into, so a split mid-dialogue does not lose the
/// attribution that tells a multi-speaker family who is talking.
/// </summary>
public static partial class TextChunker
{
    [GeneratedRegex(@"^\s*(Speaker\s+\d+\s*:)\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeakerLine();

    /// <summary>
    /// Sentence boundaries.
    /// </summary>
    /// <remarks>
    /// The reference's terminator set is 。！？!?；;… — it has no ASCII full stop,
    /// so ordinary English prose is one unsplittable sentence and gets cut on
    /// length, mid-word. That is fine for the CJK text the set was written for
    /// and poor for everything else, so '.' is added here with the two guards
    /// that make it ambiguous in the first place:
    ///
    ///   - not between digits, so 3.14 stays whole;
    ///   - not after a known abbreviation or a single-letter initial, so
    ///     "Dr. Smith" and "J. R. R. Tolkien" stay whole.
    ///
    /// A terminator must also be followed by whitespace or end of text, which
    /// keeps file.txt and example.com intact.
    /// </remarks>
    [GeneratedRegex(@"(?<=[。！？；…]|(?<!\d)[!?.;](?=\s|$)|[!?;])\s*", RegexOptions.None)]
    private static partial Regex SentenceBreak();

    /// <summary>
    /// Abbreviations that end in a full stop without ending a sentence. Not
    /// exhaustive — it cannot be — but it covers what prose actually contains,
    /// and a miss costs a split in a slightly wrong place, not a failure.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "rev", "hon",
        "vs", "etc", "eg", "ie", "approx", "dept", "est", "fig", "no", "vol",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct",
        "nov", "dec", "inc", "ltd", "co", "corp",
    };

    /// <summary>
    /// Characters per chunk for a family. The engine does not report this, so it
    /// is carried per family exactly as the reference does; anything unlisted
    /// gets the general default.
    /// </summary>
    public static int DefaultBudget(string family) => family switch
    {
        "vibevoice" => 600,
        "voxcpm2" => 60,
        _ => 1000,
    };

    /// <summary>
    /// Break <paramref name="text"/> into chunks of at most <paramref name="budget"/>
    /// characters, preferring sentence boundaries, then line boundaries, and
    /// splitting mid-sentence only when a single sentence exceeds the budget.
    /// </summary>
    public static List<string> Split(string text, int budget)
    {
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));

        var units = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Trim().Length == 0) continue;
            if (trimmed.Length > budget) units.AddRange(SplitLongLine(trimmed, budget));
            else units.Add(trimmed);
        }

        var chunks = new List<string>();
        var current = new List<string>();
        var length = 0;
        foreach (var unit in units)
        {
            var separator = current.Count > 0 ? 1 : 0;
            if (current.Count > 0 && length + separator + unit.Length > budget)
            {
                chunks.Add(string.Join("\n", current));
                current.Clear();
                length = 0;
            }
            current.Add(unit);
            length += (current.Count > 1 ? 1 : 0) + unit.Length;
        }
        if (current.Count > 0) chunks.Add(string.Join("\n", current));

        if (chunks.Count > 0) return chunks;
        return text.Trim().Length > 0 ? [text] : [];
    }

    /// <summary>
    /// Break text after each sentence terminator, keeping the terminator and any
    /// following whitespace attached to the sentence it ends.
    /// </summary>
    internal static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (!IsTerminator(text[i])) continue;
            if (text[i] == '.' && !EndsSentence(text, i)) continue;

            // Take any run of terminators ("?!") and the whitespace after it.
            var end = i + 1;
            while (end < text.Length && IsTerminator(text[end])) end++;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;

            sentences.Add(text[start..end]);
            start = end;
            i = end - 1;
        }

        if (start < text.Length) sentences.Add(text[start..]);
        return sentences;
    }

    /// <summary>
    /// Cut <paramref name="text"/> into budget-sized pieces at whitespace,
    /// falling back to a hard cut only for a single token that is itself longer
    /// than the budget.
    /// </summary>
    private static List<string> BreakOnWords(string text, int budget)
    {
        var pieces = new List<string>();
        var rest = text.AsSpan().Trim();

        while (rest.Length > budget)
        {
            // Last whitespace at or before the budget; take the whole window if
            // the token straddling it cannot be broken any other way.
            var cut = rest[..(budget + 1)].LastIndexOf(' ');
            if (cut <= 0) cut = budget;

            var piece = rest[..cut].Trim();
            if (piece.Length > 0) pieces.Add(piece.ToString());
            rest = rest[cut..].Trim();
        }

        if (rest.Length > 0) pieces.Add(rest.ToString());
        return pieces;
    }

    private static bool IsTerminator(char c) =>
        c is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '…' or '.';

    /// <summary>Whether a full stop ends a sentence rather than a number or an abbreviation.</summary>
    private static bool EndsSentence(string text, int index)
    {
        // Must be followed by whitespace or end of text: keeps file.txt whole.
        if (index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1])) return false;

        // Not a decimal point.
        if (index > 0 && char.IsDigit(text[index - 1])
            && index + 1 < text.Length && char.IsDigit(text[index + 1])) return false;

        // Walk back over the word before the stop.
        var start = index;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '\''))
        {
            start--;
        }
        var word = text[start..index];

        // A single letter is an initial: "J. R. R. Tolkien".
        if (word.Length == 1 && char.IsLetter(word[0])) return false;

        return !Abbreviations.Contains(word);
    }

    private static List<string> SplitLongLine(string line, int budget)
    {
        var match = SpeakerLine().Match(line);
        var prefix = match.Success ? match.Groups[1].Value + " " : "";
        var body = match.Success ? match.Groups[2].Value : line.Trim();

        // The prefix is repeated onto every chunk, so it has to come out of the
        // budget or chunks carrying one exceed the limit the caller asked for.
        budget = Math.Max(1, budget - prefix.Length);

        var sentences = SplitSentences(body);
        if (sentences.Count == 0) sentences.Add(body);

        var chunks = new List<string>();
        var current = "";
        foreach (var raw in sentences)
        {
            var sentence = raw.TrimEnd();
            if (sentence.Length == 0) continue;
            if (current.Length > 0 && current.Length + 1 + sentence.Length > budget)
            {
                chunks.Add(prefix + current.Trim());
                current = "";
            }
            if (sentence.Length <= budget)
            {
                current = current.Length == 0 ? sentence : current + " " + sentence;
                continue;
            }
            if (current.Length > 0)
            {
                chunks.Add(prefix + current.Trim());
                current = "";
            }
            // A single sentence past the budget has no sentence boundary left, so
            // fall back to word boundaries. Only a token longer than the whole
            // budget gets cut mid-word, which is the one case with no better
            // answer than refusing to synthesise it at all.
            foreach (var piece in BreakOnWords(sentence, budget))
            {
                chunks.Add(prefix + piece);
            }
        }
        if (current.Length > 0) chunks.Add(prefix + current.Trim());

        return chunks.Count > 0 ? chunks : [line];
    }
}
