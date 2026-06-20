using System.Text;

namespace PaddleOcrNet.Internal.Recognition;

/// <summary>
/// Loads a PaddleOCR character dictionary (a <c>ppocr_keys</c> / <c>*_dict.txt</c> file, one character
/// per line) into the exact ordered CTC vocabulary the recognizer emits.
/// <para>
/// PaddleOCR's convention: the recognizer's class 0 is the CTC blank, classes 1..N are the dictionary
/// lines in order, and a trailing space character is appended as the last class. So the returned vocab is
/// <c>["blank"] + lines + [" "]</c> and its <c>Count</c> equals the model's number of output classes.
/// </para>
/// <para>
/// This differs from the EasyOCR/RapidOCR placeholder convention only in the blank token's <em>spelling</em>:
/// the reference RapidOcrNet recognizer materializes its label array as <c>["#"] + lines + [" "]</c>, where
/// <c>"#"</c> is a stand-in for the blank at index 0. The index is identical (0) in both — Paddle prepends a
/// dedicated blank class — so decoding logic that drops index 0 is portable between them.
/// </para>
/// </summary>
internal static class CharacterDictionary
{
    /// <summary>The token placed at index 0 of every Paddle recognizer vocabulary (the CTC blank).</summary>
    public const string Blank = "blank";

    /// <summary>
    /// Reads the newline-delimited dictionary file at <paramref name="dictPath"/> and returns the Paddle
    /// vocabulary: <c>["blank"]</c> + the file's lines (in order) + a trailing <c>" "</c>.
    /// </summary>
    /// <param name="dictPath">Path to the ppocr dictionary file (UTF-8, one character/token per line).</param>
    /// <returns>The ordered vocabulary; index 0 is the blank, the last entry is a space.</returns>
    /// <exception cref="ArgumentException"><paramref name="dictPath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">No dictionary file exists at <paramref name="dictPath"/>.</exception>
    public static IReadOnlyList<string> Load(string dictPath)
    {
        if (string.IsNullOrEmpty(dictPath))
            throw new ArgumentException("Dictionary path must be provided.", nameof(dictPath));
        if (!File.Exists(dictPath))
            throw new FileNotFoundException("Character dictionary file not found.", dictPath);

        // Each line is one label, in CTC index order. We preserve order and content exactly, only
        // stripping the line terminators (ReadAllLines splits on \r\n / \n / \r and never keeps them).
        // A trailing blank/whitespace line in the file is meaningful in some ppocr dicts, but the
        // canonical packs end with content; ReadAllLines drops a single trailing empty produced by a
        // final newline, which matches PaddleOCR's own loader behaviour.
        string[] lines = File.ReadAllLines(dictPath, Encoding.UTF8);
        return FromLines(lines);
    }

    /// <summary>
    /// Builds the Paddle vocabulary directly from an in-memory ordered set of dictionary lines, applying
    /// the same <c>["blank"] + lines + [" "]</c> convention as <see cref="Load(string)"/>.
    /// </summary>
    /// <param name="lines">The dictionary entries in CTC label order (excluding blank and trailing space).</param>
    /// <returns>The ordered vocabulary; index 0 is the blank, the last entry is a space.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null.</exception>
    public static IReadOnlyList<string> FromLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // ["blank"] (index 0, the CTC blank) + dictionary lines (indices 1..N) + " " (the last class).
        var vocab = new List<string>(lines.Count + 2) { Blank };
        vocab.AddRange(lines);
        vocab.Add(" ");
        return vocab;
    }
}
