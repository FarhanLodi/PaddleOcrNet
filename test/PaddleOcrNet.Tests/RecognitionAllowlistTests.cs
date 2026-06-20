using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the recognition character filter
/// (<see cref="RecognitionOptions.Allowlist"/> / <see cref="RecognitionOptions.Blocklist"/>) implemented as
/// logit masking at decode time. Feeds hand-built logits + a small vocab to
/// <see cref="CharacterDictionary.BuildSelectableMask"/> and <see cref="CtcDecoder.GreedyDecode"/> and pins:
/// an allowlist restricts the output to allowed tokens, a blocklist suppresses specific tokens, the blank is
/// always kept, the space class follows the documented convention, the blocklist wins over the allowlist, and
/// empty lists reproduce the unmasked result exactly.
/// </summary>
public class RecognitionAllowlistTests
{
    // Paddle vocab convention: index 0 is the CTC blank, then characters, then the trailing space class.
    // Vocab: [blank, "a", "b", "c", "d", " "].
    private static readonly string[] Vocab =
        { CharacterDictionary.Blank, "a", "b", "c", "d", " " };

    /// <summary>
    /// Builds a flat row-major <c>[timeSteps, numClasses]</c> probability buffer where, at each timestep, the
    /// given winning class gets <paramref name="peak"/> and the remaining probability is split evenly over the
    /// other classes (so every row sums to 1 and the unmasked argmax is unambiguous).
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
    public void BuildSelectableMask_returns_null_when_both_lists_empty()
    {
        Assert.Null(CharacterDictionary.BuildSelectableMask(Vocab, null, null));
        Assert.Null(CharacterDictionary.BuildSelectableMask(Vocab, Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void EmptyLists_reproduce_unmasked_decode()
    {
        // a, b, c, d -> "abcd" both with and without a (null) mask.
        var logits = Probs(Vocab.Length, 0.9f, 1, 2, 3, 4);

        var unmasked = CtcDecoder.GreedyDecode(logits, 4, Vocab.Length, Vocab);
        var mask = CharacterDictionary.BuildSelectableMask(Vocab, null, null);
        var masked = CtcDecoder.GreedyDecode(logits, 4, Vocab.Length, Vocab, mask);

        Assert.Null(mask);
        Assert.Equal("abcd", unmasked.Text);
        Assert.Equal(unmasked.Text, masked.Text);
        Assert.Equal(unmasked.Confidence, masked.Confidence, 5);
    }

    [Fact]
    public void Allowlist_restricts_output_to_allowed_tokens()
    {
        // Per-timestep raw argmax would be a, b, c, d -> "abcd". Allow only {a, c}: the b and d timesteps
        // fall back to the next-best *selectable* class. With every non-allowed class masked, the only
        // selectable classes are blank, a, c (and space). At the b/d timesteps the highest selectable prob is
        // the evenly-split "rest" shared by a, c and blank — blank ties first in the seeded search and wins,
        // so those timesteps emit nothing. Result: "ac".
        var logits = Probs(Vocab.Length, 0.9f, 1, 2, 3, 4);

        var mask = CharacterDictionary.BuildSelectableMask(Vocab, new[] { "a", "c" }, null);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 4, Vocab.Length, Vocab, mask);

        Assert.Equal("ac", text);
    }

    [Fact]
    public void Allowlist_lets_a_non_winning_allowed_class_overtake_a_blocked_winner()
    {
        // One timestep: raw winner is "b" @ 0.6, but "a" carries 0.3 (above the rest). Allowing only {a}
        // masks b, so "a" — the best *selectable* non-blank class — is emitted instead of nothing.
        int n = Vocab.Length;
        var logits = new float[n];
        for (int c = 0; c < n; c++) logits[c] = 0.02f;
        logits[2] = 0.60f; // b (raw argmax)
        logits[1] = 0.30f; // a (allowed)

        var mask = CharacterDictionary.BuildSelectableMask(Vocab, new[] { "a" }, null);
        var (text, conf) = CtcDecoder.GreedyDecode(logits, 1, n, Vocab, mask);

        Assert.Equal("a", text);
        Assert.Equal(0.30f, conf, 3); // confidence is the chosen (selectable) class's prob, not the masked winner's
    }

    [Fact]
    public void Blocklist_suppresses_specific_tokens()
    {
        // Raw argmax a, b, c, d -> "abcd". Block {b}: the b timestep emits nothing, the rest stay.
        var logits = Probs(Vocab.Length, 0.9f, 1, 2, 3, 4);

        var mask = CharacterDictionary.BuildSelectableMask(Vocab, null, new[] { "b" });
        var (text, _) = CtcDecoder.GreedyDecode(logits, 4, Vocab.Length, Vocab, mask);

        Assert.Equal("acd", text);
    }

    [Fact]
    public void Blank_is_always_selectable_even_under_an_allowlist()
    {
        // Allowing only {a} must not mask the blank (index 0); a blank-only sequence still decodes to "".
        var logits = Probs(Vocab.Length, 0.9f, 0, 0, 0);
        var mask = CharacterDictionary.BuildSelectableMask(Vocab, new[] { "a" }, null);

        Assert.NotNull(mask);
        Assert.True(mask![0]);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab, mask);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Allowlist_keeps_space_selectable_by_default()
    {
        // An allowlist of {a} does not list space, but the space class stays selectable so words separate.
        var mask = CharacterDictionary.BuildSelectableMask(Vocab, new[] { "a" }, null);

        Assert.NotNull(mask);
        int spaceIndex = Array.IndexOf(Vocab, " ");
        Assert.True(mask![spaceIndex]);   // space kept
        Assert.True(mask[1]);             // "a" allowed
        Assert.False(mask[2]);            // "b" not allowed
    }

    [Fact]
    public void Explicitly_blocking_space_overrides_the_default_keep()
    {
        // Space stays selectable under an allowlist unless the caller explicitly blocks it.
        var mask = CharacterDictionary.BuildSelectableMask(Vocab, new[] { "a" }, new[] { " " });

        Assert.NotNull(mask);
        int spaceIndex = Array.IndexOf(Vocab, " ");
        Assert.False(mask![spaceIndex]);
    }

    [Fact]
    public void Blocklist_wins_over_allowlist_on_conflict()
    {
        // "a" is both allowed and blocked; the blocklist takes precedence, so "a" is suppressed.
        var logits = Probs(Vocab.Length, 0.9f, 1, 1, 1); // a, a, a -> would collapse to "a"
        var mask = CharacterDictionary.BuildSelectableMask(Vocab, new[] { "a", "b" }, new[] { "a" });

        Assert.NotNull(mask);
        Assert.False(mask![1]); // "a" blocked despite being allowed
        Assert.True(mask[2]);   // "b" still allowed

        var (text, _) = CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab, mask);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Multi_character_tokens_are_matched_as_a_whole()
    {
        // A dictionary with a multi-character token; matching is by the full token string, not per character.
        string[] vocab = { CharacterDictionary.Blank, "ab", "c", " " };
        var mask = CharacterDictionary.BuildSelectableMask(vocab, new[] { "ab" }, null);

        Assert.NotNull(mask);
        Assert.True(mask![1]);   // "ab" allowed as a whole token
        Assert.False(mask[2]);   // "c" not allowed
    }

    [Fact]
    public void FromCharacters_explodes_a_flat_string_into_single_char_tokens()
    {
        var list = RecognitionOptions.FromCharacters("0123");
        Assert.Equal(new[] { "0", "1", "2", "3" }, list);
        Assert.Empty(RecognitionOptions.FromCharacters(null));
        Assert.Empty(RecognitionOptions.FromCharacters(string.Empty));
    }

    [Fact]
    public void MaskLength_mismatch_is_ignored_by_the_decoder()
    {
        // A mask whose length disagrees with numClasses must not throw; the decoder falls back to unfiltered.
        var logits = Probs(Vocab.Length, 0.9f, 1, 2);
        var wrongLength = new bool[Vocab.Length + 3];

        var (text, _) = CtcDecoder.GreedyDecode(logits, 2, Vocab.Length, Vocab, wrongLength);
        Assert.Equal("ab", text);
    }
}
