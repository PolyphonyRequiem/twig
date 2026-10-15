using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class SeedPublishCorrelationTests
{
    private static readonly SeedPublishCorrelation Correlation = new(
        StagedIdentity.FromGuid(Guid.Parse("019a0000-0000-7000-8000-000000000001")),
        DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("<p>{marker}</p>")]
    [InlineData("<P >\r\n Twig&nbsp;publish&#32;origin v1: {id} &#160;\t</P >")]
    [InlineData("<div>\n<p>{marker}</p>\n</div>")]
    public void SupportedParagraph_NormalizesSerializationAndRemovesOnlyRawBlock(string template)
    {
        const string before = "<div class='human'><p>Keep <b>every byte</b> &amp; entity.</p></div>\r\n";
        const string after = "\n<p>Human suffix <a href='https://example.test'>link</a>.</p>  ";
        var marker = Expand(template);
        var description = before + marker + after;

        Correlation.ContainsDescriptionMarker(description).ShouldBeTrue();
        Correlation.TryRemoveDescriptionMarker(description, out var remaining).ShouldBeTrue();
        remaining.ShouldBe(before + (template.StartsWith("<div>", StringComparison.Ordinal) ? "<div>\n\n</div>" : "") + after);
    }

    [Fact]
    public void MarkerOnlyDescription_BecomesEmptyNotNull()
    {
        Correlation.TryRemoveDescriptionMarker(Correlation.DescriptionMarkerHtml, out var remaining).ShouldBeTrue();
        remaining.ShouldBe("");
    }

    [Fact]
    public void DifferentSupportedIdentity_IsRecognizedButNotOwned()
    {
        var other = new SeedPublishCorrelation(
            StagedIdentity.FromGuid(Guid.Parse("019a0000-0000-7000-8000-000000000002")),
            DateTimeOffset.UnixEpoch);
        var description = "<p>Human title is irrelevant.</p>" + other.DescriptionMarkerHtml;

        SeedPublishCorrelation.TryReadDescriptionMarkerIdentity(description, out var identity).ShouldBeTrue();
        identity.ShouldBe(other.Identity);
        Correlation.ContainsDescriptionMarker(description).ShouldBeFalse();
        Correlation.TryRemoveDescriptionMarker(description, out var remaining).ShouldBeFalse();
        remaining.ShouldBe(description);
    }

    [Theory]
    [InlineData("<p>{marker}</p><p>{marker}</p>")]
    [InlineData("<p>{marker}</p><p>Twig publish origin v1: 019a0000-0000-7000-8000-000000000002</p>")]
    [InlineData("<p>{marker}</p><p>Twig publish origin v2: {id}</p>")]
    [InlineData("<p>{marker}</p><!-- Twig publish origin v1: {id} -->")]
    public void MultipleReservedMarkers_AreNeverAttributedOrRemoved(string template)
    {
        var description = Expand(template);

        SeedPublishCorrelation.HasDescriptionMarkerContent(description).ShouldBeTrue();
        SeedPublishCorrelation.TryReadDescriptionMarkerIdentity(description, out _).ShouldBeFalse();
        Correlation.ContainsDescriptionMarker(description).ShouldBeFalse();
        Correlation.TryRemoveDescriptionMarker(description, out var remaining).ShouldBeFalse();
        remaining.ShouldBe(description);
    }

    [Theory]
    [InlineData("{marker}")]
    [InlineData("<p>{marker}")]
    [InlineData("<p>{marker} human suffix</p>")]
    [InlineData("<p class='marker'>{marker}</p>")]
    [InlineData("<p>Twig <b>publish</b> origin v1: {id}</p>")]
    [InlineData("<!-- <p>{marker}</p> -->")]
    [InlineData("<div hidden><p>{marker}</p></div>")]
    [InlineData("<script><p>{marker}</p></script>")]
    [InlineData("<p>Twig publish origin v2: {id}</p>")]
    [InlineData("<p>Twig publish origin v1: not-a-guid</p>")]
    [InlineData("<p>Twig publish origin v1: 00000000-0000-0000-0000-000000000000</p>")]
    [InlineData("<p>{marker}</p><div>")]
    public void UnsupportedReservedContent_FailsClosedWithoutRemovingHumanHtml(string template)
    {
        var description = "<p>Preserve human description.</p>" + Expand(template);

        SeedPublishCorrelation.HasDescriptionMarkerContent(description).ShouldBeTrue();
        SeedPublishCorrelation.TryReadDescriptionMarkerIdentity(description, out _).ShouldBeFalse();
        Correlation.ContainsDescriptionMarker(description).ShouldBeFalse();
        Correlation.TryRemoveDescriptionMarker(description, out var remaining).ShouldBeFalse();
        remaining.ShouldBe(description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<p>Ordinary human HTML.</p>")]
    public void AbsentMarker_LeavesDescriptionUnchanged(string? description)
    {
        Correlation.ContainsDescriptionMarker(description).ShouldBeFalse();
        SeedPublishCorrelation.HasDescriptionMarkerContent(description).ShouldBeFalse();
        Correlation.TryRemoveDescriptionMarker(description, out var remaining).ShouldBeFalse();
        remaining.ShouldBe(description);
    }

    private static string Expand(string template) => template
        .Replace("{marker}", Correlation.DescriptionMarkerText, StringComparison.Ordinal)
        .Replace("{id}", Correlation.Identity.ToString(), StringComparison.Ordinal);
}
