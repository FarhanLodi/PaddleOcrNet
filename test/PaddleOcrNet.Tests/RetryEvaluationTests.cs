using PaddleOcrNet.Tests.Evaluation;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class RetryEvaluationTests
{
    [Fact]
    public void Higher_confidence_style_time_replacement_is_damage()
    {
        var score = RetryEvaluation.Score("time", "8:47", "8:47", "8147", ["8:47"]);
        Assert.Equal("damaged", score.Outcome);
        Assert.Equal(0, score.Before.CharacterEdits);
        Assert.Equal(1, score.After.CharacterEdits);
        Assert.Equal(1, score.CriticalTokensLost);
    }

    [Fact]
    public void Summary_separates_improvement_damage_unchanged_and_equal_error_changes()
    {
        var pages = new[]
        {
            RetryEvaluation.Score("corrected", "42", "4Z", "42", ["42"]),
            RetryEvaluation.Score("damaged", "8:47", "8:47", "8147", ["8:47"]),
            RetryEvaluation.Score("unchanged", "abc", "abc", "abc", []),
            RetryEvaluation.Score("equal", "abc", "abd", "abe", []),
        };
        Assert.Equal(new RetrySummary(1, 1, 1, 1, 1, 1), RetryEvaluation.Summarize(pages));
    }

    [Fact]
    public void Critical_tokens_count_repeated_occurrences_and_reject_substrings()
    {
        var score = RetryEvaluation.Score("ids", "42 42", "42 42", "142 42", ["42"]);
        Assert.Equal(1, score.CriticalTokensLost);
        Assert.Throws<InvalidDataException>(() => RetryEvaluation.Score("bad", "abc", "abc", "abc", ["42"]));
    }

    [Fact]
    public void Blank_reference_and_unicode_are_scored_without_confidence()
    {
        var blank = RetryEvaluation.Score("blank", "", "", "invented", []);
        Assert.Equal("damaged", blank.Outcome);
        Assert.Null(blank.After.Cer);
        Assert.Equal(8, blank.After.FalsePositiveCharacters);
        var unicode = RetryEvaluation.Score("unicode", "é", "e\u0301", "é", []);
        Assert.Equal("corrected", unicode.Outcome);
        Assert.Equal(0, unicode.NormalizedBefore.CharacterEdits);
        Assert.Equal(0, unicode.NormalizedAfter.CharacterEdits);
    }

    [Fact]
    public void Explicit_manifest_missing_data_fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "paddle-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "labels.json");
            Assert.Throws<FileNotFoundException>(() => RetryEvaluation.Load(path));
            File.WriteAllText(path, """{"datasetRevision":"v1","pages":[]}""");
            Assert.Throws<InvalidDataException>(() => RetryEvaluation.Load(path));
            File.WriteAllText(path, """{"datasetRevision":"v1","pages":[{"id":"p1","imagePath":"absent.png","reference":"42","criticalTokens":["42"]}]}""");
            Assert.Throws<FileNotFoundException>(() => RetryEvaluation.Load(path));
            File.WriteAllBytes(Path.Combine(dir, "absent.png"), []);
            Assert.Equal("42", Assert.Single(RetryEvaluation.Load(path).Pages).Reference);
        }
        finally
        {
            // Delete only the test-owned files and its uniquely named directory.
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }
}
