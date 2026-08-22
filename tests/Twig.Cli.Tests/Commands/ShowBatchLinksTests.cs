using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for the per-item <c>links</c> / <c>relations</c> arrays that
/// <c>twig show-batch --output json</c> emits (ADO #154) — the surface starchart consumes.
/// </summary>
/// <remarks>
/// These assert against PARSED JSON rather than substrings. A <c>ShouldContain("\"targetId\":
/// 200")</c> is satisfied by the token appearing anywhere in the document, including under the
/// wrong item, which is precisely the defect this card exists to fix.
/// </remarks>
public sealed class ShowBatchLinksTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly ShowCommand _cmd;

    public ShowBatchLinksTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();

        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            _workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, _linkRepo, 30, 30);

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, formatterFactory, hintEngine, new TwigConfiguration(),
            TelemetryClient: Substitute.For<ITelemetryClient>());

        var tempDir = Path.Combine(Path.GetTempPath(), "twig-showbatchlinks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var statusFieldReader = new StatusFieldConfigReader(
            new TwigPaths(tempDir, Path.Combine(tempDir, "config"), Path.Combine(tempDir, "twig.db")));

        _cmd = new ShowCommand(ctx, _workItemRepo, _linkRepo, syncCoordinatorFactory, statusFieldReader);
    }

    private void HaveItems(params int[] ids)
    {
        foreach (var id in ids)
            _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>())
                .Returns(new WorkItemBuilder(id, $"Item {id}").Build());
    }

    private void HaveLinks(params WorkItemLink[] links)
    {
        _linkRepo.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(links);
    }

    private static JsonElement ItemWithId(string json, int id)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        root.ValueKind.ShouldBe(JsonValueKind.Array);
        foreach (var el in root.EnumerateArray())
        {
            if (el.GetProperty("id").GetInt32() == id)
                return el.Clone();
        }

        throw new Xunit.Sdk.XunitException($"No item with id {id} in batch output: {json}");
    }

    /// <summary>
    /// 🔴 The card's headline acceptance criterion: links for EVERY id, attributed to the
    /// right one. The fixture gives the two items DIFFERENT targets so an implementation
    /// that hands every item the whole edge set goes red.
    /// </summary>
    [Fact]
    public async Task ShowBatch_Json_ReturnsLinksForEveryIdAttributedToTheCorrectItem()
    {
        HaveItems(10, 20);
        HaveLinks(
            new WorkItemLink(10, 200, LinkTypes.Predecessor),
            new WorkItemLink(20, 201, LinkTypes.Successor));

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20", "json"));

        var ten = ItemWithId(output, 10);
        var twenty = ItemWithId(output, 20);

        var tenLinks = ten.GetProperty("links").EnumerateArray().ToList();
        tenLinks.Count.ShouldBe(1);
        tenLinks[0].GetProperty("sourceId").GetInt32().ShouldBe(10);
        tenLinks[0].GetProperty("targetId").GetInt32().ShouldBe(200);
        tenLinks[0].GetProperty("linkType").GetString().ShouldBe(LinkTypes.Predecessor);

        var twentyLinks = twenty.GetProperty("links").EnumerateArray().ToList();
        twentyLinks.Count.ShouldBe(1);
        twentyLinks[0].GetProperty("targetId").GetInt32().ShouldBe(201);
        twentyLinks[0].GetProperty("linkType").GetString().ShouldBe(LinkTypes.Successor);
    }

    /// <summary>
    /// AB#618: <c>commentCount</c> reaches the batch rows too. Both surfaces project through
    /// <c>BuildCoreCells</c>, so this pins the shared helper rather than restating the
    /// single-item contract — if someone promotes the key on the document path only, this
    /// goes red.
    /// </summary>
    [Fact]
    public async Task ShowBatch_Json_EmitsCommentCountPerItem()
    {
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(10, "Commented")
                .WithField("System.CommentCount", "3").Build());
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(20, "Uncommented").Build());
        HaveLinks();

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20", "json"));

        ItemWithId(output, 10).GetProperty("commentCount").GetInt32().ShouldBe(3);

        // Present-and-zero, never absent — same rule as links/relations above.
        var twenty = ItemWithId(output, 20);
        twenty.TryGetProperty("commentCount", out var commentCount).ShouldBeTrue();
        commentCount.ValueKind.ShouldBe(JsonValueKind.Number);
        commentCount.GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task ShowBatch_Json_ItemWithNoLinks_EmitsEmptyArraysNotMissingKeys()
    {
        HaveItems(10, 20);
        HaveLinks(new WorkItemLink(10, 200, LinkTypes.Related));

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20", "json"));

        var twenty = ItemWithId(output, 20);

        // Present-and-empty, never absent — missing-vs-empty ambiguity breaks integrators.
        twenty.TryGetProperty("links", out var links).ShouldBeTrue();
        links.ValueKind.ShouldBe(JsonValueKind.Array);
        links.GetArrayLength().ShouldBe(0);

        twenty.TryGetProperty("relations", out var relations).ShouldBeTrue();
        relations.ValueKind.ShouldBe(JsonValueKind.Array);
        relations.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ShowBatch_Json_EmitsAdoShapedRelationsAlongsideLinks()
    {
        HaveItems(10);
        HaveLinks(new WorkItemLink(10, 200, LinkTypes.Predecessor));

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10", "json"));

        var relations = ItemWithId(output, 10).GetProperty("relations").EnumerateArray().ToList();
        relations.Count.ShouldBe(1);
        relations[0].GetProperty("id").GetInt32().ShouldBe(200);
        // The ADO reference name, not the friendly one — this is what polyphony's client reads.
        relations[0].GetProperty("rel").GetString().ShouldBe("System.LinkTypes.Dependency-Reverse");
        relations[0].GetProperty("attributes").GetProperty("name").GetString().ShouldBe(LinkTypes.Predecessor);
    }

    /// <summary>
    /// One plural call, not one call per id — the whole reason the repository grew an
    /// overload rather than the command growing a loop.
    /// </summary>
    [Fact]
    public async Task ShowBatch_ReadsLinksWithOnePluralCallRatherThanOnePerId()
    {
        HaveItems(10, 20, 30);
        HaveLinks();

        await _cmd.ExecuteBatchAsync("10,20,30", "json");

        await _linkRepo.Received(1).GetLinksForSetAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 3 && ids.Contains(10) && ids.Contains(20) && ids.Contains(30)),
            Arg.Any<CancellationToken>());
        await _linkRepo.DidNotReceive().GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowBatch_DoesNotAskForLinksOfIdsThatWereNotFound()
    {
        HaveItems(10);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        HaveLinks();

        await _cmd.ExecuteBatchAsync("10,99", "json");

        await _linkRepo.Received(1).GetLinksForSetAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 10),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowBatch_EmptyBatch_DoesNotTouchTheLinkStore()
    {
        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("", "json"));

        result.ShouldBe(0);
        output.Trim().ShouldBe("[]");
        await _linkRepo.DidNotReceive().GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A link-store failure must degrade the read to items-without-edges rather than fail the
    /// whole batch — the same best-effort contract the single-item path has.
    /// </summary>
    [Fact]
    public async Task ShowBatch_LinkStoreFailure_StillReturnsItemsWithEmptyLinkArrays()
    {
        HaveItems(10);
        _linkRepo.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkItemLink>>(_ => throw new InvalidOperationException("link store down"));

        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10", "json"));

        result.ShouldBe(0);
        var ten = ItemWithId(output, 10);
        ten.GetProperty("links").GetArrayLength().ShouldBe(0);
    }

    /// <summary>
    /// The batch output is a top-level ARRAY at every count — including one. Adding per-row
    /// array cells must not change that, or every existing consumer breaks.
    /// </summary>
    [Fact]
    public async Task ShowBatch_Json_RemainsATopLevelArrayWithLinksPresent()
    {
        HaveItems(10);
        HaveLinks(new WorkItemLink(10, 200, LinkTypes.Related));

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10", "json"));

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(1);
    }

    /// <summary>
    /// The <c>ids</c> format must keep emitting one work-item id per line. The new
    /// <c>links</c>/<c>relations</c> cells contain nested <c>id</c> keys (a relation's TARGET
    /// id), so a renderer that walks into them indiscriminately would corrupt this output.
    /// </summary>
    [Fact]
    public async Task ShowBatch_IdsFormat_IsNotPollutedByRelationTargetIds()
    {
        HaveItems(10, 20);
        HaveLinks(
            new WorkItemLink(10, 777, LinkTypes.Predecessor),
            new WorkItemLink(20, 888, LinkTypes.Successor));

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20", "ids"));

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        lines.ShouldBe(["10", "20"]);
        lines.ShouldNotContain("777");
        lines.ShouldNotContain("888");
    }
}
