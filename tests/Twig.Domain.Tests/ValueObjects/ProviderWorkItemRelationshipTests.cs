using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class ProviderWorkItemRelationshipTests
{
    private static readonly ExternalWorkItemIdentity Parent = new("ado", "org-1", "1");
    private static readonly ExternalWorkItemIdentity Child = new("ado", "org-1", "2");

    [Fact]
    public void ParentChild_PreservesParentToChildDirection()
    {
        var relationship = new ProviderWorkItemRelationship(Parent, Child, LinkKind.ParentChild);

        relationship.Source.ShouldBe(Parent);
        relationship.Target.ShouldBe(Child);
    }

    [Fact]
    public void PredecessorSuccessor_PreservesPredecessorToSuccessorDirection()
    {
        var relationship = new ProviderWorkItemRelationship(Parent, Child, LinkKind.PredecessorSuccessor);

        relationship.Source.ShouldBe(Parent);
        relationship.Target.ShouldBe(Child);
    }

    [Fact]
    public void Related_NormalizesEndpointOrderForUniqueness()
    {
        var forward = new ProviderWorkItemRelationship(Parent, Child, LinkKind.Related);
        var reverse = new ProviderWorkItemRelationship(Child, Parent, LinkKind.Related);

        forward.ShouldBe(reverse);
    }

    [Fact]
    public void Artifact_IsNotAWorkItemRelationship()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ProviderWorkItemRelationship(Parent, Child, LinkKind.Artifact));
    }

    [Fact]
    public void SelfRelationship_IsRejected()
    {
        Should.Throw<ArgumentException>(
            () => new ProviderWorkItemRelationship(Parent, Parent, LinkKind.Related));
    }

    [Theory]
    [InlineData(LinkKind.ParentChild, "parent-child")]
    [InlineData(LinkKind.PredecessorSuccessor, "predecessor-successor")]
    [InlineData(LinkKind.Related, "related")]
    [InlineData(LinkKind.Artifact, "artifact")]
    public void LinkKindNames_UsesCanonicalStableNames(LinkKind kind, string expected)
    {
        LinkKindNames.ToStableName(kind).ShouldBe(expected);
        LinkKindNames.TryParseStableName(expected, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(kind);
    }

    [Fact]
    public void LinkKindNames_RejectsNonCanonicalSpelling()
    {
        LinkKindNames.TryParseStableName("ParentChild", out _).ShouldBeFalse();
    }
}
