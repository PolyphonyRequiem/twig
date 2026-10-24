using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Capabilities;

public sealed class RelationshipProvenanceSnapshotTests
{
    private static ExternalWorkItemIdentity Id(string provider, string id)
        => new(provider, "scope", id);

    private static ProviderWorkItemRelationship Rel(string sourceProvider, string targetProvider, LinkKind kind)
        => new(Id(sourceProvider, "1"), Id(targetProvider, "2"), kind);

    [Theory]
    [InlineData(LinkKind.ParentChild)]
    [InlineData(LinkKind.PredecessorSuccessor)]
    [InlineData(LinkKind.Related)]
    public void MatchingProviderAndNativeKind_IsProviderNative(LinkKind kind)
    {
        var descriptor = ProviderCapabilityCatalog.Ado;

        RelationshipProvenance.OfProviderObservation(descriptor, Rel(descriptor.ProviderKind, descriptor.ProviderKind, kind))
            .ShouldBe(RelationshipOrigin.ProviderNative);
    }

    [Fact]
    public void NullDescriptor_IsUnknown()
        => RelationshipProvenance.OfProviderObservation(null, Rel("ado", "ado", LinkKind.Related)).ShouldBeNull();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EndpointFromAnotherProvider_IsUnknown(bool sourceMismatch, bool targetMismatch)
    {
        var descriptor = ProviderCapabilityCatalog.Ado;
        var relationship = Rel(
            sourceMismatch ? "other" : descriptor.ProviderKind,
            targetMismatch ? "other" : descriptor.ProviderKind,
            LinkKind.ParentChild);

        RelationshipProvenance.OfProviderObservation(descriptor, relationship).ShouldBeNull();
    }

    [Theory]
    [InlineData(CapabilityImplementation.Emulated)]
    [InlineData(CapabilityImplementation.Unsupported)]
    public void NonNativeKind_IsUnknown(CapabilityImplementation implementation)
    {
        var descriptor = new ProviderCapabilityDescriptor("test",
        [
            new RelationshipCapability(LinkKind.ParentChild, CapabilityImplementation.Native, EmulationStorage.None),
            new RelationshipCapability(LinkKind.Related, implementation,
                implementation == CapabilityImplementation.Emulated ? EmulationStorage.LocalOnly : EmulationStorage.None),
        ]);

        RelationshipProvenance.OfProviderObservation(descriptor, Rel("test", "test", LinkKind.Related)).ShouldBeNull();
        RelationshipProvenance.OfProviderObservation(descriptor, Rel("test", "test", LinkKind.ParentChild))
            .ShouldBe(RelationshipOrigin.ProviderNative);
    }

    [Fact]
    public void KindUndeclaredByDescriptor_IsUnknown()
    {
        var descriptor = new ProviderCapabilityDescriptor("test",
        [
            new RelationshipCapability(LinkKind.ParentChild, CapabilityImplementation.Native, EmulationStorage.None),
        ]);

        RelationshipProvenance.OfProviderObservation(descriptor, Rel("test", "test", LinkKind.PredecessorSuccessor))
            .ShouldBeNull();
    }
}
