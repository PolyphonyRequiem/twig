using System.Text.Json;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// End-to-end round trip through the <see cref="PrimaryScopeAttachmentService"/>
/// public surface over the real <see cref="WorktreeLocalAttachmentStore"/>. The
/// tests deliberately do not inspect file contents or filenames — that seam is
/// owned by AB#736 and is out of scope for AB#738's acceptance criteria. Every
/// assertion goes through the service's <see cref="Result{T}"/> projections.
/// </summary>
public sealed class WorktreeLocalAttachmentStoreTests : IDisposable
{
    private readonly string _workDir;
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;

    public WorktreeLocalAttachmentStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "twig-attachment-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        var twigDir = Path.Combine(_workDir, ".twig");
        _paths = new TwigPaths(
            twigDir: twigDir,
            configPath: Path.Combine(twigDir, "config"),
            dbPath: Path.Combine(twigDir, "twig.db"),
            startDir: _workDir);
        _config = new TwigConfiguration
        {
            Organization = "fixture-org",
            Project = "fixture-project",
        };

        // A twig.json manifest at the workspace root is the "managed" marker
        // WorkspaceDiscovery + the store both consult. Its presence alone is
        // enough for the store to acknowledge a managed checkout.
        File.WriteAllText(Path.Combine(_workDir, "twig.json"), "{\n}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort */ }
    }

    private PrimaryScopeAttachmentService BuildService(IPrimaryScopeTypeEligibility eligibility, params WorkItem[] items)
    {
        var store = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System);
        var repo = new InMemoryWorkItemRepository(items);
        return new PrimaryScopeAttachmentService(store, eligibility, repo, TimeProvider.System);
    }

    // ── AC round-trip via service surface ──

    [Fact]
    public async Task Attach_then_read_projects_the_new_scope_through_the_service()
    {
        var service = BuildService(
            eligibility: new AlwaysEligible(),
            new WorkItemBuilder(10, "Root scope").AsTask().Build());

        var attach = await service.AttachAsync(10);
        attach.IsSuccess.ShouldBeTrue();

        var status = (await service.ReadStatusAsync()).Value;
        status.IsManagedWorktree.ShouldBeTrue();
        status.PrimaryScope.ShouldNotBeNull();
        status.PrimaryScope!.Value.WorkItemId.ShouldBe(10);
        status.WorkItemTitle.ShouldBe("Root scope");
    }

    [Fact]
    public async Task Detach_returns_status_to_unattached_projection()
    {
        var service = BuildService(
            eligibility: new AlwaysEligible(),
            new WorkItemBuilder(11, "Some scope").AsTask().Build());

        (await service.AttachAsync(11)).IsSuccess.ShouldBeTrue();
        (await service.DetachAsync()).IsSuccess.ShouldBeTrue();

        var status = (await service.ReadStatusAsync()).Value;
        status.IsManagedWorktree.ShouldBeTrue();
        status.PrimaryScope.ShouldBeNull();
    }

    [Fact]
    public async Task Ineligible_type_refusal_leaves_the_projection_unchanged()
    {
        var service = BuildService(
            eligibility: new AllowedTypesEligibility("Feature"),
            new WorkItemBuilder(12, "A Task").AsTask().Build());

        var attach = await service.AttachAsync(12);
        attach.IsSuccess.ShouldBeFalse();
        attach.Error.ShouldContain(AttachmentFailure.IneligibleType.ToString());

        var status = (await service.ReadStatusAsync()).Value;
        status.PrimaryScope.ShouldBeNull();
    }

    [Fact]
    public async Task Explicit_switch_replaces_the_scope_and_read_reflects_it()
    {
        var service = BuildService(
            eligibility: new AlwaysEligible(),
            new WorkItemBuilder(20, "First").AsTask().Build(),
            new WorkItemBuilder(21, "Second").AsTask().Build());

        (await service.AttachAsync(20)).IsSuccess.ShouldBeTrue();
        (await service.SwitchAsync(21)).IsSuccess.ShouldBeTrue();

        var status = (await service.ReadStatusAsync()).Value;
        status.PrimaryScope!.Value.WorkItemId.ShouldBe(21);
    }

    // ── Named failure: parent-attachment-does-not-authorize-child boundary ──

    [Fact]
    public async Task Requiring_a_claim_on_a_non_primary_scope_fails_named()
    {
        var service = BuildService(
            eligibility: new AlwaysEligible(),
            new WorkItemBuilder(30, "Parent").AsFeature().Build(),
            new WorkItemBuilder(31, "Child").AsTask().Build());

        (await service.AttachAsync(30)).IsSuccess.ShouldBeTrue();

        var req = await service.RequireActiveClaimForScopeAsync(31);
        req.IsSuccess.ShouldBeFalse();
        req.Error.ShouldContain(AttachmentFailure.ScopeNotPrimary.ToString());
        req.Error.ShouldContain("#31");
    }

    // ── Test doubles ──

    private sealed class AlwaysEligible : IPrimaryScopeTypeEligibility
    {
        public bool IsEligible(WorkItemType type) => true;
    }

    private sealed class AllowedTypesEligibility : IPrimaryScopeTypeEligibility
    {
        private readonly HashSet<string> _allowed;
        public AllowedTypesEligibility(params string[] allowed) => _allowed = new(allowed, StringComparer.OrdinalIgnoreCase);
        public bool IsEligible(WorkItemType type) => _allowed.Contains(type.Value);
    }

    private sealed class InMemoryWorkItemRepository : IWorkItemRepository
    {
        private readonly Dictionary<int, WorkItem> _items;
        public InMemoryWorkItemRepository(params WorkItem[] items)
        {
            _items = items.ToDictionary(i => i.Id);
        }

        public Task<WorkItem?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult<WorkItem?>(_items.TryGetValue(id, out var it) ? it : null);

        public Task<bool> ExistsByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(_items.ContainsKey(id));

        public Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetRootItemsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByIterationAsync(IterationPath iterationPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByIterationsAsync(IReadOnlyList<IterationPath> iterationPaths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByIterationAndAssigneeAsync(IterationPath iterationPath, string assignee, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetParentChainAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> FindByPatternAsync(string pattern, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetDirtyItemsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetSeedsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<int>> GetOrphanParentIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveAsync(WorkItem workItem, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteByIdAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemapParentIdAsync(int oldParentId, int newParentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearPhantomDirtyFlagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearDirtyFlagAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByAreaPathsAsync(IReadOnlyList<AreaPathFilter> entries, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Twig.Domain.ReadModels.CacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }
}
