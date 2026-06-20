using PaddleOcrNet.Internal.Recognition;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="CtcDecoder.GreedyDecode"/> with
/// hand-built logits. Pins the PaddleOCR CTC convention: per-timestep argmax, drop the blank at index 0,
/// collapse runs of the same index, map the survivors through the vocab, and report the mean of the
/// per-timestep max probabilities over the timesteps that actually contributed a character.
/// </summary>
public class CtcDecoderTests
{
    // Paddle vocab convention: index 0 is the CTC blank, the rest map to characters.
    private static readonly string[] Vocab = { CharacterDictionary.Blank, "a", "b", "c" };

    /// <summary>
    /// Builds a flat row-major <c>[timeSteps, numClasses]</c> probability buffer where, at each timestep,
    /// the given winning class gets <paramref name="peak"/> and the remaining probability is split evenly
    /// over the other classes (so every row sums to 1 and the argmax is unambiguous).
    /// </summary>
    private static float[] Probs(int numClasses, float peak, params int[] argmaxPerStep)
    {
        var buf = new float[argmaxPerStep.Length * numClasses];
        float rest = (1f - peak) / (numClasses - 1);
        for (int t = 0; t < argmaxPerStep.Length; t++)
            for (int c = 0; c < numClasses; c++)
                buf[t * numClasses + c] = c == argmaxPerStep[t] ? peak : rest;
        return buf;
    }

    [Fact]
    public void GreedyDecode_skips_blank_at_index_zero()
    {
        // blank, a, blank -> "a"
        var logits = Probs(Vocab.Length, 0.9f, 0, 1, 0);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab);
        Assert.Equal("a", text);
    }

    [Fact]
    public void GreedyDecode_collapses_repeated_indices()
    {
        // a, a, a -> "a" (a run of the same index collapses to one character).
        var logits = Probs(Vocab.Length, 0.9f, 1, 1, 1);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab);
        Assert.Equal("a", text);
    }

    [Fact]
    public void GreedyDecode_blank_separated_repeat_is_kept_as_two_characters()
    {
        // a, blank, a -> "aa" (a blank between identical indices breaks the run).
        var logits = Probs(Vocab.Length, 0.9f, 1, 0, 1);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab);
        Assert.Equal("aa", text);
    }

    [Fact]
    public void GreedyDecode_decodes_full_sequence_with_collapse_and_blanks()
    {
        // a, a, blank, b, c, c -> "abc"
        var logits = Probs(Vocab.Length, 0.8f, 1, 1, 0, 2, 3, 3);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 6, Vocab.Length, Vocab);
        Assert.Equal("abc", text);
    }

    [Fact]
    public void GreedyDecode_confidence_is_mean_of_contributing_max_probabilities()
    {
        // Two distinct characters with different peaks; only the contributing (non-blank, non-repeat)
        // timesteps count toward the mean. Steps: a@0.90, a@0.70 (repeat — collapses to the same char),
        // blank, b@0.50. Whether the repeat timestep counts is implementation detail, so this case uses
        // distinct characters to make the contributing set unambiguous: a@0.90 and b@0.50 -> mean 0.70.
        var logits = Probs(Vocab.Length, 0.90f, 1);          // step 0: a @ 0.90
        var step1 = Probs(Vocab.Length, 0.50f, 2);           // step 1: b @ 0.50
        var combined = new float[logits.Length + step1.Length];
        logits.CopyTo(combined, 0);
        step1.CopyTo(combined, logits.Length);

        var (text, conf) = CtcDecoder.GreedyDecode(combined, 2, Vocab.Length, Vocab);

        Assert.Equal("ab", text);
        Assert.Equal(0.70f, conf, 3); // mean(0.90, 0.50)
    }

    [Fact]
    public void GreedyDecode_all_blank_yields_empty_text()
    {
        var logits = Probs(Vocab.Length, 0.9f, 0, 0, 0);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab);
        Assert.Equal(string.Empty, text);
    }
}
