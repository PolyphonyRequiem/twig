using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SeedLinkPromoter"/>.
/// </summary>
public class SeedLinkPromoterTests
{
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly SeedLinkPromoter _promoter;

    public SeedLinkPromoterTests()
    {
        _promoter = new SeedLinkPromoter(_seedLinkRepo, _adoService);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Both endpoints positive → promotes link
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_BothPositive_CallsAddLinkAsync()
    {
        var links = new[]
        {
            new SeedLink(100, 200, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.Received(1).AddLinkAsync(100, 200, "System.LinkTypes.Dependency-Reverse", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Other endpoint still negative → skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_OtherEndpointNegative_SkipsLink()
    {
        var links = new[]
        {
            new SeedLink(100, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.DidNotReceive().AddLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent-child link where newly published item is child → skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_ParentChildAsChild_SkipsLink()
    {
        // newId=100 is the child (SourceId=100 in parent-child link)
        var links = new[]
        {
            new SeedLink(100, 200, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.DidNotReceive().AddLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent-child link where newly published item is parent → promotes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_ParentChildAsParent_PromotesLink()
    {
        // newId=100 is the parent (TargetId=100 in parent-child link, SourceId=200 is child)
        var links = new[]
        {
            new SeedLink(200, 100, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.Received(1).AddLinkAsync(200, 100, "System.LinkTypes.Hierarchy-Forward", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Link promotion failure → warning, not exception
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_AdoFails_ReturnsWarning()
    {
        var links = new[]
        {
            new SeedLink(100, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);
        _adoService.AddLinkAsync(100, 200, "System.LinkTypes.Related", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("ADO error"));

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("Failed to create ADO link");
        warnings[0].ShouldContain("ADO error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No links → no warnings, no calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_NoLinks_ReturnsEmpty()
    {
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.DidNotReceive().AddLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple links — only eligible ones promoted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_MixedLinks_PromotesOnlyEligible()
    {
        var links = new[]
        {
            // Both positive, blocks → promote
            new SeedLink(100, 300, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
            // Other endpoint negative → skip
            new SeedLink(100, -5, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            // Parent-child, newId is child → skip
            new SeedLink(100, 400, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.Received(1).AddLinkAsync(100, 300, "System.LinkTypes.Dependency-Forward", Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Source ID is zero → skipped (not positive)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PromoteLinksAsync_SourceIdZero_SkipsLink()
    {
        var links = new[]
        {
            new SeedLink(0, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(100, Arg.Any<CancellationToken>()).Returns(links);

        var warnings = await _promoter.PromoteLinksAsync(100);

        warnings.ShouldBeEmpty();
        await _adoService.DidNotReceive().AddLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
