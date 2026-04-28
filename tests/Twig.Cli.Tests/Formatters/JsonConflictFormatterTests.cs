using Shouldly;
using Twig.Commands;
using Twig.Domain.Services.Sync;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Tests for <see cref="JsonConflictFormatter"/>: RFC 8259 §7 compliant JSON escaping.
/// </summary>
public class JsonConflictFormatterTests
{
    [Fact]
    public void EscapeJson_BackslashAndQuote_Escaped()
    {
        JsonConflictFormatter.EscapeJson("a\\b\"c").ShouldBe("a\\\\b\\\"c");
    }

    [Fact]
    public void EscapeJson_ControlCharacters_Escaped()
    {
        JsonConflictFormatter.EscapeJson("line1\nline2\r\ttab").ShouldBe("line1\\nline2\\r\\ttab");
    }

    [Fact]
    public void EscapeJson_BackspaceAndFormFeed_Escaped()
    {
        JsonConflictFormatter.EscapeJson("\b\f").ShouldBe("\\b\\f");
    }

    [Fact]
    public void EscapeJson_LowUnicodeControl_EscapedAsHex()
    {
        // U+0001 (SOH) should be escaped as \u0001
        JsonConflictFormatter.EscapeJson("\u0001").ShouldBe("\\u0001");
    }

    [Fact]
    public void EscapeJson_Null_ReturnsEmpty()
    {
        JsonConflictFormatter.EscapeJson(null).ShouldBe("");
    }

    [Fact]
    public void FormatConflictsAsJson_WithControlChars_ProducesValidJson()
    {
        var conflicts = new List<FieldConflict>
        {
            new("System.Title", "line1\nline2", "tab\there")
        };

        var json = JsonConflictFormatter.FormatConflictsAsJson(conflicts);

        // Should be parseable JSON
        json.ShouldContain("\\n");
        json.ShouldContain("\\t");
        json.ShouldNotContain("\n");
        json.ShouldNotContain("\t");
    }
}
