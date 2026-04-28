using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Xunit;

namespace Twig.Domain.Tests.Services.Navigation;

/// <summary>
/// Unit tests for <see cref="LinkTypeMapper"/>.
/// </summary>
public sealed class LinkTypeMapperTests
{
    [Theory]
    [InlineData("parent", "System.LinkTypes.Hierarchy-Reverse")]
    [InlineData("child", "System.LinkTypes.Hierarchy-Forward")]
    [InlineData("related", "System.LinkTypes.Related")]
    [InlineData("predecessor", "System.LinkTypes.Dependency-Reverse")]
    [InlineData("successor", "System.LinkTypes.Dependency-Forward")]
    public void TryResolve_KnownFriendlyName_ReturnsTrueAndAdoType(string friendly, string expectedAdo)
    {
        LinkTypeMapper.TryResolve(friendly, out var result).ShouldBeTrue();
        result.ShouldBe(expectedAdo);
    }

    [Theory]
    [InlineData("Parent", "System.LinkTypes.Hierarchy-Reverse")]
    [InlineData("CHILD", "System.LinkTypes.Hierarchy-Forward")]
    [InlineData("Related", "System.LinkTypes.Related")]
    [InlineData("PREDECESSOR", "System.LinkTypes.Dependency-Reverse")]
    [InlineData("Successor", "System.LinkTypes.Dependency-Forward")]
    public void TryResolve_CaseInsensitive_ReturnsTrueAndAdoType(string friendly, string expectedAdo)
    {
        LinkTypeMapper.TryResolve(friendly, out var result).ShouldBeTrue();
        result.ShouldBe(expectedAdo);
    }

    [Fact]
    public void TryResolve_UnknownType_ReturnsFalse()
    {
        LinkTypeMapper.TryResolve("not-a-type", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_EmptyString_ReturnsFalse()
    {
        LinkTypeMapper.TryResolve(string.Empty, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("parent", "System.LinkTypes.Hierarchy-Reverse")]
    [InlineData("child", "System.LinkTypes.Hierarchy-Forward")]
    [InlineData("related", "System.LinkTypes.Related")]
    [InlineData("predecessor", "System.LinkTypes.Dependency-Reverse")]
    [InlineData("successor", "System.LinkTypes.Dependency-Forward")]
    public void Resolve_KnownFriendlyName_ReturnsAdoType(string friendly, string expectedAdo)
    {
        LinkTypeMapper.Resolve(friendly).ShouldBe(expectedAdo);
    }

    [Fact]
    public void Resolve_UnknownType_ThrowsArgumentException()
    {
        var ex = Should.Throw<ArgumentException>(() => LinkTypeMapper.Resolve("not-a-type"));
        ex.Message.ShouldContain("not-a-type");
        ex.Message.ShouldContain("Supported types");
    }

    [Fact]
    public void Resolve_EmptyString_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => LinkTypeMapper.Resolve(string.Empty));
    }

    [Theory]
    [InlineData("System.LinkTypes.Hierarchy-Reverse", "parent")]
    [InlineData("System.LinkTypes.Hierarchy-Forward", "child")]
    [InlineData("System.LinkTypes.Related", "related")]
    [InlineData("System.LinkTypes.Dependency-Reverse", "predecessor")]
    [InlineData("System.LinkTypes.Dependency-Forward", "successor")]
    public void ToFriendlyName_KnownAdoType_ReturnsFriendlyName(string adoType, string expectedFriendly)
    {
        LinkTypeMapper.ToFriendlyName(adoType).ShouldBe(expectedFriendly);
    }

    [Fact]
    public void ToFriendlyName_UnknownAdoType_ThrowsArgumentException()
    {
        var ex = Should.Throw<ArgumentException>(() => LinkTypeMapper.ToFriendlyName("System.LinkTypes.Unknown"));
        ex.Message.ShouldContain("System.LinkTypes.Unknown");
    }

    [Fact]
    public void ToFriendlyName_EmptyString_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => LinkTypeMapper.ToFriendlyName(string.Empty));
    }

    [Theory]
    [InlineData("system.linktypes.hierarchy-reverse", "parent")]
    [InlineData("SYSTEM.LINKTYPES.HIERARCHY-FORWARD", "child")]
    [InlineData("SYSTEM.LINKTYPES.RELATED", "related")]
    [InlineData("system.linktypes.dependency-reverse", "predecessor")]
    [InlineData("System.LINKTYPES.Dependency-Forward", "successor")]
    public void ToFriendlyName_CaseInsensitive_ReturnsFriendlyName(string adoType, string expectedFriendly)
    {
        LinkTypeMapper.ToFriendlyName(adoType).ShouldBe(expectedFriendly);
    }

    [Theory]
    [InlineData("System.LinkTypes.Hierarchy-Reverse", "parent")]
    [InlineData("System.LinkTypes.Hierarchy-Forward", "child")]
    [InlineData("System.LinkTypes.Related", "related")]
    [InlineData("System.LinkTypes.Dependency-Reverse", "predecessor")]
    [InlineData("System.LinkTypes.Dependency-Forward", "successor")]
    public void TryToFriendlyName_KnownAdoType_ReturnsTrueAndFriendlyName(string adoType, string expectedFriendly)
    {
        LinkTypeMapper.TryToFriendlyName(adoType, out var result).ShouldBeTrue();
        result.ShouldBe(expectedFriendly);
    }

    [Fact]
    public void TryToFriendlyName_UnknownAdoType_ReturnsFalse()
    {
        LinkTypeMapper.TryToFriendlyName("System.LinkTypes.Unknown", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryToFriendlyName_EmptyString_ReturnsFalse()
    {
        LinkTypeMapper.TryToFriendlyName(string.Empty, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("system.linktypes.hierarchy-reverse", "parent")]
    [InlineData("SYSTEM.LINKTYPES.HIERARCHY-FORWARD", "child")]
    [InlineData("system.linktypes.dependency-forward", "successor")]
    public void TryToFriendlyName_CaseInsensitive_ReturnsTrueAndFriendlyName(string adoType, string expectedFriendly)
    {
        LinkTypeMapper.TryToFriendlyName(adoType, out var result).ShouldBeTrue();
        result.ShouldBe(expectedFriendly);
    }

    [Fact]
    public void SupportedTypes_ContainsAllFiveFriendlyNames()
    {
        LinkTypeMapper.SupportedTypes.Count.ShouldBe(5);
        LinkTypeMapper.SupportedTypes.ShouldContain("parent");
        LinkTypeMapper.SupportedTypes.ShouldContain("child");
        LinkTypeMapper.SupportedTypes.ShouldContain("related");
        LinkTypeMapper.SupportedTypes.ShouldContain("predecessor");
        LinkTypeMapper.SupportedTypes.ShouldContain("successor");
    }

    [Fact]
    public void Bidirectionality_AllSupportedTypesHaveReverseMapping()
    {
        foreach (var friendly in LinkTypeMapper.SupportedTypes)
        {
            LinkTypeMapper.TryResolve(friendly, out var adoType).ShouldBeTrue(
                $"Forward mapping missing for '{friendly}'.");
            LinkTypeMapper.TryToFriendlyName(adoType, out var reversed).ShouldBeTrue(
                $"Reverse mapping missing for ADO type '{adoType}' (from friendly '{friendly}').");
            reversed.ShouldBe(friendly);
        }
    }
}
