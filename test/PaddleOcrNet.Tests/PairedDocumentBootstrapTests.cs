using PaddleOcrNet.Tests.Evaluation;
using Xunit;

namespace PaddleOcrNet.Tests;

public class PairedDocumentBootstrapTests
{
    [Fact]
    public void SubgroupRegressionsCannotBeHiddenByImprovingMajority()
    {
        var scores = Enumerable.Range(0, 30).Select(i => new PairedDocumentScore($"a{i}", "clean", .3, .1))
            .Concat(Enumerable.Range(0, 20).Select(i => new PairedDocumentScore($"b{i}", "rtl", .1, .2))).ToArray();
        var result = PairedDocumentBootstrap.Evaluate(scores);
        Assert.Equal("pass", result["clean"].Gate);
        Assert.Equal("regression", result["rtl"].Gate);
    }

    [Fact]
    public void TooSmallSliceCannotPass()
        => Assert.Equal("insufficient-evidence", PairedDocumentBootstrap.Evaluate([new("a", "small", 1, 0)])["small"].Gate);

    [Fact]
    public void DuplicateDocumentRowsCannotPretendToBeIndependent()
        => Assert.Throws<ArgumentException>(() => PairedDocumentBootstrap.Evaluate([new("a", "rtl", 1, 0), new("a", "rtl", 1, 0)]));

    [Fact]
    public void FixedSeedProducesReproducibleIntervals()
    {
        var rows = Enumerable.Range(0, 30).Select(i => new PairedDocumentScore(i.ToString(), "all", .2, i / 100d)).ToArray();
        Assert.Equal(PairedDocumentBootstrap.Evaluate(rows)["all"], PairedDocumentBootstrap.Evaluate(rows)["all"]);
    }
}
