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

public sealed class ShowBatchExportTests
{
    private readonly IWorkItemRepository _repo = Substitute.For<IWorkItemRepository>();
    private readonly IWorkItemLinkRepository _links = Substitute.For<IWorkItemLinkRepository>();
    private readonly IPendingChangeStore _pending = Substitute.For<IPendingChangeStore>();
    private readonly IAdoWorkItemService _remote = Substitute.For<IAdoWorkItemService>();
    private readonly StringWriter _stderr = new();
    private readonly ShowCommand _command;

    public ShowBatchExportTests()
    {
        _pending.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _links.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _links.GetLinksVerifiedAtForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DateTimeOffset>());
        var protectedWriter = new ProtectedCacheWriter(_repo, _pending);
        var sync = new SyncCoordinatorFactory(_repo, _remote, protectedWriter, _pending, _links, 30, 30);
        var formatters = new OutputFormatterFactory(new HumanOutputFormatter());
        var pipeline = new RenderingPipelineFactory(formatters, null!, isOutputRedirected: () => true);
        var context = new CommandContext(pipeline, formatters,
            new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration { Organization = "testorg", Project = "testproj" }, Stderr: _stderr);
        var scratch = Path.Combine(Path.GetTempPath(), "twig-export-test-" + Guid.NewGuid().ToString("N"));
        var paths = new TwigPaths(scratch, Path.Combine(scratch, "config"), Path.Combine(scratch, "twig.db"));
        _command = new ShowCommand(context, _repo, _links, sync,
            new StatusFieldConfigReader(paths), pendingChangeStore: _pending);
    }

    private void AllowOnlyTargets(IReadOnlyDictionary<int, WorkItem> targets, IReadOnlySet<int>? missing = null)
    {
        _repo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.ArgAt<int>(0);
            if (targets.TryGetValue(id, out var item)) return item;
            if (missing?.Contains(id) == true) return (WorkItem?)null;
            throw new InvalidOperationException($"Unrequested body read: {id}");
        });
        _repo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Child body read")));
        _repo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Parent body read")));
        _repo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Global dirty body read")));
        _repo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Global seed body read")));
        _repo.GetRootItemsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Global root body read")));
        _repo.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Unexpected plural body read")));
        _repo.ClearReceivedCalls();
    }

    private void AssertTargetOnlyRead()
    {
        // This is the scope/no-mutation boundary, not a pin on internal call counts.
        _repo.ReceivedCalls().Select(call => call.GetMethodInfo().Name)
            .ShouldAllBe(name => name == nameof(IWorkItemRepository.GetByIdAsync) || name == nameof(IWorkItemRepository.AcquireOperation));
        _links.ReceivedCalls().Select(call => call.GetMethodInfo().Name)
            .ShouldAllBe(name => name == nameof(IWorkItemLinkRepository.GetLinksForSetAsync) || name == nameof(IWorkItemLinkRepository.GetLinksVerifiedAtForSetAsync));
        _pending.ReceivedCalls().Select(call => call.GetMethodInfo().Name)
            .ShouldAllBe(name => name == nameof(IPendingChangeStore.GetDirtyItemIdsAsync));
        _remote.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_PreservesEveryStoredField_AndNeverLoadsRelativeBodies()
    {
        var html = "<div data-title=\"雪 & café\">\r\n<strong>Δ 😀</strong>" + new string('x', 8192) + "</div>";
        var fields = new Dictionary<string, string?>
        {
            ["Custom.MixedCASE"] = "Σ雪😀\r\n\t\"quoted\"\\value",
            ["Custom.Null"] = null,
            ["Custom.Empty"] = "",
            ["System.Description"] = html,
            ["System.State"] = "New",
            ["System.Tags"] = "One; 二 ;Three",
        };
        var seed = new WorkItemBuilder(-1, "Draft 雪").AsSeed().InState("Draft")
            .WithParent(900).WithFields(fields).Build();
        AllowOnlyTargets(new Dictionary<int, WorkItem> { [-1] = seed });
        _pending.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { -1, 777 });
        var verified = DateTimeOffset.Parse("2026-10-01T12:00:00+00:00");
        _links.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemLink(-1, 901, "System.LinkTypes.Related") });
        _links.GetLinksVerifiedAtForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DateTimeOffset> { [-1] = verified });

        var (exitCode, stdout) = await StdoutCapture.RunAsync(() =>
            _command.ExecuteBatchAsync("-1", "json", includeFields: true));

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        root.GetProperty("exportVersion").GetInt32().ShouldBe(1);
        root.GetProperty("connection").GetString().ShouldBe("testorg/testproj");
        root.GetProperty("requestedIds").EnumerateArray().Select(id => id.GetInt32()).ShouldBe(new[] { -1 });
        root.GetProperty("missing").GetArrayLength().ShouldBe(0);
        var exported = root.GetProperty("items").EnumerateArray().Single();
        exported.GetProperty("id").GetInt32().ShouldBe(-1);
        exported.GetProperty("revision").GetInt32().ShouldBe(0);
        exported.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
        exported.GetProperty("parentId").GetInt32().ShouldBe(900);
        exported.GetProperty("state").GetString().ShouldBe("Draft");
        exported.GetProperty("assignedTo").ValueKind.ShouldBe(JsonValueKind.Null);
        exported.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).ShouldBe(new[] { "One", "二", "Three" });
        var actualFields = exported.GetProperty("fields").EnumerateObject()
            .ToDictionary(field => field.Name, field => field.Value.GetString(), StringComparer.Ordinal);
        actualFields.Keys.Order().ShouldBe(fields.Keys.Order());
        foreach (var (key, value) in fields)
            actualFields[key].ShouldBe(value);
        var freshness = exported.GetProperty("freshness");
        freshness.GetProperty("hasLocalChanges").GetBoolean().ShouldBeTrue();
        freshness.GetProperty("lastSyncedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        freshness.GetProperty("linksVerifiedAt").GetDateTimeOffset().ShouldBe(verified);
        var link = exported.GetProperty("links").EnumerateArray().Single();
        link.GetProperty("sourceId").GetInt32().ShouldBe(-1);
        link.GetProperty("targetId").GetInt32().ShouldBe(901);
        link.GetProperty("linkType").GetString().ShouldBe("System.LinkTypes.Related");
        _stderr.ToString().ShouldBeEmpty();
        seed.State.ShouldBe("Draft");
        seed.IsDirty.ShouldBeFalse();
        AssertTargetOnlyRead();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(257)]
    [InlineData(1001)]
    public async Task Export_ReturnsExactRequestedSetWithoutAHiddenBatchLimit(int count)
    {
        var ids = Enumerable.Range(1, count).Reverse().ToArray();
        var targets = ids.ToDictionary(id => id, id => new WorkItemBuilder(id, $"Target {id}").Build());
        AllowOnlyTargets(targets);
        _links.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var sources = call.ArgAt<IReadOnlyList<int>>(0);
            if (sources.Count > 999 || sources.Any(id => !targets.ContainsKey(id)))
                throw new InvalidOperationException("Metadata query exceeds its boundary");
            return sources.Select(id => new WorkItemLink(id, id + count, LinkTypes.Related)).ToArray();
        });
        var (exitCode, stdout) = await StdoutCapture.RunAsync(() =>
            _command.ExecuteBatchAsync(string.Join(',', ids), "json", includeFields: true));

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("requestedIds").EnumerateArray().Select(id => id.GetInt32()).ShouldBe(ids);
        document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ShouldBe(ids);
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("fields").EnumerateObject().ShouldBeEmpty();
            var id = item.GetProperty("id").GetInt32();
            var link = item.GetProperty("links").EnumerateArray().Single();
            link.GetProperty("sourceId").GetInt32().ShouldBe(id);
            link.GetProperty("targetId").GetInt32().ShouldBe(id + count);
        }
        AssertTargetOnlyRead();
    }

    [Fact]
    public async Task Export_MissingPositiveAndNegativeTargets_ExitNonzeroWithCompleteEnvelope()
    {
        var item = new WorkItemBuilder(42, "Found").Build();
        item.MarkSynced(8);
        AllowOnlyTargets(new Dictionary<int, WorkItem> { [42] = item }, new HashSet<int> { 77, -8 });
        var (exitCode, stdout) = await StdoutCapture.RunAsync(() =>
            _command.ExecuteBatchAsync("77,42,-8", "json", includeFields: true));

        exitCode.ShouldBe(1);
        using var document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("requestedIds").EnumerateArray().Select(id => id.GetInt32()).ShouldBe(new[] { 77, 42, -8 });
        document.RootElement.GetProperty("missing").EnumerateArray().Select(id => id.GetInt32()).ShouldBe(new[] { 77, -8 });
        var exported = document.RootElement.GetProperty("items").EnumerateArray().Single();
        exported.GetProperty("id").GetInt32().ShouldBe(42);
        exported.GetProperty("revision").GetInt32().ShouldBe(8);
        exported.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
        exported.GetProperty("freshness").GetProperty("hasLocalChanges").GetBoolean().ShouldBeFalse();
        AssertTargetOnlyRead();
    }

    [Fact]
    public async Task Export_UnknownPendingEvidence_DoesNotClaimCleanOrEraseKnownDirtyFlag()
    {
        var clean = new WorkItemBuilder(42, "Unknown").Build();
        var dirty = new WorkItemBuilder(43, "Known dirty").Dirty().Build();
        AllowOnlyTargets(new Dictionary<int, WorkItem> { [42] = clean, [43] = dirty });
        _pending.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<int>>(new InvalidOperationException("Unavailable pending metadata")));
        var (exitCode, stdout) = await StdoutCapture.RunAsync(() =>
            _command.ExecuteBatchAsync("42,43", "json", includeFields: true));

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(stdout);
        var items = document.RootElement.GetProperty("items");
        items[0].GetProperty("freshness").GetProperty("hasLocalChanges").ValueKind.ShouldBe(JsonValueKind.Null);
        items[1].GetProperty("freshness").GetProperty("hasLocalChanges").GetBoolean().ShouldBeTrue();
        dirty.IsDirty.ShouldBeTrue();
        AssertTargetOnlyRead();
    }

    [Theory]
    [InlineData("human", null, null)]
    [InlineData("json-full", null, null)]
    [InlineData("json-compact", null, null)]
    [InlineData("json", "System.State", null)]
    [InlineData("json", null, "children")]
    public async Task Export_IncompatibleModes_FailBeforeRepositoryReads(string output, string? fields, string? sections)
    {
        _repo.ClearReceivedCalls();
        var (exitCode, stdout) = await StdoutCapture.RunAsync(() =>
            _command.ExecuteBatchAsync("42", output, fields: fields, sections: sections, includeFields: true));

        exitCode.ShouldBe(2);
        stdout.ShouldBeEmpty();
        _repo.ReceivedCalls().ShouldBeEmpty();
        _remote.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("42,garbage")]
    [InlineData("42,")]
    [InlineData("0")]
    [InlineData("42,42")]
    public async Task Export_InvalidIds_FailClosedInsteadOfSilentlyDroppingInputs(string batch)
    {
        var (exitCode, stdout) = await StdoutCapture.RunAsync(() =>
            _command.ExecuteBatchAsync(batch, "json", includeFields: true));

        exitCode.ShouldBe(2);
        stdout.ShouldBeEmpty();
        _repo.ReceivedCalls().ShouldBeEmpty();
    }
}
