namespace AudioCpp.Bindings.Gui;

/// <summary>
/// Word-level comparison of two transcripts.
/// </summary>
/// <remarks>
/// Agreement, not correctness: neither side is a reference. Comparing two
/// configurations of the same engine is how you tell whether a quantisation or
/// a backend changed the answer, which is the question this repo exists to
/// answer, and for that a symmetric measure is the honest one.
///
/// Words are compared case-insensitively with punctuation stripped, because
/// families differ on both and a capitalisation difference is not a
/// transcription difference.
/// </remarks>
public static class TextDiff
{
    public enum Mark { Same, OnlyLeft, OnlyRight }

    public readonly record struct Piece(Mark Mark, string Text);

    private static string[] Tokenise(string text) =>
        text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static string Normalise(string word) =>
        new(word.Where(c => char.IsLetterOrDigit(c) || c == '\'').Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Longest common subsequence over normalised words, walked back into a
    /// sequence of same/left/right runs.
    /// </summary>
    public static List<Piece> Compare(string left, string right)
    {
        var a = Tokenise(left);
        var b = Tokenise(right);
        var an = a.Select(Normalise).ToArray();
        var bn = b.Select(Normalise).ToArray();

        // O(n*m); transcripts here are thousands of words at most, and the
        // alternative is a diff library for one screen of one page.
        var table = new int[an.Length + 1, bn.Length + 1];
        for (var i = an.Length - 1; i >= 0; i--)
        {
            for (var j = bn.Length - 1; j >= 0; j--)
            {
                table[i, j] = an[i] == bn[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var pieces = new List<Piece>();
        int x = 0, y = 0;
        while (x < an.Length && y < bn.Length)
        {
            if (an[x] == bn[y]) { pieces.Add(new Piece(Mark.Same, a[x])); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) { pieces.Add(new Piece(Mark.OnlyLeft, a[x])); x++; }
            else { pieces.Add(new Piece(Mark.OnlyRight, b[y])); y++; }
        }
        while (x < an.Length) pieces.Add(new Piece(Mark.OnlyLeft, a[x++]));
        while (y < bn.Length) pieces.Add(new Piece(Mark.OnlyRight, b[y++]));
        return pieces;
    }

    /// <summary>
    /// Shared words as a fraction of the longer transcript. Dividing by the
    /// longer side means a configuration that simply stops early cannot score
    /// well by transcribing a little perfectly.
    /// </summary>
    public static double Agreement(string left, string right)
    {
        var a = Tokenise(left).Length;
        var b = Tokenise(right).Length;
        if (a == 0 && b == 0) return 1;
        if (a == 0 || b == 0) return 0;

        var same = Compare(left, right).Count(p => p.Mark == Mark.Same);
        return same / (double)Math.Max(a, b);
    }
}
