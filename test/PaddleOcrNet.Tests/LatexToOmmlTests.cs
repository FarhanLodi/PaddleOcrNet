using System.Xml.Linq;
using PaddleOcrNet.Structure.Export;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Unit tests for <see cref="LatexToOmml"/>. Assertions are made on structural substrings (not whole-document
/// equality) plus a well-formedness check (every non-empty result must parse as XML).
/// </summary>
public class LatexToOmmlTests
{
    private const string MathNs = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    /// <summary>Asserts the result is a self-contained, well-formed <c>&lt;m:oMath&gt;</c> with the namespace bound.</summary>
    private static void AssertSelfContainedOMath(string omml)
    {
        Assert.StartsWith("<m:oMath", omml);
        Assert.Contains($"xmlns:m=\"{MathNs}\"", omml);

        // Must be parseable as XML on its own (the "m" prefix is declared on the root element).
        var doc = XDocument.Parse(omml);
        Assert.NotNull(doc.Root);
        Assert.Equal(XName.Get("oMath", MathNs), doc.Root!.Name);
    }

    [Fact]
    public void Frac_ProducesFractionElements()
    {
        string omml = LatexToOmml.Convert("\\frac{a}{b}");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:f>", omml);
        Assert.Contains("<m:num>", omml);
        Assert.Contains("<m:den>", omml);
        Assert.Contains("a", omml);
        Assert.Contains("b", omml);
    }

    [Fact]
    public void Superscript_ProducesSSup()
    {
        string omml = LatexToOmml.Convert("x^2");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:sSup>", omml);
        Assert.Contains("2", omml);
    }

    [Fact]
    public void SuperscriptBracedExponent_ProducesSSup()
    {
        string omml = LatexToOmml.Convert("x^{2n}");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:sSup>", omml);
        Assert.Contains("2n", omml);
    }

    [Fact]
    public void Subscript_ProducesSSub()
    {
        string omml = LatexToOmml.Convert("a_i");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:sSub>", omml);
        Assert.Contains("i", omml);
    }

    [Fact]
    public void CombinedSubSup_ProducesSSubSup()
    {
        string omml = LatexToOmml.Convert("x_i^2");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:sSubSup>", omml);
        Assert.Contains("<m:sub>", omml);
        Assert.Contains("<m:sup>", omml);
    }

    [Fact]
    public void Sqrt_ProducesRad()
    {
        string omml = LatexToOmml.Convert("\\sqrt{x}");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:rad>", omml);
        Assert.Contains("<m:degHide m:val=\"1\"/>", omml);
        Assert.Contains("x", omml);
    }

    [Fact]
    public void SqrtWithDegree_ProducesRadWithDegree()
    {
        string omml = LatexToOmml.Convert("\\sqrt[3]{x}");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:rad>", omml);
        Assert.Contains("<m:deg>", omml);
        // The degree placeholder must NOT be hidden when an explicit degree is given.
        Assert.DoesNotContain("<m:degHide", omml);
        Assert.Contains("3", omml);
    }

    [Fact]
    public void GreekLetters_ProduceUnicode()
    {
        string omml = LatexToOmml.Convert("\\alpha + \\beta");
        AssertSelfContainedOMath(omml);
        Assert.Contains("α", omml);
        Assert.Contains("β", omml);
        Assert.Contains("+", omml);
    }

    [Fact]
    public void UpperCaseGreek_ProduceUnicode()
    {
        string omml = LatexToOmml.Convert("\\Gamma \\Delta \\Sigma \\Omega \\Pi");
        AssertSelfContainedOMath(omml);
        Assert.Contains("Γ", omml);
        Assert.Contains("Δ", omml);
        Assert.Contains("Σ", omml);
        Assert.Contains("Ω", omml);
        Assert.Contains("Π", omml);
    }

    [Fact]
    public void Operators_ProduceUnicode()
    {
        string omml = LatexToOmml.Convert("a \\times b \\cdot c \\pm d \\div e \\leq f \\geq g \\neq h \\approx i");
        AssertSelfContainedOMath(omml);
        Assert.Contains("×", omml);
        Assert.Contains("·", omml);
        Assert.Contains("±", omml);
        Assert.Contains("÷", omml);
        Assert.Contains("≤", omml);
        Assert.Contains("≥", omml);
        Assert.Contains("≠", omml);
        Assert.Contains("≈", omml);
    }

    [Fact]
    public void Symbols_ProduceUnicode()
    {
        string omml = LatexToOmml.Convert("\\infty \\rightarrow \\partial \\nabla \\cdots");
        AssertSelfContainedOMath(omml);
        Assert.Contains("∞", omml);
        Assert.Contains("→", omml);
        Assert.Contains("∂", omml);
        Assert.Contains("∇", omml);
        Assert.Contains("⋯", omml);
    }

    [Fact]
    public void Sum_ProducesNary()
    {
        string omml = LatexToOmml.Convert("\\sum_{i=1}^{n} i");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:nary>", omml);
        Assert.Contains("∑", omml);
        Assert.Contains("<m:sub>", omml);
        Assert.Contains("<m:sup>", omml);
        Assert.Contains("<m:e>", omml);
    }

    [Fact]
    public void Integral_ProducesNaryWithIntegralChar()
    {
        string omml = LatexToOmml.Convert("\\int_0^1 f");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:nary>", omml);
        Assert.Contains("∫", omml);
    }

    [Fact]
    public void Product_ProducesNaryWithProductChar()
    {
        string omml = LatexToOmml.Convert("\\prod_{k} a");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:nary>", omml);
        Assert.Contains("∏", omml);
    }

    [Fact]
    public void SumWithoutLimits_HidesLimitPlaceholders()
    {
        string omml = LatexToOmml.Convert("\\sum x");
        AssertSelfContainedOMath(omml);
        Assert.Contains("<m:nary>", omml);
        Assert.Contains("<m:subHide m:val=\"1\"/>", omml);
        Assert.Contains("<m:supHide m:val=\"1\"/>", omml);
    }

    [Fact]
    public void ThinSpaceCommands_DoNotThrowAndStayWellFormed()
    {
        string omml = LatexToOmml.Convert("a\\,b\\;c\\!d\\ e");
        AssertSelfContainedOMath(omml);
        Assert.Contains("a", omml);
        Assert.Contains("e", omml);
    }

    [Fact]
    public void UnknownCommand_EmittedAsLiteralText()
    {
        string omml = LatexToOmml.Convert("\\foobar x");
        AssertSelfContainedOMath(omml);
        Assert.Contains("foobar", omml);
    }

    [Fact]
    public void NestedFraction_ParsesAndStaysWellFormed()
    {
        string omml = LatexToOmml.Convert("\\frac{\\frac{a}{b}}{c}");
        AssertSelfContainedOMath(omml);
        // The inner fraction nests inside the outer numerator.
        Assert.True(CountOccurrences(omml, "<m:f>") >= 2);
    }

    [Fact]
    public void XmlSpecialCharacters_AreEscaped()
    {
        string omml = LatexToOmml.Convert("a < b & c > d");
        AssertSelfContainedOMath(omml);
        Assert.Contains("&lt;", omml);
        Assert.Contains("&amp;", omml);
        Assert.Contains("&gt;", omml);
        // The raw, unescaped forms must not leak into text content.
        Assert.DoesNotContain("a < b", omml);
    }

    [Fact]
    public void NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LatexToOmml.Convert(null!));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LatexToOmml.Convert(""));
    }

    [Fact]
    public void WhitespaceInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LatexToOmml.Convert("   "));
    }

    [Fact]
    public void MalformedFraction_DoesNotThrowAndReturnsNonEmptyOMath()
    {
        string omml = LatexToOmml.Convert("\\frac{a");
        Assert.NotEqual(string.Empty, omml);
        AssertSelfContainedOMath(omml);
        Assert.Contains("a", omml);
    }

    [Fact]
    public void MalformedScript_DoesNotThrowAndStaysWellFormed()
    {
        // Trailing script operators with no argument must not blow up.
        string omml = LatexToOmml.Convert("x^");
        Assert.NotEqual(string.Empty, omml);
        AssertSelfContainedOMath(omml);
    }

    [Fact]
    public void UnbalancedBraces_DoNotThrow()
    {
        string omml = LatexToOmml.Convert("{a}}{b");
        Assert.NotEqual(string.Empty, omml);
        AssertSelfContainedOMath(omml);
    }

    [Theory]
    [InlineData("\\frac{x+1}{y-2}")]
    [InlineData("e^{i\\pi} + 1 = 0")]
    [InlineData("\\sqrt[n]{x^2 + y^2}")]
    [InlineData("\\sum_{i=0}^{\\infty} \\frac{1}{i!}")]
    [InlineData("\\alpha_1 + \\beta_2^3 - \\gamma")]
    [InlineData("\\int_a^b f(x)\\,dx")]
    public void VariousExpressions_ProduceWellFormedXml(string latex)
    {
        string omml = LatexToOmml.Convert(latex);
        AssertSelfContainedOMath(omml);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
