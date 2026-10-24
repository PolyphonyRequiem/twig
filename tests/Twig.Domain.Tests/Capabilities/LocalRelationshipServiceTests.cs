using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Capabilities;

public sealed class LocalRelationshipServiceTests
{
    private static readonly ProviderCapabilityDescriptor Emulating = new(
        "test",
        [
            new(LinkKind.ParentChild, CapabilityImplementation.Emulated, EmulationStorage.LocalOnly),
            new(LinkKind.PredecessorSuccessor, CapabilityImplementation.Emulated, EmulationStorage.LocalWithProviderProjection),
            new(LinkKind.Related, CapabilityImplementation.Emulated, EmulationStorage.LocalOnly),
        ]);

    private readonly FakeRepository _repo = new();
    private readonly LocalRelationshipService _service;

    public LocalRelationshipServiceTests() => _service = new LocalRelationshipService(_repo);

    private static LocalWorkItemId Id(int value) =>
        LocalWorkItemId.FromGuid(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    [Theory]
    [InlineData(LinkKind.ParentChild)]
    [InlineData(LinkKind.Related)]
    public async Task Add_RefusesKindsThatAreNotEmulated(LinkKind kind)
    {
        var result = await _service.AddAsync(ProviderCapabilityCatalog.GitHub, Id(1), Id(2), kind);

        result.Status.ShouldBe(LocalRelationshipStatus.NotEmulated);
        _repo.Rows.ShouldBeEmpty();
        if (kind == LinkKind.Related)
            result.ReasonCode.ShouldBe(RelationshipRoute.UnsupportedReason);
    }

    [Fact]
    public async Task Remove_RefusesNativeKinds() =>
        (await _service.RemoveAsync(ProviderCapabilityCatalog.Ado, Id(1), Id(2), LinkKind.ParentChild))
            .Status.ShouldBe(LocalRelationshipStatus.NotEmulated);

    [Fact]
    public async Task Add_RejectsSelfEdge()
    {
        var result = await _service.AddAsync(Emulating, Id(5), Id(5), LinkKind.PredecessorSuccessor);
        result.Status.ShouldBe(LocalRelationshipStatus.Invalid);
        result.ReasonCode.ShouldBe(LocalRelationshipService.SelfEdgeReason);
    }

    [Fact]
    public async Task Add_RejectsSecondParent()
    {
        (await _service.AddAsync(Emulating, Id(1), Id(3), LinkKind.ParentChild)).Status.ShouldBe(LocalRelationshipStatus.Added);

        var second = await _service.AddAsync(Emulating, Id(2), Id(3), LinkKind.ParentChild);
        second.Status.ShouldBe(LocalRelationshipStatus.Invalid);
        second.ReasonCode.ShouldBe(LocalRelationshipService.SecondParentReason);
    }

    [Fact]
    public async Task Add_AllowsNewParentAfterOldOneRemoved()
    {
        await _service.AddAsync(Emulating, Id(1), Id(3), LinkKind.ParentChild);
        await _service.RemoveAsync(Emulating, Id(1), Id(3), LinkKind.ParentChild);

        (await _service.AddAsync(Emulating, Id(2), Id(3), LinkKind.ParentChild)).Status.ShouldBe(LocalRelationshipStatus.Added);
    }

    [Fact]
    public async Task Related_IsCanonicalised()
    {
        var added = await _service.AddAsync(Emulating, Id(9), Id(4), LinkKind.Related);
        added.Relationship!.SourceId.ShouldBe(Id(4));
        added.Relationship.TargetId.ShouldBe(Id(9));

        (await _service.AddAsync(Emulating, Id(4), Id(9), LinkKind.Related)).Status.ShouldBe(LocalRelationshipStatus.AlreadyPresent);
        (await _service.RemoveAsync(Emulating, Id(9), Id(4), LinkKind.Related)).Status.ShouldBe(LocalRelationshipStatus.Removed);
    }

    [Fact]
    public async Task DirectedKinds_AreNotCanonicalised()
    {
        var added = await _service.AddAsync(Emulating, Id(9), Id(4), LinkKind.PredecessorSuccessor);
        added.Relationship!.SourceId.ShouldBe(Id(9));
        (await _service.AddAsync(Emulating, Id(4), Id(9), LinkKind.PredecessorSuccessor)).Status.ShouldBe(LocalRelationshipStatus.Added);
    }

    [Theory]
    [InlineData(LinkKind.PredecessorSuccessor, "pending")]
    [InlineData(LinkKind.ParentChild, "not-applicable")]
    public async Task Add_SetsPublicationFromStorage(LinkKind kind, string expected)
    {
        var result = await _service.AddAsync(Emulating, Id(1), Id(2), kind);
        CapabilityTokens.ToToken(CapabilityTokens.Publication, result.Relationship!.Publication).ShouldBe(expected);
        result.Relationship.Origin.ShouldBe(RelationshipOrigin.TwigLocal);
        result.Relationship.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Add_IsIdempotent_AndRemoveOfMissingIsNotPresent()
    {
        await _service.AddAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor);
        (await _service.AddAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor)).Status.ShouldBe(LocalRelationshipStatus.AlreadyPresent);
        (await _service.RemoveAsync(Emulating, Id(7), Id(8), LinkKind.PredecessorSuccessor)).Status.ShouldBe(LocalRelationshipStatus.NotPresent);
    }

    [Fact]
    public async Task ReAdd_AfterTombstone_ResurrectsWithHigherRevision()
    {
        await _service.AddAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor);
        var removed = await _service.RemoveAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor);
        removed.Relationship!.Deleted.ShouldBeTrue();
        removed.Relationship.Revision.ShouldBe(2);

        (await _service.RemoveAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor)).Status.ShouldBe(LocalRelationshipStatus.NotPresent);

        var readded = await _service.AddAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor);
        readded.Status.ShouldBe(LocalRelationshipStatus.Added);
        readded.Relationship!.Deleted.ShouldBeFalse();
        readded.Relationship.Revision.ShouldBe(3);
    }

    [Fact]
    public async Task Add_ReportsConflictWhenRepositoryRejectsRevision()
    {
        _repo.ForceConflict = true;
        (await _service.AddAsync(Emulating, Id(1), Id(2), LinkKind.PredecessorSuccessor)).Status.ShouldBe(LocalRelationshipStatus.Conflict);
    }

    private sealed class FakeRepository : ILocalRelationshipRepository
    {
        public Dictionary<(LocalWorkItemId, LocalWorkItemId, LinkKind), LocalRelationship> Rows { get; } = new();
        public bool ForceConflict { get; set; }

        public Task<IReadOnlyList<LocalRelationship>> GetForItemsAsync(
            IReadOnlyCollection<LocalWorkItemId> itemIds, bool includeDeleted = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LocalRelationship>>(Rows.Values
                .Where(r => (itemIds.Contains(r.SourceId) || itemIds.Contains(r.TargetId)) && (includeDeleted || !r.Deleted))
                .ToArray());

        public Task<LocalRelationship?> GetAsync(
            LocalWorkItemId sourceId, LocalWorkItemId targetId, LinkKind kind, CancellationToken ct = default) =>
            Task.FromResult(Rows.GetValueOrDefault((sourceId, targetId, kind)));

        public Task<LocalRelationshipWrite> UpsertAsync(LocalRelationship relationship, long? expectedRevision, CancellationToken ct = default)
        {
            var key = (relationship.SourceId, relationship.TargetId, relationship.Kind);
            Rows.TryGetValue(key, out var current);
            if (ForceConflict || current?.Revision != expectedRevision)
                return Task.FromResult(new LocalRelationshipWrite(LocalRelationshipWriteStatus.Conflict, current));

            var written = relationship with { Revision = (current?.Revision ?? 0) + 1 };
            Rows[key] = written;
            return Task.FromResult(new LocalRelationshipWrite(LocalRelationshipWriteStatus.Written, written));
        }

        public Task<LocalRelationshipWrite> TombstoneAsync(
            LocalWorkItemId sourceId, LocalWorkItemId targetId, LinkKind kind, long expectedRevision, DateTimeOffset updatedAt, CancellationToken ct = default)
        {
            var key = (sourceId, targetId, kind);
            if (!Rows.TryGetValue(key, out var current) || current.Revision != expectedRevision)
                return Task.FromResult(new LocalRelationshipWrite(LocalRelationshipWriteStatus.Conflict, current));

            var written = current with
            {
                DeletedAt = updatedAt,
                Revision = current.Revision + 1,
                UpdatedAt = updatedAt,
            };
            Rows[key] = written;
            return Task.FromResult(new LocalRelationshipWrite(LocalRelationshipWriteStatus.Written, written));
        }
    }
}
