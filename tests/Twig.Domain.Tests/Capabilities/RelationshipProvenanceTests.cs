using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Capabilities;

public sealed class RelationshipProvenanceTests
{
    private static ProviderCapabilityDescriptor Descriptor(
        CapabilityImplementation parentChild,
        CapabilityImplementation dependency,
        CapabilityImplementation related)
    {
        static RelationshipCapability Cap(LinkKind kind, CapabilityImplementation implementation)
            => new(kind, implementation,
                implementation == CapabilityImplementation.Emulated ? EmulationStorage.LocalOnly : EmulationStorage.None);

        return new ProviderCapabilityDescriptor("test",
        [
            Cap(LinkKind.ParentChild, parentChild),
            Cap(LinkKind.PredecessorSuccessor, dependency),
            Cap(LinkKind.Related, related),
        ]);
    }

    [Theory]
    [InlineData(LinkTypes.Related, LinkKind.Related)]
    [InlineData(LinkTypes.Predecessor, LinkKind.PredecessorSuccessor)]
    [InlineData(LinkTypes.Successor, LinkKind.PredecessorSuccessor)]
    public void TryGetKind_MapsFriendlyLinkTypes(string friendly, LinkKind expected)
    {
        RelationshipProvenance.TryGetKind(friendly, out var kind).ShouldBeTrue();
        kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Artifact")]
    [InlineData("related")]
    public void TryGetKind_RejectsUnmappedTypes(string? friendly)
        => RelationshipProvenance.TryGetKind(friendly, out _).ShouldBeFalse();

    [Fact]
    public void NativeKind_IsProviderNative()
    {
        RelationshipProvenance.OfProviderObservation(ProviderCapabilityCatalog.Ado, LinkTypes.Related)
            .ShouldBe(RelationshipOrigin.ProviderNative);
        RelationshipProvenance.OfProviderObservation(ProviderCapabilityCatalog.GitHub, LinkTypes.Predecessor)
            .ShouldBe(RelationshipOrigin.ProviderNative);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonNativeKind_IsUnknown_NeverTwigLocal(bool emulated)
    {
        var implementation = emulated ? CapabilityImplementation.Emulated : CapabilityImplementation.Unsupported;
        var descriptor = Descriptor(CapabilityImplementation.Native, CapabilityImplementation.Native, implementation);

        RelationshipProvenance.OfProviderObservation(descriptor, LinkKind.Related).ShouldBeNull();
        RelationshipProvenance.OfProviderObservation(descriptor, LinkKind.ParentChild)
            .ShouldBe(RelationshipOrigin.ProviderNative);
    }

    [Fact]
    public void NoDescriptor_IsUnknown()
    {
        RelationshipProvenance.OfProviderObservation(null, LinkKind.Related).ShouldBeNull();
        RelationshipProvenance.OfProviderObservation(null, LinkTypes.Related).ShouldBeNull();
    }

    [Fact]
    public void UnmappedFriendlyType_IsUnknown()
        => RelationshipProvenance.OfProviderObservation(ProviderCapabilityCatalog.Ado, "Artifact").ShouldBeNull();

    [Fact]
    public void ToToken_UsesCanonicalTokens()
    {
        RelationshipProvenance.ToToken(RelationshipOrigin.ProviderNative).ShouldBe("provider-native");
        RelationshipProvenance.ToToken(RelationshipOrigin.TwigLocal).ShouldBe("twig-local");
        RelationshipProvenance.ToToken(null).ShouldBeNull();
    }
}
