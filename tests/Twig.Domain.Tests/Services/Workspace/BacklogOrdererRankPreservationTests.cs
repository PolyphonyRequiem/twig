using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

/// <summary>
/// Rank-preservation contract from #727 and #734: any Twig code that publishes
/// or links an item MUST NOT rewrite existing server rank on other items. The
/// only rank write allowed is on the newly published item, and its value MUST
/// be strictly greater than the sibling maximum (append semantics).
/// </summary>
/// <remarks>
/// Routed through the profile seam in spirit — <see cref="BacklogOrderer"/> is
/// the one place rank is ever written by Twig core, so this test guards the
/// invariant at the single write point rather than at every caller.
/// </remarks>
public sealed class BacklogOrdererRankPreservationTests
{
    private const string StackRankField = "Microsoft.VSTS.Common.StackRank";

    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IFieldDefinitionStore _fields = Substitute.For<IFieldDefinitionStore>();

    [Fact]
    public async Task TryOrderAsync_never_patches_any_id_other_than_the_new_item()
    {
        var sibling1 = new WorkItemBuilder(101, "sib1").WithField(StackRankField, "10").Build();
        var sibling2 = new WorkItemBuilder(102, "sib2").WithField(StackRankField, "20").Build();
        var newItem = new WorkItemBuilder(999, "new").Build();

        _fields.GetByReferenceNameAsync(StackRankField).Returns(new FieldDefinition(StackRankField, "Stack Rank", "double", false));
        _ado.FetchChildrenAsync(50).Returns([sibling1, sibling2, newItem]);
        _ado.FetchAsync(999).Returns(newItem);
        _ado.PatchAsync(999, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>()).Returns(1);

        var orderer = new BacklogOrderer(_ado, _fields);
        var result = await orderer.TryOrderAsync(999, parentId: 50);

        result.ShouldBeTrue();

        // Assertion 1: exactly one PatchAsync call, addressed to the new item id.
        await _ado.Received(1).PatchAsync(999, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>());
        await _ado.DidNotReceive().PatchAsync(101, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>());
        await _ado.DidNotReceive().PatchAsync(102, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>());

        // Assertion 2: the one patch only writes the rank field, only on the new item,
        // and with a value STRICTLY greater than the sibling maximum (append semantics).
        await _ado.Received(1).PatchAsync(999,
            Arg.Is<IReadOnlyList<FieldChange>>(changes =>
                changes.Count == 1
                && changes[0].FieldName == StackRankField
                && double.Parse(changes[0].NewValue!, System.Globalization.CultureInfo.InvariantCulture) > 20.0),
            Arg.Any<int>());
    }
}
