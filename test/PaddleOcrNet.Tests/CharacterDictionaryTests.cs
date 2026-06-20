using PaddleOcrNet.Internal.Recognition;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="CharacterDictionary"/>. Pins the
/// PaddleOCR vocabulary convention: <c>["blank"] + dictionary lines (in order) + [" "]</c>, where class 0
/// is the CTC blank, classes 1..N are the dictionary lines, and the trailing class is a literal space.
/// </summary>
public class CharacterDictionaryTests
{
    [Fact]
    public void FromLines_builds_blank_plus_lines_plus_space()
    {
        var vocab = CharacterDictionary.FromLines(new[] { "a", "b", "c" });

        Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", "c", " " }, vocab);
        Assert.Equal(5, vocab.Count);
    }

    [Fact]
    public void FromLines_blank_is_first_and_space_is_last()
    {
        var vocab = CharacterDictionary.FromLines(new[] { "x", "y" });

        Assert.Equal(CharacterDictionary.Blank, vocab[0]);
        Assert.Equal(" ", vocab[^1]);
    }

    [Fact]
    public void FromLines_index_to_char_is_off_by_one_against_the_source_lines()
    {
        // Dictionary line i sits at vocab index i+1 (the blank shifts everything by one).
        var lines = new[] { "h", "e", "l", "o" };
        var vocab = CharacterDictionary.FromLines(lines);

        for (int i = 0; i < lines.Length; i++)
            Assert.Equal(lines[i], vocab[i + 1]);
    }

    [Fact]
    public void FromLines_count_equals_lines_plus_two()
    {
        var lines = new[] { "a", "b", "c", "d", "e", "f" };
        var vocab = CharacterDictionary.FromLines(lines);
        Assert.Equal(lines.Length + 2, vocab.Count); // + blank + space
    }

    [Fact]
    public void Load_reads_newline_dict_into_blank_plus_lines_plus_space()
    {
        var path = Path.GetTempFileName();
        try
        {
            // A ppocr-style dictionary: one token per line, UTF-8, order preserved.
            File.WriteAllText(path, "a\nb\nc\n", System.Text.Encoding.UTF8);

            var vocab = CharacterDictionary.Load(path);

            Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", "c", " " }, vocab);
            Assert.Equal(CharacterDictionary.Blank, vocab[0]);
            Assert.Equal(" ", vocab[^1]);
            // Off-by-one: file line 0 ("a") -> vocab index 1, line 1 ("b") -> index 2, line 2 ("c") -> index 3.
            Assert.Equal("a", vocab[1]);
            Assert.Equal("b", vocab[2]);
            Assert.Equal("c", vocab[3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_handles_utf8_multibyte_lines_in_order()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "中\n文\n", System.Text.Encoding.UTF8);

            var vocab = CharacterDictionary.Load(path);

            Assert.Equal(new[] { CharacterDictionary.Blank, "中", "文", " " }, vocab);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
