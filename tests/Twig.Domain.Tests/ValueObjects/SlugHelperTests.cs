using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class SlugHelperTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Basic conversion
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BasicConversion_LowercasesAndReplacesSpaces()
    {
        SlugHelper.Slugify("Login timeout on slow connections")
            .ShouldBe("login-timeout-on-slow-connections");
    }

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("UPPER CASE", "upper-case")]
    [InlineData("mixed Case Input", "mixed-case-input")]
    public void BasicConversion_VariousCases(string input, string expected)
    {
        SlugHelper.Slugify(input).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Special characters
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("feat: add login!", "feat-add-login")]
    [InlineData("hello@world.com", "helloworldcom")]
    [InlineData("C# is great", "c-is-great")]
    [InlineData("dots...everywhere", "dotseverywhere")]
    public void SpecialChars_AreStripped(string input, string expected)
    {
        SlugHelper.Slugify(input).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Underscores → hyphens
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Underscores_ConvertedToHyphens()
    {
        SlugHelper.Slugify("hello_world_test").ShouldBe("hello-world-test");
    }

    [Fact]
    public void MixedSeparators_NormalizedToSingleHyphen()
    {
        SlugHelper.Slugify("hello _ world").ShouldBe("hello-world");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Consecutive hyphens
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("hello--world", "hello-world")]
    [InlineData("a---b----c", "a-b-c")]
    [InlineData("test - - value", "test-value")]
    public void ConsecutiveHyphens_Collapsed(string input, string expected)
    {
        SlugHelper.Slugify(input).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Truncation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void LongInput_TruncatedToMaxLength()
    {
        var input = "this is a very long title that should be truncated to fit within the maximum length";
        var result = SlugHelper.Slugify(input, 50);
        result.Length.ShouldBeLessThanOrEqualTo(50);
    }

    [Fact]
    public void Truncation_TrimsTrailingHyphen()
    {
        // "abcde-fghij" truncated to 6 = "abcde-" → trailing hyphen trimmed → "abcde"
        var result = SlugHelper.Slugify("abcde fghij", 6);
        result.ShouldBe("abcde");
        result.ShouldNotEndWith("-");
    }

    [Fact]
    public void CustomMaxLength_Respected()
    {
        var result = SlugHelper.Slugify("short title", 5);
        result.Length.ShouldBeLessThanOrEqualTo(5);
        result.ShouldBe("short");
    }

    [Fact]
    public void DefaultMaxLength_Is50()
    {
        var longInput = new string('a', 100);
        SlugHelper.Slugify(longInput).Length.ShouldBeLessThanOrEqualTo(50);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty / whitespace input
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespace_ReturnsEmpty(string? input)
    {
        SlugHelper.Slugify(input!).ShouldBe(string.Empty);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unicode handling
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("café latte", "caf-latte")]
    [InlineData("über cool", "ber-cool")]
    [InlineData("日本語テスト", "")]
    public void Unicode_NonAsciiStripped(string input, string expected)
    {
        SlugHelper.Slugify(input).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Leading/trailing hyphens
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("-hello-", "hello")]
    [InlineData("---leading", "leading")]
    [InlineData("trailing---", "trailing")]
    public void LeadingTrailingHyphens_Trimmed(string input, string expected)
    {
        SlugHelper.Slugify(input).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Negative / zero maxLength
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void NegativeOrZeroMaxLength_ReturnsEmpty(int maxLength)
    {
        SlugHelper.Slugify("some input", maxLength).ShouldBe(string.Empty);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance criteria
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AcceptanceCriteria_LoginTimeout()
    {
        SlugHelper.Slugify("Hello World!! @#$ Special_chars")
            .ShouldBe("hello-world-special-chars");
    }
}
