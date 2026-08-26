using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests the organization normalizer that keeps the stored workItemUrl and
/// URL origin validation aligned when contributors check in different shapes
/// or casings of <c>twig.json</c>'s <c>organization</c>. The
/// <see cref="ConnectionRefResolver"/> hash intentionally does NOT normalize
/// — T1 §5.1 hashes the configured strings opaquely, so two shapes yield
/// two rows; the single-source-of-truth twig.json fixes the canonical shape
/// at the team level. Normalization applies only where the value is
/// projected through URL construction or validation.
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
    public void ConnectionRef_hashes_the_configured_strings_opaquely()
    {
        // T1 §5.1 fixes the org+project payload as opaque; two check-in
        // shapes of the same organization MUST produce two connectionRefs
        // so the storage tier never absorbs the drift silently.
        var slug = ConnectionRefResolver.Compute("contoso", "proj");
        var uriShape = ConnectionRefResolver.Compute("https://dev.azure.com/contoso", "proj");
        slug.ShouldNotBe(uriShape);
        // But identical inputs must produce identical refs.
        slug.ShouldBe(ConnectionRefResolver.Compute("contoso", "proj"));
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
