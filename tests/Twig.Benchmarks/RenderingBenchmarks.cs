using BenchmarkDotNet.Attributes;
using Spectre.Console;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Rendering;

namespace Twig.Benchmarks;

/// <summary>
/// Benchmarks for tree rendering throughput.
/// Uses a TestConsole to capture output without terminal overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RenderingBenchmarks
{
    private TestConsole _console = null!;
    private SpectreRenderer _renderer = null!;
    private List<WorkItem> _items = null!;

    [Params(10, 100, 500)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _console = new TestConsole();
        _console.Profile.Width = 120;
        var theme = new SpectreTheme(new Twig.Infrastructure.Config.DisplayConfig());
        _renderer = new SpectreRenderer(_console, theme);

        var mapper = new WorkItemMapper();
        _items = new List<WorkItem>(NodeCount);
        for (var i = 1; i <= NodeCount; i++)
        {
            var snapshot = new WorkItemSnapshot
            {
                Id = i,
                Title = $"Work Item {i} - A medium-length title for realistic rendering",
                State = i % 3 == 0 ? "Done" : "Active",
                TypeName = i <= 5 ? "Epic" : (i <= 20 ? "Issue" : "Task"),
                AssignedTo = "Daniel Green",
                AreaPath = "Project\\Team\\SubTeam",
                IterationPath = "Project\\Sprint 1",
                ParentId = i <= 5 ? null : (i <= 20 ? ((i - 6) % 5) + 1 : ((i - 21) % 15) + 6),
                Revision = i,
            };
            _items.Add(mapper.Map(snapshot));
        }
    }

    [Benchmark(Description = "RenderTree (full tree output)")]
    public async Task RenderTree()
    {
        var focused = _items[0];
        var children = _items.Where(x => x.ParentId == focused.Id).ToList();
        var parentChain = new List<WorkItem>();

        await _renderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focused),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(parentChain),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(children),
            maxDepth: 3,
            activeId: focused.Id,
            ct: CancellationToken.None);
    }

    [Benchmark(Description = "RenderWorkItem (single card)")]
    public async Task RenderWorkItem()
    {
        var item = _items[NodeCount / 2];

        await _renderer.RenderWorkItemAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            showDirty: false,
            ct: CancellationToken.None);
    }
}
