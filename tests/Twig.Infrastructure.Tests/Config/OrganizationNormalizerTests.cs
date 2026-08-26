using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests the organization normalizer that keeps the connectionRef, stored
/// workItemUrl, and URL origin validation aligned when contributors check in
/// different shapes of <c>twig.json</c>'s <c>organization</c>.
/// </summary>
public sealed class OrganizationNormalizerTests
{
    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("  contoso  ", "contoso")]
    [InlineData("contoso/", "contoso")]
    [InlineData("https://dev.azure.com/contoso", "contoso")]
    [InlineData("https://dev.azure.com/contoso/", "contoso")]
    [InlineData("https://contoso.visualstudio.com", "contoso")]
    [InlineData("https://contoso.visualstudio.com/", "contoso")]
    [InlineData("https://CONTOSO.VISUALSTUDIO.COM", "CONTOSO")]
    public void ToSlug_reduces_every_supported_shape_to_the_same_slug(string input, string expected)
    {
        OrganizationNormalizer.ToSlug(input).ShouldBe(expected);
    }

    [Fact]
    public void ConnectionRef_is_stable_across_slug_and_uri_shapes()
    {
        var slug = ConnectionRefResolver.Compute("contoso", "proj");
        var uri = ConnectionRefResolver.Compute("https://dev.azure.com/contoso", "proj");
        var legacy = ConnectionRefResolver.Compute("https://contoso.visualstudio.com", "proj");
        slug.ShouldBe(uri);
        slug.ShouldBe(legacy);
    }

    [Fact]
    public void BuildWorkItemUrl_normalizes_org_regardless_of_input_shape()
    {
        var fromSlug = AdoWorkItemUrlValidator.BuildWorkItemUrl("contoso", "proj", 42);
        var fromUri = AdoWorkItemUrlValidator.BuildWorkItemUrl("https://dev.azure.com/contoso", "proj", 42);
        fromSlug.ShouldBe(fromUri);
        fromSlug.ShouldContain("contoso/proj/_workitems/edit/42");
    }

    [Fact]
    public void OriginMatches_accepts_both_configured_shapes()
    {
        var url = "https://dev.azure.com/contoso/proj/_workitems/edit/1";
        AdoWorkItemUrlValidator.OriginMatches(url, "contoso", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "https://dev.azure.com/contoso", "proj").ShouldBeTrue();
        AdoWorkItemUrlValidator.OriginMatches(url, "https://contoso.visualstudio.com", "proj").ShouldBeTrue();
    }
}
