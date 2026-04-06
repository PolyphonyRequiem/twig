using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Tests for <see cref="IOutputFormatter.FormatWorkItemLinks"/> across all formatter implementations.
/// </summary>
public class FormatWorkItemLinksTests
{
    private static readonly IReadOnlyList<WorkItemLink> SampleLinks = new List<WorkItemLink>
    {
        new(42, 100, "Related"),
        new(42, 200, "Predecessor"),
    };

    private static readonly IReadOnlyList<WorkItemLink> SingleLink = new List<WorkItemLink>
    {
        new(10, 20, "Successor"),
    };

    private static readonly IReadOnlyList<WorkItemLink> EmptyLinks = Array.Empty<WorkItemLink>();

    // ── HumanOutputFormatter ────────────────────────────────────────

    [Fact]
    public void Human_EmptyLinks_ReturnsDimNoLinks()
    {
        var fmt = new HumanOutputFormatter();

        var result = fmt.FormatWorkItemLinks(EmptyLinks);

        result.ShouldContain("No links");
    }

    [Fact]
    public void Human_WithLinks_IncludesCount()
    {
        var fmt = new HumanOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        result.ShouldContain("Links (2)");
    }

    [Fact]
    public void Human_WithLinks_IncludesSourceAndTargetIds()
    {
        var fmt = new HumanOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        result.ShouldContain("#42");
        result.ShouldContain("#100");
        result.ShouldContain("#200");
    }

    [Fact]
    public void Human_WithLinks_IncludesLinkType()
    {
        var fmt = new HumanOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        result.ShouldContain("Related");
        result.ShouldContain("Predecessor");
    }

    [Fact]
    public void Human_WithLinks_ContainsAnsiCodes()
    {
        var fmt = new HumanOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        result.ShouldContain("\x1b[");
    }

    [Fact]
    public void Human_SingleLink_FormatsCorrectly()
    {
        var fmt = new HumanOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SingleLink);

        result.ShouldContain("Links (1)");
        result.ShouldContain("#10");
        result.ShouldContain("#20");
        result.ShouldContain("Successor");
    }

    // ── JsonOutputFormatter ─────────────────────────────────────────

    [Fact]
    public void Json_EmptyLinks_ReturnsValidJsonWithZeroCount()
    {
        var fmt = new JsonOutputFormatter();

        var result = fmt.FormatWorkItemLinks(EmptyLinks);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("links").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void Json_WithLinks_ReturnsValidJsonWithCorrectCount()
    {
        var fmt = new JsonOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("links").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void Json_WithLinks_ContainsCorrectFields()
    {
        var fmt = new JsonOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        var doc = JsonDocument.Parse(result);
        var firstLink = doc.RootElement.GetProperty("links")[0];
        firstLink.GetProperty("sourceId").GetInt32().ShouldBe(42);
        firstLink.GetProperty("targetId").GetInt32().ShouldBe(100);
        firstLink.GetProperty("linkType").GetString().ShouldBe("Related");

        var secondLink = doc.RootElement.GetProperty("links")[1];
        secondLink.GetProperty("sourceId").GetInt32().ShouldBe(42);
        secondLink.GetProperty("targetId").GetInt32().ShouldBe(200);
        secondLink.GetProperty("linkType").GetString().ShouldBe("Predecessor");
    }

    // ── JsonCompactOutputFormatter ──────────────────────────────────

    [Fact]
    public void JsonCompact_DelegatesFullJson()
    {
        var full = new JsonOutputFormatter();
        var compact = new JsonCompactOutputFormatter(full);

        var fullResult = full.FormatWorkItemLinks(SampleLinks);
        var compactResult = compact.FormatWorkItemLinks(SampleLinks);

        compactResult.ShouldBe(fullResult);
    }

    [Fact]
    public void JsonCompact_EmptyLinks_ReturnsValidJson()
    {
        var compact = new JsonCompactOutputFormatter(new JsonOutputFormatter());

        var result = compact.FormatWorkItemLinks(EmptyLinks);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(0);
    }

    // ── MinimalOutputFormatter ──────────────────────────────────────

    [Fact]
    public void Minimal_EmptyLinks_ReturnsNoLinks()
    {
        var fmt = new MinimalOutputFormatter();

        var result = fmt.FormatWorkItemLinks(EmptyLinks);

        result.ShouldBe("No links");
    }

    [Fact]
    public void Minimal_WithLinks_ReturnsLinkPerLine()
    {
        var fmt = new MinimalOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
    }

    [Fact]
    public void Minimal_WithLinks_ContainsExpectedFormat()
    {
        var fmt = new MinimalOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        result.ShouldContain("LINK #42 Related #100");
        result.ShouldContain("LINK #42 Predecessor #200");
    }

    [Fact]
    public void Minimal_SingleLink_NoTrailingNewline()
    {
        var fmt = new MinimalOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SingleLink);

        result.ShouldNotEndWith("\n");
        result.ShouldNotEndWith("\r");
        result.ShouldBe("LINK #10 Successor #20");
    }

    [Fact]
    public void Minimal_WithLinks_NoAnsiCodes()
    {
        var fmt = new MinimalOutputFormatter();

        var result = fmt.FormatWorkItemLinks(SampleLinks);

        result.ShouldNotContain("\x1b[");
    }
}
