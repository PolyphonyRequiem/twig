using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests the organization normalizer that keeps the connectionRef, stored
/// workItemUrl, and URL origin validation aligned when contributors check in
/// different shapes or casings of <c>twig.json</c>'s <c>organization</c>.
/// The canonical form is lowercase invariant across every supported shape —
/// ADO already routes slugs case-insensitively, so a mixed-casing check-in
/// must not surface as <c>attachment-connection-mismatch</c>.
/// </summary>
public sealed class OrganizationNormalizerTests
{
    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("  contoso  ", "contoso")]
    [InlineData("contoso/", "contoso")]
    [InlineData("Contoso", "contoso")]
    [InlineData("CONTOSO", "contoso")]
    [InlineData("https://dev.azure.com/contoso", "contoso")]
    [InlineData("https://dev.azure.com/contoso/", "contoso")]
    [InlineData("https://dev.azure.com/CONTOSO", "contoso")]
    [InlineData("https://DEV.AZURE.COM/Contoso", "contoso")]
    [InlineData("https://contoso.visualstudio.com", "contoso")]
    [InlineData("https://contoso.visualstudio.com/", "contoso")]
    [InlineData("https://CONTOSO.VISUALSTUDIO.COM", "contoso")]
    [InlineData("https://Contoso.VisualStudio.com", "contoso")]
    public void ToSlug_reduces_every_supported_shape_to_the_same_lowercase_slug(string input, string expected)
    {
        OrganizationNormalizer.ToSlug(input).ShouldBe(expected);
    }

    [Fact]
    public void ConnectionRef_is_stable_across_slug_and_uri_shapes_and_casings()
    {
        var slug = ConnectionRefResolver.Compute("contoso", "proj");
        var upperSlug = ConnectionRefResolver.Compute("CONTOSO", "proj");
        var mixedSlug = ConnectionRefResolver.Compute("Contoso", "proj");
        var uri = ConnectionRefResolver.Compute("https://dev.azure.com/contoso", "proj");
        var upperUri = ConnectionRefResolver.Compute("https://dev.azure.com/CONTOSO", "proj");
        var legacy = ConnectionRefResolver.Compute("https://contoso.visualstudio.com", "proj");
        var legacyUpper = ConnectionRefResolver.Compute("https://CONTOSO.VISUALSTUDIO.COM", "proj");

        // Every shape must converge on the same connectionRef — otherwise the
        // system-store registry would treat casing variants as distinct
        // worktree bindings and reject the second contributor's attach.
        slug.ShouldBe(upperSlug);
        slug.ShouldBe(mixedSlug);
        slug.ShouldBe(uri);
        slug.ShouldBe(upperUri);
        slug.ShouldBe(legacy);
        slug.ShouldBe(legacyUpper);
    }

    [Fact]
    public void BuildWorkItemUrl_normalizes_org_regardless_of_input_shape_or_casing()
    {
        var fromSlug = AdoWorkItemUrlValidator.BuildWorkItemUrl("contoso", "proj", 42);
        var fromUpper = AdoWorkItemUrlValidator.BuildWorkItemUrl("CONTOSO", "proj", 42);
        var fromUri = AdoWorkItemUrlValidator.BuildWorkItemUrl("https://dev.azure.com/contoso", "proj", 42);
        var fromUriUpper = AdoWorkItemUrlValidator.BuildWorkItemUrl("https://dev.azure.com/CONTOSO", "proj", 42);
        fromSlug.ShouldBe(fromUri);
        fromSlug.ShouldBe(fromUpper);
        fromSlug.ShouldBe(fromUriUpper);
        fromSlug.ShouldContain("contoso/proj/_workitems/edit/42");
    }

    [Fact]
    public void OriginMatches_accepts_every_configured_shape_and_casing()
    {
        var url = "https://dev.azure.com/contoso/proj/_workitems/edit/1";
        AdoWorkItemUrlValidator.OriginMatches(url, "contoso", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "CONTOSO", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "https://dev.azure.com/contoso", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "https://dev.azure.com/CONTOSO", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "https://contoso.visualstudio.com", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "https://CONTOSO.VISUALSTUDIO.COM", "proj").ShouldBeTrue();
    }
}
