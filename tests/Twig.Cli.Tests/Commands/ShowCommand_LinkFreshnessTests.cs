using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#831: <c>twig show</c> must let a consumer tell a VERIFIED-empty link set apart from one this
/// cache has never fetched.
/// </summary>
/// <remarks>
/// 🔴 Every test in this file that asserts on <c>links</c>/<c>relations</c> pairs it with an
/// assertion on <c>linksVerifiedAt</c>, because the two empty arrays are byte-identical and the
/// arrays alone cannot fail. The reported live consequence of the defect was two agent sessions
/// concluding a work item had no blocking graph while its Predecessor edges existed on ADO.
/// </remarks>
public sealed class ShowCommand_LinkFreshnessTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();
    private readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly StringWriter _stderr = new();
    private readonly string _tempDir;
    private readonly ShowCommand _cmd;

    public ShowCommand_LinkFreshnessTests()
    {
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var adoService = Substitute.For<IAdoWorkItemService>();
        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, adoService);
        var iterationService = Substitute.For<IIterationService>();
        var workingSetService = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            _workItemRepo, adoService, protectedCacheWriter, _pendingChangeStore, _linkRepo, 30, 30);

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-links-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));

        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(
            pipelineFactory, formatterFactory, new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration(), TelemetryClient: Substitute.For<ITelemetryClient>(), Stderr: _stderr);

        _cmd = new ShowCommand(
            ctx,
            _workItemRepo,
            _linkRepo,
            syncCoordinatorFactory,
            new StatusFieldConfigReader(paths),
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: Substitute.For<IProcessConfigurationProvider>(),
            contextStore: _contextStore,
            activeItemResolver: activeItemResolver,
            pendingChangeStore: _pendingChangeStore,
            workingSetService: workingSetService);
    }

    public void Dispose()
    {
        _stderr.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Seeds a cached item with no edges. <paramref name="verifiedAt"/> null means this cache has
    /// never fetched the item's edges — the state the whole ticket is about.
    /// </summary>
    private void SetupCachedItem(WorkItem item, DateTimeOffset? verifiedAt, params WorkItemLink[] links)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(item.Id, Arg.Any<CancellationToken>()).Returns(links);
        _linkRepo.GetLinksVerifiedAtAsync(item.Id, Arg.Any<CancellationToken>()).Returns(verifiedAt);
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());
    }

    // ── Single item ─────────────────────────────────────────────────

    /// <summary>
    /// The headline case: two reads that used to be indistinguishable now differ in exactly one
    /// readable key.
    /// </summary>
    [Fact]
    public async Task Json_NeverFetchedEdges_AndVerifiedEmptyEdges_AreDistinguishable()
    {
        SetupCachedItem(new WorkItemBuilder(742, "Never fetched").Build(), verifiedAt: null);
        var neverFetched = await CaptureStdout(() => _cmd.ExecuteAsync(742, "json"));

        SetupCachedItem(new WorkItemBuilder(743, "Verified isolated").Build(),
            verifiedAt: DateTimeOffset.Parse("2026-08-28T05:00:00+00:00"));
        var verifiedEmpty = await CaptureStdout(() => _cmd.ExecuteAsync(743, "json"));

        // Precondition: the edge arrays really are identical, or the comparison is vacuous.
        using var a = JsonDocument.Parse(neverFetched);
        using var b = JsonDocument.Parse(verifiedEmpty);
        a.RootElement.GetProperty("links").GetArrayLength().ShouldBe(0);
        b.RootElement.GetProperty("links").GetArrayLength().ShouldBe(0);
        a.RootElement.GetProperty("relations").GetArrayLength().ShouldBe(0);
        b.RootElement.GetProperty("relations").GetArrayLength().ShouldBe(0);

        a.RootElement.GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        b.RootElement.GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    /// <summary>
    /// The key is ALWAYS present, never omitted — an absent key would reintroduce exactly the
    /// missing-vs-empty ambiguity it exists to resolve, in a new place.
    /// </summary>
    [Fact]
    public async Task Json_LinksVerifiedAt_IsAlwaysEmitted_EvenWhenUnverified()
    {
        SetupCachedItem(new WorkItemBuilder(742, "Never fetched").Build(), verifiedAt: null);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(742, "json"));

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.TryGetProperty("linksVerifiedAt", out var verified).ShouldBeTrue();
        verified.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Json_VerifiedEdges_CarryTheVerificationInstant()
    {
        var verifiedAt = DateTimeOffset.Parse("2026-08-28T05:24:13+00:00");
        SetupCachedItem(
            new WorkItemBuilder(742, "Blocked").Build(),
            verifiedAt,
            new WorkItemLink(742, 740, "Predecessor"));

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(742, "json"));

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("links").GetArrayLength().ShouldBe(1);
        DateTimeOffset.Parse(doc.RootElement.GetProperty("linksVerifiedAt").GetString()!)
            .ShouldBe(verifiedAt);
    }

    // ── Surface split: human hints, machine stays quiet ──────────────

    /// <summary>
    /// The rich surface says so in words, in the same idiom as the staleness hint: a human reading
    /// an empty Relations panel has no other way to learn the list means UNKNOWN.
    /// </summary>
    [Fact]
    public async Task Human_UnverifiedEdges_EmitAHintNamingTheAmbiguity()
    {
        SetupCachedItem(new WorkItemBuilder(742, "Never fetched").Build(), verifiedAt: null);

        await CaptureStdout(() => _cmd.ExecuteAsync(742, "human"));

        var hint = _stderr.ToString();
        hint.ShouldContain("#742");
        hint.ShouldContain("--refresh");
    }

    [Fact]
    public async Task Human_VerifiedEdges_EmitNoHint()
    {
        SetupCachedItem(new WorkItemBuilder(743, "Verified isolated").Build(),
            verifiedAt: DateTimeOffset.UtcNow);

        await CaptureStdout(() => _cmd.ExecuteAsync(743, "human"));

        _stderr.ToString().ShouldNotContain("never fetched");
    }

    /// <summary>
    /// A scripted read keeps its quiet contract — it receives the identical signal structurally,
    /// as the null-valued key, and must not have prose injected into its stderr.
    /// </summary>
    [Fact]
    public async Task Json_UnverifiedEdges_EmitNoHumanHint()
    {
        SetupCachedItem(new WorkItemBuilder(742, "Never fetched").Build(), verifiedAt: null);

        await CaptureStdout(() => _cmd.ExecuteAsync(742, "json"));

        _stderr.ToString().ShouldNotContain("links");
    }

    // ── Set read (show-batch) ───────────────────────────────────────

    /// <summary>
    /// 🔴 The set read is what the second acceptance criterion asks for: the edges among a set of
    /// ids WITHOUT one refresh per id. It must carry the same per-member verification, or a
    /// frontier walker learns the edges and not whether to believe them.
    /// </summary>
    [Fact]
    public async Task ShowBatch_ReportsVerificationPerMember_InOneQueryEach()
    {
        var verified = new WorkItemBuilder(742, "Verified").Build();
        var unverified = new WorkItemBuilder(900, "Never fetched").Build();
        _workItemRepo.GetByIdAsync(742, Arg.Any<CancellationToken>()).Returns(verified);
        _workItemRepo.GetByIdAsync(900, Arg.Any<CancellationToken>()).Returns(unverified);
        _linkRepo.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _linkRepo.GetLinksVerifiedAtForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DateTimeOffset>
            {
                [742] = DateTimeOffset.Parse("2026-08-28T05:24:13+00:00"),
            });

        var output = await CaptureStdout(() => _cmd.ExecuteBatchAsync("742,900", "json"));

        using var doc = JsonDocument.Parse(output);
        var rows = doc.RootElement;
        rows.GetArrayLength().ShouldBe(2);

        // Both rows carry an empty edge array; only the verification tells them apart.
        rows[0].GetProperty("links").GetArrayLength().ShouldBe(0);
        rows[1].GetProperty("links").GetArrayLength().ShouldBe(0);
        rows[0].GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.String);
        rows[1].GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        // One plural call each — not one per id, which is the cost the ticket rejected.
        await _linkRepo.Received(1).GetLinksVerifiedAtForSetAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        await _linkRepo.DidNotReceive().GetLinksVerifiedAtAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A link-store failure degrades the batch read to items-without-edges rather than failing it,
    /// and every member then reads as unverified — the honest answer when the store cannot answer.
    /// </summary>
    [Fact]
    public async Task ShowBatch_LinkStoreThrows_ReportsEveryMemberUnverified()
    {
        _workItemRepo.GetByIdAsync(742, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(742, "Item").Build());
        _linkRepo.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("link store unavailable"));

        var output = await CaptureStdout(() => _cmd.ExecuteBatchAsync("742", "json"));

        using var doc = JsonDocument.Parse(output);
        doc.RootElement[0].GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static async Task<string> CaptureStdout(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
