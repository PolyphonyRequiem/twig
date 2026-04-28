using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

/// <summary>
/// Unit tests for <see cref="SeedLinkTypeMapper"/>.
/// </summary>
public class SeedLinkTypeMapperTests
{
    [Theory]
    [InlineData(SeedLinkTypes.ParentChild, "System.LinkTypes.Hierarchy-Forward")]
    [InlineData(SeedLinkTypes.Blocks, "System.LinkTypes.Dependency-Forward")]
    [InlineData(SeedLinkTypes.BlockedBy, "System.LinkTypes.Dependency-Reverse")]
    [InlineData(SeedLinkTypes.DependsOn, "System.LinkTypes.Dependency-Reverse")]
    [InlineData(SeedLinkTypes.DependedOnBy, "System.LinkTypes.Dependency-Forward")]
    [InlineData(SeedLinkTypes.Related, "System.LinkTypes.Related")]
    [InlineData(SeedLinkTypes.Successor, "System.LinkTypes.Dependency-Forward")]
    [InlineData(SeedLinkTypes.Predecessor, "System.LinkTypes.Dependency-Reverse")]
    public void ToAdoRelationType_KnownType_ReturnsMappedValue(string seedLinkType, string expectedAdo)
    {
        SeedLinkTypeMapper.ToAdoRelationType(seedLinkType).ShouldBe(expectedAdo);
    }

    [Fact]
    public void ToAdoRelationType_UnknownType_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => SeedLinkTypeMapper.ToAdoRelationType("not-a-type"));
    }

    [Fact]
    public void ToAdoRelationType_EmptyString_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => SeedLinkTypeMapper.ToAdoRelationType(string.Empty));
    }

    [Theory]
    [InlineData(SeedLinkTypes.ParentChild, "System.LinkTypes.Hierarchy-Forward")]
    [InlineData(SeedLinkTypes.Blocks, "System.LinkTypes.Dependency-Forward")]
    [InlineData(SeedLinkTypes.BlockedBy, "System.LinkTypes.Dependency-Reverse")]
    [InlineData(SeedLinkTypes.DependsOn, "System.LinkTypes.Dependency-Reverse")]
    [InlineData(SeedLinkTypes.DependedOnBy, "System.LinkTypes.Dependency-Forward")]
    [InlineData(SeedLinkTypes.Related, "System.LinkTypes.Related")]
    [InlineData(SeedLinkTypes.Successor, "System.LinkTypes.Dependency-Forward")]
    [InlineData(SeedLinkTypes.Predecessor, "System.LinkTypes.Dependency-Reverse")]
    public void TryToAdoRelationType_KnownType_ReturnsTrueAndMappedValue(string seedLinkType, string expectedAdo)
    {
        SeedLinkTypeMapper.TryToAdoRelationType(seedLinkType, out var result).ShouldBeTrue();
        result.ShouldBe(expectedAdo);
    }

    [Fact]
    public void TryToAdoRelationType_UnknownType_ReturnsFalse()
    {
        SeedLinkTypeMapper.TryToAdoRelationType("not-a-type", out _).ShouldBeFalse();
    }

    [Fact]
    public void AllSeedLinkTypes_AreMapped()
    {
        // Ensure every value in SeedLinkTypes.All has a mapping
        foreach (var linkType in SeedLinkTypes.All)
        {
            SeedLinkTypeMapper.TryToAdoRelationType(linkType, out var adoType).ShouldBeTrue(
                $"SeedLinkTypes.All contains '{linkType}' which has no mapping.");
            adoType.ShouldNotBeNullOrEmpty();
        }
    }

}
