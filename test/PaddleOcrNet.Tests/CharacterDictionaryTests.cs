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

    [Fact]
    public void BuildVocab_uses_the_file_verbatim_when_it_is_already_the_full_class_list()
    {
        // ppocrv5_dict.txt ships as the complete class list: index 0 is the blank slot, the last class is
        // the literal space. Line count == class count, so nothing is added.
        var lines = new[] { "　", "a", "b", " " };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 4);

        Assert.Equal(lines, vocab);
    }

    [Fact]
    public void BuildVocab_treats_an_empty_first_line_as_the_blank_and_appends_the_space()
    {
        // Regression: the per-script PP-OCRv5 dicts (cyrillic_dict.txt, latin_dict.txt, arabic_dict.txt, …)
        // start with an empty line — that empty line IS the blank class, so the class the file omits is the
        // trailing space, not the blank. Prepending another blank shifted every character by one class and
        // turned Cyrillic output into mojibake (issue #1).
        var lines = new[] { "", "!", "\"", "А", "Б" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 6);

        Assert.Equal(new[] { CharacterDictionary.Blank, "!", "\"", "А", "Б", " " }, vocab);
        // The first real dictionary token stays at class 1 — where the network emits it.
        Assert.Equal("!", vocab[1]);
        Assert.Equal(" ", vocab[^1]);
    }

    [Fact]
    public void BuildVocab_prepends_the_blank_when_the_file_starts_with_a_real_token()
    {
        // A dict that is short by one class but whose first line is a real token omits only the blank.
        var lines = new[] { "a", "b", "c" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 4);

        Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", "c" }, vocab);
    }

    [Fact]
    public void BuildVocab_applies_the_canonical_layout_when_the_file_omits_blank_and_space()
    {
        var lines = new[] { "a", "b", "c" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 5);

        Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", "c", " " }, vocab);
    }

    [Theory]
    // (dictionary lines, model output classes) for every recognizer PaddleModelRegistry ships. The per-script
    // packs all lead with an empty line; the default ppocrv5 dict is the full class list.
    [InlineData(851, 852)]   // cyrillic_PP-OCRv5_mobile
    [InlineData(837, 838)]   // latin_PP-OCRv5_mobile
    [InlineData(748, 749)]   // arabic_PP-OCRv5_mobile
    [InlineData(569, 570)]   // devanagari_PP-OCRv5_mobile
    [InlineData(11946, 11947)] // korean_PP-OCRv5_mobile
    [InlineData(4400, 4401)] // japan_PP-OCRv5_mobile
    [InlineData(525, 526)]   // th_PP-OCRv5_mobile
    [InlineData(355, 356)]   // el_PP-OCRv5_mobile
    [InlineData(541, 542)]   // te_PP-OCRv5_mobile
    [InlineData(514, 515)]   // ta_PP-OCRv5_mobile
    [InlineData(518, 519)]   // eslav_PP-OCRv5_mobile
    public void BuildVocab_aligns_every_shipped_language_pack_dictionary(int dictLines, int numClasses)
    {
        // Line 0 empty (the blank slot), then dictLines-1 real tokens, mirroring the shipped files.
        var lines = new string[dictLines];
        lines[0] = string.Empty;
        for (int i = 1; i < dictLines; i++)
            lines[i] = $"c{i}";

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses);

        Assert.Equal(numClasses, vocab.Count);
        Assert.Equal(CharacterDictionary.Blank, vocab[0]);
        Assert.Equal(" ", vocab[^1]);
        // Every dictionary token keeps the class index the network was trained to emit for it.
        for (int i = 1; i < dictLines; i++)
            Assert.Equal(lines[i], vocab[i]);
    }
}
