using NSubstitute;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Bridges the one-iteration repository stubs used by the ReadTools MCP fixtures to the
/// batch-local-cache <see cref="IWorkItemRepository.GetByIterationsAsync"/> read ReadTools
/// now issues (ADO #1106). Production repositories implement both APIs; this exists only
/// because NSubstitute does not run an interface implementation and otherwise returns an
/// empty batch regardless of the arranged rows.
/// </summary>
internal static class ReadToolsRepoBridges
{
    public static void BridgeBatchIterationReads(this IWorkItemRepository repository)
    {
        repository.GetByIterationsAsync(
                Arg.Any<IReadOnlyList<IterationPath>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => ReadArrangedIterationsAsync(
                repository,
                call.ArgAt<IReadOnlyList<IterationPath>>(0),
                call.ArgAt<CancellationToken>(1)));
    }

    private static async Task<IReadOnlyList<WorkItem>> ReadArrangedIterationsAsync(
        IWorkItemRepository repository,
        IReadOnlyList<IterationPath> iterations,
        CancellationToken ct)
    {
        var items = new List<WorkItem>();
        var seen = new HashSet<int>();
        foreach (var iteration in iterations)
        {
            var arranged = await repository.GetByIterationAsync(iteration, ct);
            foreach (var item in arranged)
            {
                if (seen.Add(item.Id))
                    items.Add(item);
            }
        }
        return items;
    }
}
