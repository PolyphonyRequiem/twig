using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

public class DeterministicTypeColorTests
{
    private static readonly string[] ValidAnsiEscapes =
    [
        "\x1b[35m", // Magenta
        "\x1b[36m", // Cyan
        "\x1b[34m", // Blue
        "\x1b[33m", // Yellow
        "\x1b[32m", // Green
        "\x1b[31m", // Red
    ];

    [Fact]
    public void GetAnsiEscape_ReturnsValidAnsiEscape()
    {
        var result = DeterministicTypeColor.GetAnsiEscape("Bug");

        ValidAnsiEscapes.ShouldContain(result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pinning tests — verify exact hash→color mapping matches
    //  the original HumanOutputFormatter.DeterministicColor() output
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Bug", "\x1b[32m")]   // hash 67156 % 6 = 4 → Green
    [InlineData("Epic", "\x1b[36m")]  // hash 2166565 % 6 = 1 → Cyan
    [InlineData("Task", "\x1b[36m")]  // hash 2599333 % 6 = 1 → Cyan
    public void GetAnsiEscape_KnownInputs_ReturnsExpectedColor(string typeName, string expected)
    {
        DeterministicTypeColor.GetAnsiEscape(typeName).ShouldBe(expected);
    }

    [Fact]
    public void GetAnsiEscape_SameInput_ReturnsSameOutput()
    {
        var first = DeterministicTypeColor.GetAnsiEscape("Epic");
        var second = DeterministicTypeColor.GetAnsiEscape("Epic");

        first.ShouldBe(second);
    }

    [Fact]
    public void GetAnsiEscape_DifferentInputs_CoversMultipleColors()
    {
        var colors = new HashSet<string>();
        // Use enough distinct type names to exercise multiple hash buckets.
        string[] typeNames = ["Bug", "Epic", "Feature", "Task", "User Story", "Test Case", "Issue", "Risk", "Impediment", "Change Request"];

        foreach (var name in typeNames)
            colors.Add(DeterministicTypeColor.GetAnsiEscape(name));

        // At least two distinct colors should appear across 10 inputs.
        colors.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void GetAnsiEscape_EmptyString_DoesNotThrow()
    {
        var result = DeterministicTypeColor.GetAnsiEscape("");

        ValidAnsiEscapes.ShouldContain(result);
    }
}
