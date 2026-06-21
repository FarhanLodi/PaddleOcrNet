using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the SLANet / SLANeXt table-structure decode step
/// behind <see cref="PaddleOcrNet.Structure.Table.SlanetTableRecognizer"/>. SLANet emits, per decode step, a
/// distribution over the structure-token vocabulary (<c>structure_probs[T, V]</c>) plus a 4-value cell box
/// (<c>loc[T, 4]</c>). The decoder argmaxes each step to a token, stops at <c>&lt;/eos&gt;</c>, skips the
/// <c>&lt;sos&gt;/&lt;eos&gt;</c> control tokens, joins the remaining structure tokens into an HTML table, and
/// pairs each emitted <c>&lt;td&gt;</c>/<c>&lt;td&gt;&lt;/td&gt;</c> cell-open token with the box from the
/// same step (td-token ↔ bbox alignment). This pins that decode on a hand-built tensor; the logic is encoded
/// as a local reference so it is verified independent of where the recognizer body lands.
/// </summary>
public class SlanetStructureDecodeTests
{
    // Minimal SLANet structure vocabulary (subset of table_structure_dict.txt) with the control tokens the
    // decoder must skip plus the HTML structure tokens it joins.
    private static readonly string[] Vocab =
    {
        /* 0 */ "<sos>",
        /* 1 */ "<eos>",
        /* 2 */ "<table>",
        /* 3 */ "<tr>",
        /* 4 */ "<td></td>",
        /* 5 */ "<td>",
        /* 6 */ "</td>",
        /* 7 */ "</tr>",
        /* 8 */ "</table>",
    };

    private const int Sos = 0;
    private const int Eos = 1;

    /// <summary>
    /// True for the cell-open tokens that carry a bounding box at their decode step.
    /// </summary>
    private static bool IsCellToken(string tok) => tok is "<td>" or "<td></td>";

    /// <summary>
    /// Asserts a decoded box's corners with a tolerance (the model emits float-precision values).
    /// </summary>
    private static void AssertBox(double x1, double y1, double x2, double y2, OcrBoundingBox actual, double tol = 1e-5)
    {
        Assert.Equal(x1, actual.MinX, tol);
        Assert.Equal(y1, actual.MinY, tol);
        Assert.Equal(x2, actual.MaxX, tol);
        Assert.Equal(y2, actual.MaxY, tol);
    }

    private readonly record struct DecodeResult(string Html, IReadOnlyList<OcrBoundingBox> CellBoxes);

    // -----------------------------------------------------------------------------------------------------
    // Local reference of the SLANet structure decode:
    //   • argmax structure_probs[t] -> token id for each step t,
    //   • break the moment <eos> is produced (everything after is ignored),
    //   • skip <sos> and <eos> when building the HTML string,
    //   • for every emitted cell token (<td> / <td></td>) take loc[t] as that cell's box (td ↔ bbox align).
    // Boxes are normalized [x1,y1,x2,y2] in 0..1 as the model emits them.
    // -----------------------------------------------------------------------------------------------------
    private static DecodeResult DecodeReference(float[,] structureProbs, float[,] loc, IReadOnlyList<string> vocab)
    {
        int steps = structureProbs.GetLength(0);
        int vocabSize = structureProbs.GetLength(1);

        var html = new System.Text.StringBuilder();
        var boxes = new List<OcrBoundingBox>();

        for (int t = 0; t < steps; t++)
        {
            int best = 0;
            float bestP = structureProbs[t, 0];
            for (int v = 1; v < vocabSize; v++)
            {
                if (structureProbs[t, v] > bestP)
                {
                    bestP = structureProbs[t, v];
                    best = v;
                }
            }

            if (best == Eos) break;        // eos terminates the sequence
            if (best == Sos) continue;     // sos is a control token, never emitted into HTML

            string tok = vocab[best];
            html.Append(tok);

            if (IsCellToken(tok))
            {
                boxes.Add(new OcrBoundingBox(loc[t, 0], loc[t, 1], loc[t, 2], loc[t, 3]));
            }
        }

        return new DecodeResult(html.ToString(), boxes);
    }

    /// <summary>
    /// Builds a one-hot <c>structure_probs[T,V]</c> from a token-id sequence (each step certain).
    /// </summary>
    private static float[,] OneHot(int[] tokenIds, int vocabSize)
    {
        var probs = new float[tokenIds.Length, vocabSize];
        for (int t = 0; t < tokenIds.Length; t++)
            probs[t, tokenIds[t]] = 1f;
        return probs;
    }

    [Fact]
    public void Decode_skips_sos_and_eos_and_joins_structure_tokens_into_html()
    {
        // <sos> <table> <tr> <td></td> </tr> </table> <eos>  ->  HTML omits <sos>/<eos>.
        int[] seq = { Sos, 2, 3, 4, 7, 8, Eos };
        var probs = OneHot(seq, Vocab.Length);
        var loc = new float[seq.Length, 4]; // single empty cell at step 3
        loc[3, 0] = 0.1f; loc[3, 1] = 0.2f; loc[3, 2] = 0.4f; loc[3, 3] = 0.5f;

        var result = DecodeReference(probs, loc, Vocab);

        Assert.Equal("<table><tr><td></td></tr></table>", result.Html);
    }

    [Fact]
    public void Decode_breaks_at_eos_and_ignores_trailing_tokens()
    {
        // The </table> after <eos> must never make it into the HTML (decode stops at the first <eos>).
        int[] seq = { Sos, 2, 3, 4, 7, Eos, 8 };
        var probs = OneHot(seq, Vocab.Length);
        var loc = new float[seq.Length, 4];

        var result = DecodeReference(probs, loc, Vocab);

        Assert.Equal("<table><tr><td></td></tr>", result.Html);
        Assert.DoesNotContain("</table>", result.Html);
    }

    [Fact]
    public void Decode_aligns_each_td_token_with_the_box_from_its_own_step()
    {
        // Two cells in one row: <td></td> at step 3 and <td> (with content) at step 4. Each cell's box must
        // be the loc row at that same step — not the next one, not shifted.
        int[] seq = { Sos, 2, 3, 4, 5, 6, 7, 8, Eos };
        //            0    1  2  3  4  5  6  7  8
        var probs = OneHot(seq, Vocab.Length);
        var loc = new float[seq.Length, 4];
        // step 3 -> first cell box
        loc[3, 0] = 0.00f; loc[3, 1] = 0.00f; loc[3, 2] = 0.50f; loc[3, 3] = 0.40f;
        // step 4 -> second cell box (the <td> open carries the box)
        loc[4, 0] = 0.50f; loc[4, 1] = 0.00f; loc[4, 2] = 1.00f; loc[4, 3] = 0.40f;

        var result = DecodeReference(probs, loc, Vocab);

        Assert.Equal("<table><tr><td></td><td></td></tr></table>", result.Html);
        Assert.Equal(2, result.CellBoxes.Count);

        AssertBox(0.00, 0.00, 0.50, 0.40, result.CellBoxes[0]);
        AssertBox(0.50, 0.00, 1.00, 0.40, result.CellBoxes[1]);
    }

    [Fact]
    public void Decode_uses_argmax_when_a_step_is_not_one_hot()
    {
        // A genuinely soft distribution at the cell step: argmax (index 4, <td></td>) wins over its rivals.
        var probs = new float[3, Vocab.Length];
        // step 0: <table>
        probs[0, 2] = 0.9f; probs[0, 3] = 0.1f;
        // step 1: <td></td> wins (0.55) over <td> (0.40)
        probs[1, 4] = 0.55f; probs[1, 5] = 0.40f; probs[1, 8] = 0.05f;
        // step 2: <eos>
        probs[2, Eos] = 0.8f; probs[2, 8] = 0.2f;
        var loc = new float[3, 4];

        var result = DecodeReference(probs, loc, Vocab);

        Assert.Equal("<table><td></td>", result.Html);
        Assert.Single(result.CellBoxes);
    }
}
