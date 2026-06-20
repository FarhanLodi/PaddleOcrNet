using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the LaTeX-OCR (RapidLaTeXOCR) detokenization step
/// behind <see cref="PaddleOcrNet.Structure.Formula.LatexOcrRecognizer"/>. After the autoregressive decoder
/// produces a token-id sequence, the recognizer detokenizes it: map each id to its token string via the
/// vocab, join, translate the byte-level <c>Ġ</c> marker back into a space, strip the special
/// <c>BOS/EOS/PAD</c> tokens, and collapse runs of whitespace. This pins that transformation on a hand-built
/// token sequence; the logic is encoded as a local reference so it is verified independent of where the
/// recognizer body lands.
/// </summary>
public class LatexOcrDetokenizeTests
{
    // Special token ids (RapidLaTeXOCR tokenizer.json convention: 0 = [PAD], 1 = [BOS], 2 = [EOS]).
    private const int Pad = 0;
    private const int Bos = 1;
    private const int Eos = 2;

    // Hand-built id -> token vocab. The leading 'Ġ' (U+0120) is the GPT-2/byte-level space marker.
    private static readonly IReadOnlyDictionary<int, string> Vocab = new Dictionary<int, string>
    {
        [Pad] = "[PAD]",
        [Bos] = "[BOS]",
        [Eos] = "[EOS]",
        [10] = "\\frac",
        [11] = "{",
        [12] = "a",
        [13] = "}",
        [14] = "Ġ+",   // a token that begins with a space
        [15] = "Ġb",
        [16] = "x",
        [17] = "Ġ=",
        [18] = "Ġy",
    };

    private static readonly HashSet<int> Special = new() { Pad, Bos, Eos };

    // -----------------------------------------------------------------------------------------------------
    // Local reference of the detokenizer:
    //   • drop the special tokens (BOS / EOS / PAD) wherever they appear,
    //   • map each remaining id to its vocab string and concatenate,
    //   • translate the byte-level space marker 'Ġ' to a real space,
    //   • collapse any run of whitespace to a single space and trim the ends.
    // -----------------------------------------------------------------------------------------------------
    private static string Detokenize(IEnumerable<int> ids, IReadOnlyDictionary<int, string> vocab)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var id in ids)
        {
            if (Special.Contains(id)) continue;
            if (!vocab.TryGetValue(id, out var tok)) continue;
            sb.Append(tok);
        }

        var text = sb.ToString().Replace('Ġ', ' ');

        // Collapse internal whitespace runs to a single space and trim.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        return collapsed;
    }

    [Fact]
    public void Detokenize_joins_vocab_strings_and_maps_G_marker_to_space()
    {
        // [BOS] x Ġ= Ġy [EOS]  ->  "x = y"
        int[] ids = { Bos, 16, 17, 18, Eos };

        Assert.Equal("x = y", Detokenize(ids, Vocab));
    }

    [Fact]
    public void Detokenize_strips_bos_eos_and_pad_tokens()
    {
        // Trailing PADs and the BOS/EOS markers contribute nothing to the output.
        int[] ids = { Bos, 12, 14, 15, Eos, Pad, Pad, Pad };
        //            BOS  a  Ġ+  Ġb  EOS  pad pad pad   ->  "a + b"

        Assert.Equal("a + b", Detokenize(ids, Vocab));
    }

    [Fact]
    public void Detokenize_collapses_runs_of_whitespace()
    {
        // Two consecutive space-marked tokens with nothing between them must not yield a double space.
        int[] ids = { 16, 17, 17, 18 }; // x = = y  (intentional duplicate '=' to force a ws run)

        var result = Detokenize(ids, Vocab);

        Assert.DoesNotContain("  ", result);
        Assert.Equal("x = = y", result);
    }

    [Fact]
    public void Detokenize_renders_a_latex_fraction_without_spurious_spaces()
    {
        // [BOS] \frac { a } { a } [EOS]  ->  "\frac{a}{a}"  (no Ġ markers => no spaces inserted).
        int[] ids = { Bos, 10, 11, 12, 13, 11, 12, 13, Eos };

        Assert.Equal("\\frac{a}{a}", Detokenize(ids, Vocab));
    }

    [Fact]
    public void Detokenize_empty_or_special_only_sequence_returns_empty()
    {
        int[] ids = { Bos, Eos, Pad };

        Assert.Equal(string.Empty, Detokenize(ids, Vocab));
    }
}
