using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Tests for <see cref="FieldDefinitionHasher"/>: determinism, order-independence,
/// prefix format, and distinct hashes for different inputs.
/// </summary>
public class FieldDefinitionHasherTests
{
    private static readonly FieldDefinition Title =
        new("System.Title", "Title", "string", false);

    private static readonly FieldDefinition Priority =
        new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false);

    private static readonly FieldDefinition State =
        new("System.State", "State", "string", false);

    [Fact]
    public void ComputeFieldHash_SameInput_ProducesSameHash()
    {
        var fields = new List<FieldDefinition> { Title, Priority };
        var hash1 = FieldDefinitionHasher.ComputeFieldHash(fields);
        var hash2 = FieldDefinitionHasher.ComputeFieldHash(fields);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void ComputeFieldHash_DifferentFieldSets_ProduceDifferentHashes()
    {
        var set1 = new List<FieldDefinition> { Title, Priority };
        var set2 = new List<FieldDefinition> { Title, State };

        var hash1 = FieldDefinitionHasher.ComputeFieldHash(set1);
        var hash2 = FieldDefinitionHasher.ComputeFieldHash(set2);

        hash1.ShouldNotBe(hash2);
    }

    [Fact]
    public void ComputeFieldHash_OrderIndependent_ShuffledInputProducesSameHash()
    {
        var ordered = new List<FieldDefinition> { Priority, State, Title };
        var shuffled = new List<FieldDefinition> { Title, Priority, State };

        var hash1 = FieldDefinitionHasher.ComputeFieldHash(ordered);
        var hash2 = FieldDefinitionHasher.ComputeFieldHash(shuffled);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void ComputeFieldHash_EmptyList_ProducesConsistentHash()
    {
        var empty = new List<FieldDefinition>();

        var hash1 = FieldDefinitionHasher.ComputeFieldHash(empty);
        var hash2 = FieldDefinitionHasher.ComputeFieldHash(empty);

        hash1.ShouldBe(hash2);
        hash1.ShouldStartWith("sha256:");
    }

    [Fact]
    public void ComputeFieldHash_StartsWithSha256Prefix()
    {
        var fields = new List<FieldDefinition> { Title };

        var hash = FieldDefinitionHasher.ComputeFieldHash(fields);

        hash.ShouldStartWith("sha256:");
        // SHA-256 produces 64 hex characters
        hash.Length.ShouldBe("sha256:".Length + 64);
    }
}
