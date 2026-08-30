using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// AB#735 criterion (c), rank half: LINK operations must preserve server-owned
/// backlog rank.
/// </summary>
/// <remarks>
/// <para>
/// <c>BacklogOrdererRankPreservationTests</c> already covers the publish half —
/// the one place Twig deliberately WRITES rank. This covers the half nothing
/// asserted: link operations, which have no business touching rank at all.
/// </para>
/// <para>
/// 🔴 The assertion is that the link path issues no field write whatsoever, not
/// that it writes no rank field. Those differ in the direction that matters: a
/// future link implementation that "helpfully" re-stamped an ordering field
/// while relinking would satisfy a rank-specific assertion only until someone
/// renamed the field, whereas "links move edges, never fields" is the actual
/// invariant and cannot be satisfied accidentally. ADO reorders siblings on
/// reparent server-side; Twig's contract is to let it, not to reimplement it.
/// </para>
/// </remarks>
public sealed class PlanLinkOperationRankPreservationTests
{
    private const string StackRankField = "Microsoft.VSTS.Common.StackRank";
    private const string BacklogPriorityField = "Microsoft.VSTS.Common.BacklogPriority";

    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IRevisionBoundAdoWorkItemService _revisionBound =
        Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly IFieldDefinitionStore _fieldDefinitions = Substitute.For<IFieldDefinitionStore>();
    private readonly PlanOperationExecutor _executor;

    public PlanLinkOperationRankPreservationTests()
    {
        _revisionBound.AddLinkAtRevisionAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(8);
        _revisionBound.RemoveLinkAtRevisionAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(9);

        var publisher = new PlanSeedPublisher(
            _ado,
            Substitute.For<IWorkItemRepository>(),
            Substitute.For<ISeedLinkRepository>(),
            Substitute.For<IStagedIdentityRegistry>(),
            Substitute.For<IPublishIdMapRepository>(),
            Substitute.For<IPublishIntentRepository>(),
            (_, _) => Task.FromResult(new SeedPublishResult { Status = SeedPublishStatus.Error }));

        _executor = new PlanOperationExecutor(_ado, _revisionBound, _fieldDefinitions, publisher);
    }

    public static TheoryData<string> Relations() => new()
    {
        "parent", "predecessor", "successor", "related",
    };

    [Theory]
    [MemberData(nameof(Relations))]
    public async Task Add_link_writes_no_fields(string relation)
    {
        var op = new AddLinkOperation
        {
            Id = "op-add",
            WorkItemId = 100,
            ExpectedRevision = 7,
            Relation = relation,
            OtherId = 200,
        };

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Applied);
        await _revisionBound.Received(1).AddLinkAtRevisionAsync(
            100, Arg.Any<string>(), 200, 7, Arg.Any<CancellationToken>());
        await AssertNoFieldWrites();
    }

    [Theory]
    [MemberData(nameof(Relations))]
    public async Task Remove_link_writes_no_fields(string relation)
    {
        var op = new RemoveLinkOperation
        {
            Id = "op-remove",
            WorkItemId = 100,
            ExpectedRevision = 7,
            Relation = relation,
            OtherId = 200,
        };

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Applied);
        await _revisionBound.Received(1).RemoveLinkAtRevisionAsync(
            100, Arg.Any<string>(), 200, 7, Arg.Any<CancellationToken>());
        await AssertNoFieldWrites();
    }

    /// <summary>
    /// The neighbouring endpoint is never touched either. A link is one write on
    /// one item at one revision; ADO maintains the reciprocal side.
    /// </summary>
    [Fact]
    public async Task Link_operations_never_write_to_the_other_endpoint()
    {
        var op = new AddLinkOperation
        {
            Id = "op-add",
            WorkItemId = 100,
            ExpectedRevision = 7,
            Relation = "parent",
            OtherId = 200,
        };

        await _executor.ExecuteAsync(op, CancellationToken.None);

        await _revisionBound.DidNotReceive().AddLinkAtRevisionAsync(
            200, Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _ado.DidNotReceive().PatchAsync(
            200, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertNoFieldWrites()
    {
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Belt-and-braces on the specific fields the criterion names, so a
        // failure reads as "rank was rewritten" rather than only "a field was".
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(),
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Any(f => f.FieldName == StackRankField || f.FieldName == BacklogPriorityField)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }
}
