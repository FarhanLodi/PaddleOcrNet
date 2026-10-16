using System.Text;

namespace PaddleOcrNet.Tests.Evaluation;

// Evaluation infrastructure, deliberately independent of OCR confidence and model availability.
internal sealed record TranscriptionScore(
    int CharacterEdits, int ReferenceCharacters, int WordEdits, int ReferenceWords,
    bool ExactMatch, int LineEdits, int ReferenceLines, int ExactLines, bool BlankReference,
    int FalsePositiveCharacters)
{
    public double? Cer => ReferenceCharacters == 0 ? null : (double)CharacterEdits / ReferenceCharacters;
    public double? Wer => ReferenceWords == 0 ? null : (double)WordEdits / ReferenceWords;
    public double? ExactLineRecall => ReferenceLines == 0 ? null : (double)ExactLines / ReferenceLines;
}

internal static class TranscriptionScorer
{
    public const string Normalization = "NFC; CRLF/CR to LF; trim/collapse horizontal whitespace per line; preserve case and line breaks";

    public static TranscriptionScore Score(string reference, string prediction)
    {
        var expected = reference.EnumerateRunes().ToArray();
        var actual = prediction.EnumerateRunes().ToArray();
        var words = Words(reference);
        return new(EditDistance(expected, actual), expected.Length,
            EditDistance(words, Words(prediction)), words.Length,
            string.Equals(reference, prediction, StringComparison.Ordinal),
            EditDistance(Lines(reference), Lines(prediction)), Lines(reference).Length,
            ExactLines(Lines(reference), Lines(prediction)),
            expected.Length == 0, expected.Length == 0 ? actual.Length : 0);
    }

    public static string Normalize(string text) => string.Join('\n',
        text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n').Select(line => string.Join(' ', Words(line))));

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static string[] Lines(string text) => text.Length == 0 ? [] : text.Split('\n');

    // Longest common subsequence aligns exact lines without shifting every match after an omission.
    private static int ExactLines(string[] expected, string[] actual)
    {
        if (expected.Length < actual.Length) (expected, actual) = (actual, expected);
        var row = new int[actual.Length + 1];
        foreach (var line in expected)
        {
            int diagonal = 0;
            for (int j = 1; j <= actual.Length; j++)
            {
                int previous = row[j];
                row[j] = string.Equals(line, actual[j - 1], StringComparison.Ordinal)
                    ? diagonal + 1 : Math.Max(row[j], row[j - 1]);
                diagonal = previous;
            }
        }
        return row[actual.Length];
    }

    // Two rows: memory depends on the shorter transcription, not their product.
    internal static int EditDistance<T>(T[] expected, T[] actual)
    {
        if (expected.Length < actual.Length) (expected, actual) = (actual, expected);
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        var current = new int[actual.Length + 1];
        for (int i = 1; i <= expected.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= actual.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (EqualityComparer<T>.Default.Equals(expected[i - 1], actual[j - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[actual.Length];
    }
}
