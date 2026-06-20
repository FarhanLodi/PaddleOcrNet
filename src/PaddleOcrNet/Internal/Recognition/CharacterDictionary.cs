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
        return FromLines(LoadLines(dictPath));
    }

    /// <summary>
    /// Reads the raw dictionary lines from <paramref name="dictPath"/> in CTC index order, without applying
    /// any blank/space convention. Use with <see cref="BuildVocab"/> when the model's output class count is
    /// known, so the vocabulary can be matched to the network exactly (community ONNX exports disagree on
    /// whether the file already includes the blank/space classes).
    /// </summary>
    public static IReadOnlyList<string> LoadLines(string dictPath)
    {
        if (string.IsNullOrEmpty(dictPath))
            throw new ArgumentException("Dictionary path must be provided.", nameof(dictPath));
        if (!File.Exists(dictPath))
            throw new FileNotFoundException("Character dictionary file not found.", dictPath);

        // ReadAllLines splits on \r\n / \n / \r and drops a single trailing empty from a final newline,
        // matching PaddleOCR's own loader. We preserve order and content (including interior blank lines).
        return File.ReadAllLines(dictPath, Encoding.UTF8);
    }

    /// <summary>
    /// Builds the CTC vocabulary so that its length equals the recognizer's output class count, auto-detecting
    /// which blank/space convention the dictionary file follows. Community PP-OCRv5 exports are inconsistent:
    /// the default <c>ppocrv5_dict.txt</c> is the <em>complete</em> class list already (index 0 is the blank),
    /// while the per-language dicts omit the blank (and sometimes the trailing space). Matching the file's
    /// line count against <paramref name="numClasses"/> picks the right construction and avoids the
    /// off-by-one that shifts every decoded character.
    /// </summary>
    /// <param name="lines">Raw dictionary lines (see <see cref="LoadLines"/>).</param>
    /// <param name="numClasses">The recognizer's number of output classes (last output dim); ≤0 means unknown.</param>
    public static IReadOnlyList<string> BuildVocab(IReadOnlyList<string> lines, int numClasses)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Unknown class count: fall back to the canonical PaddleOCR convention.
        if (numClasses <= 0)
            return FromLines(lines);

        // The file is already the full class list (index 0 is the blank slot) — use it verbatim.
        if (lines.Count == numClasses)
            return new List<string>(lines);

        // File omits only the leading blank class.
        if (lines.Count + 1 == numClasses)
        {
            var v = new List<string>(numClasses) { Blank };
            v.AddRange(lines);
            return v;
        }

        // File omits both the leading blank and the trailing space class (the canonical PaddleOCR layout).
        if (lines.Count + 2 == numClasses)
            return FromLines(lines);

        // Mismatch we don't recognize: build the canonical vocab, then pad/truncate to the class count so
        // indexing stays in-bounds (a wrong glyph is preferable to an out-of-range crash mid-batch).
        var fallback = new List<string>(FromLines(lines));
        if (fallback.Count > numClasses)
            fallback.RemoveRange(numClasses, fallback.Count - numClasses);
        else
            while (fallback.Count < numClasses) fallback.Add(string.Empty);
        return fallback;
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
