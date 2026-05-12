using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Content;
using Xunit;

namespace Twig.Infrastructure.Tests.Content;

public sealed class HtmlFieldFormatterTests
{
    private readonly IFieldDefinitionStore _store = Substitute.For<IFieldDefinitionStore>();

    private void StubField(string refName, string dataType)
    {
        _store.GetByReferenceNameAsync(refName, Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition(refName, refName, dataType, false));
    }

    private void StubMissing(string refName)
    {
        _store.GetByReferenceNameAsync(refName, Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);
    }

    // ── ValidateFormat ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("markdown")]
    [InlineData("MARKDOWN")]
    [InlineData("raw")]
    [InlineData("Raw")]
    public void ValidateFormat_AcceptedValues_ReturnsNull(string? format)
    {
        HtmlFieldFormatter.ValidateFormat(format).ShouldBeNull();
    }

    [Theory]
    [InlineData("html")]
    [InlineData("plain")]
    [InlineData("")]
    public void ValidateFormat_Unknown_ReturnsErrorMentioningSupported(string format)
    {
        var error = HtmlFieldFormatter.ValidateFormat(format);

        error.ShouldNotBeNull();
        error!.ShouldContain("markdown");
        error.ShouldContain("raw");
    }

    // ── ResolveAsync (auto mode) ───────────────────────────────────

    [Fact]
    public async Task ResolveAsync_HtmlField_NullFormat_ConvertsAndIsHtml()
    {
        StubField("System.Description", "html");

        var result = await HtmlFieldFormatter.ResolveAsync(
            "System.Description", "## Heading", format: null, _store);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
        result.EffectiveValue.ShouldContain("Heading");
    }

    [Fact]
    public async Task ResolveAsync_StringField_NullFormat_PassesThrough()
    {
        StubField("System.Title", "string");

        var result = await HtmlFieldFormatter.ResolveAsync(
            "System.Title", "## not a heading", format: null, _store);

        result.IsHtml.ShouldBeFalse();
        result.EffectiveValue.ShouldBe("## not a heading");
    }

    [Fact]
    public async Task ResolveAsync_MarkdownFormat_AlwaysConverts_EvenForNonHtmlField()
    {
        StubField("System.Title", "string");

        var result = await HtmlFieldFormatter.ResolveAsync(
            "System.Title", "## hi", format: "markdown", _store);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
    }

    [Fact]
    public async Task ResolveAsync_RawFormat_HtmlField_PassesThrough_ButReportsHtml()
    {
        StubField("System.Description", "html");

        var result = await HtmlFieldFormatter.ResolveAsync(
            "System.Description", "<p>raw</p>", format: "raw", _store);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldBe("<p>raw</p>");
    }

    [Fact]
    public async Task ResolveAsync_RawFormat_NonHtmlField_PassesThroughAndNotHtml()
    {
        StubField("System.Title", "string");

        var result = await HtmlFieldFormatter.ResolveAsync(
            "System.Title", "literal", format: "raw", _store);

        result.IsHtml.ShouldBeFalse();
        result.EffectiveValue.ShouldBe("literal");
    }

    [Fact]
    public async Task ResolveAsync_FieldDefMissing_NullFormat_PassesThroughAndInvokesCallback()
    {
        StubMissing("Custom.Unknown");
        string? warned = null;

        var result = await HtmlFieldFormatter.ResolveAsync(
            "Custom.Unknown", "## hi", format: null, _store,
            onMissingFieldDef: name => warned = name);

        result.IsHtml.ShouldBeFalse();
        result.EffectiveValue.ShouldBe("## hi");
        warned.ShouldBe("Custom.Unknown");
    }

    [Fact]
    public async Task ResolveAsync_FieldDefMissing_MarkdownFormat_StillConverts_NoCallback()
    {
        StubMissing("Custom.Unknown");
        string? warned = null;

        var result = await HtmlFieldFormatter.ResolveAsync(
            "Custom.Unknown", "## hi", format: "markdown", _store,
            onMissingFieldDef: name => warned = name);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
        warned.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_HtmlField_DataTypeIsCaseInsensitive()
    {
        StubField("System.Description", "HTML");

        var result = await HtmlFieldFormatter.ResolveAsync(
            "System.Description", "## hi", format: null, _store);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
    }

    // ── ResolveForcedMarkdownDefault ───────────────────────────────

    [Fact]
    public void ResolveForcedMarkdownDefault_NullFormat_Converts()
    {
        var result = HtmlFieldFormatter.ResolveForcedMarkdownDefault("## hi", format: null);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
    }

    [Fact]
    public void ResolveForcedMarkdownDefault_MarkdownFormat_Converts()
    {
        var result = HtmlFieldFormatter.ResolveForcedMarkdownDefault("## hi", format: "markdown");

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
    }

    [Fact]
    public void ResolveForcedMarkdownDefault_RawFormat_PassesThroughButIsHtml()
    {
        var result = HtmlFieldFormatter.ResolveForcedMarkdownDefault("<p>raw</p>", format: "raw");

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldBe("<p>raw</p>");
    }

    // ── ResolveComment ─────────────────────────────────────────────

    [Fact]
    public void ResolveComment_NullFormat_Converts()
    {
        var result = HtmlFieldFormatter.ResolveComment("## hi", format: null);

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldContain("<h2");
    }

    [Fact]
    public void ResolveComment_RawFormat_PassesThrough()
    {
        var result = HtmlFieldFormatter.ResolveComment("<b>raw</b>", format: "raw");

        result.IsHtml.ShouldBeTrue();
        result.EffectiveValue.ShouldBe("<b>raw</b>");
    }
}
