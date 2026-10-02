using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Claims;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Task #1108 / Spec #1103 §Live lifetime, switch safety and unfinished work
/// (Proof scenarios 7-9). These are the identity-change eligibility inspector's
/// consumer-facing invariants, exercised against the real durable native
/// stores: real <see cref="SqliteSystemWorktreeRegistry"/>,
/// <see cref="WorktreeLocalAttachmentStore"/>, real
/// <see cref="ConnectionBindingService"/> with a stateful principal-private
/// HTTP transport, real Sqlite pending/intent/journal/claim stores, real
/// <see cref="IPlanLifecycleService"/>. Every positive Verified receipt is
/// produced by the lifecycle's own authorized apply; no row is hand-forged.
/// Every blocker is observed from the actual store it was written into, and
/// is cleared only through its ordinary authorized path. The inspection
/// surface itself is proven read-only (no ADO work HTTP calls, no attachment
/// revision bump, no claim/journal mutation, no cache erase).
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class IdentityChangeEligibilityConsumerTests : IAsyncLifetime
{
    private const string Actor = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Sibling = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ActorPat = "fixture-actor-pat";
    private const string SiblingPat = "fixture-sibling-pat";
    private const string Organization = "fixture";
    private const string Project = "Work";

    private readonly string _temp = Path.Combine(Path.GetTempPath(),
        "twig-eligibility-consumer-" + Guid.NewGuid().ToString("N"));
    private readonly PrincipalTransport _transport = new();
    private readonly List<ServiceProvider> _runtimes = [];
    private HttpClient _http = null!;
    private ConnectionBindingService _bindings = null!;

    private string Home => Path.Combine(_temp, "home");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        _http = new HttpClient(_transport);
        _bindings = new ConnectionBindingService(Home, patHttpClient: _http);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        _bindings.Dispose();
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CombinedEditNoteSeedOpenIntentAndMultipleOldUnknownJournalsStayVisibleAfterAFreshAuthorizedVerifiedApplyTargetingADifferentItem()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var seedAlias = await StageLocalSeedAsync(runtime, "Local draft");
        var intentIdentity = StagedIdentity.New();
        await runtime.GetRequiredService<IPublishIntentRepository>().RecordIntentAsync(intentIdentity, "Pending publication", "Task");
        var older = await WriteFieldProposalAsync(runtime, "older", "older-op", 43, 1, "Older proposal");
        var olderDigest = (await lifecycle.PreviewAsync(older)).Digest!;
        var partial = await WriteFieldProposalAsync(runtime, "partial", "partial-op", 43, 1, "Partial proposal");
        var partialDigest = (await lifecycle.PreviewAsync(partial)).Digest!;
        await journal.SaveOperationErrorAsync(partialDigest, "partial-op", "Unacknowledged earlier outcome",
            PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        var verified = await WriteFieldProposalAsync(runtime, "verified", "verified-op", 42, 1, "Freshly authorized title");
        var verifiedDigest = (await lifecycle.PreviewAsync(verified)).Digest!;
        var applied = await lifecycle.ApplyAsync(verified, verifiedDigest, Authorize(verifiedDigest));
        applied.Failed.ShouldBeFalse(applied.Error);
        applied.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(42, "field", "System.Title", "Freshly authorized title", "Edited title");
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(42, "note", null, null, "Reminder from actor");
        var beforeReads = _transport.WorkRequests;
        var after = await BuildInspector(runtime).InspectAsync(config, paths);
        after.IsEligible.ShouldBeFalse();
        after.PendingEdits.Select(p => p.Kind).ShouldBe(new[] { "field", "note" });
        after.UnresolvedJournals.Select(j => j.Digest).ShouldBe(new[] { olderDigest, partialDigest }, ignoreOrder: true);
        after.LocalSeeds.Single().SeedAlias.ShouldBe(seedAlias);
        after.OpenPublishIntents.Single().Identity.ShouldBe(intentIdentity.ToString());
        _transport.WorkRequests.ShouldBe(beforeReads, "inspection must not perform normal ADO work requests");
    }

    [Fact]
    public async Task NativeReconcileClearsOnlyTheExactOriginalBlockerAndLeavesOtherUnknownsBlocking()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();

        var retiredFile = await WriteFieldProposalAsync(runtime, "retired", "retired-op", 42, 1, "Deliberately retired");
        var retiredDigest = (await lifecycle.PreviewAsync(retiredFile)).Digest!;

        var orphanFile = await WriteFieldProposalAsync(runtime, "orphan", "orphan-op", 43, 1, "Orphan unknown");
        var orphanDigest = (await lifecycle.PreviewAsync(orphanFile)).Digest!;
        await journal.SaveOperationErrorAsync(orphanDigest, "orphan-op",
            "Unacknowledged earlier outcome",
            PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);

        var inspector = BuildInspector(runtime);
        (await inspector.InspectAsync(config, paths)).UnresolvedJournals
            .Select(j => j.Digest).ShouldBe(new[] { retiredDigest, orphanDigest }, ignoreOrder: true);

        var retire = await lifecycle.ReconcileAsync(retiredFile, retiredDigest, "retired-op",
            PlanOutcomeKind.Retired, Authorize(retiredDigest));
        retire.Settled.ShouldBeTrue(retire.Error);
        (await inspector.InspectAsync(config, paths)).UnresolvedJournals
            .ShouldHaveSingleItem().Digest.ShouldBe(orphanDigest,
                "only the exact reconciled original is cleared; the other unknown remains");
    }

    [Fact]
    public async Task EligibilityIsReachedOnlyAfterEveryCategoryClearsThroughItsOwnAuthorizedPathAndInspectionItselfPerformsNoImplicitWrites()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var pending = runtime.GetRequiredService<IPendingChangeStore>();

        await pending.AddChangeAsync(42, "field", "System.Title", "Original 42", "Editing");
        await pending.AddChangeAsync(42, "note", null, null, "Reminder");
        _ = await StageLocalSeedAsync(runtime, "Publishable draft");
        var stagedSeed = (await runtime.GetRequiredService<IWorkItemRepository>().GetSeedsAsync()).Single();
        var leftoverIntent = stagedSeed.StagedIdentity!.Value;
        await runtime.GetRequiredService<IPublishIntentRepository>().RecordIntentAsync(leftoverIntent, stagedSeed.Title, stagedSeed.Type.Value);
        var abandoned = await WriteFieldProposalAsync(runtime, "abandoned", "abandon-op", 42, 1, "Abandoned");
        var abandonedDigest = (await lifecycle.PreviewAsync(abandoned)).Digest!;

        var inspector = BuildInspector(runtime);
        var start = await inspector.InspectAsync(config, paths);
        start.IsEligible.ShouldBeFalse();

        // Inspection is read-only: re-running yields identical blocker counts and makes no ADO work HTTP call.
        var workBefore = _transport.WorkRequests;
        var patchesBefore = _transport.PatchCount;
        var postsBefore = _transport.PostCount;
        var attachmentRevBefore = (await runtime.GetRequiredService<IPrimaryScopeAttachmentStore>()
            .ReadWithRevisionAsync()).Value.Revision;
        var repeat = await inspector.InspectAsync(config, paths);
        _transport.WorkRequests.ShouldBe(workBefore, "inspection never calls the ADO work-items surface");
        _transport.PatchCount.ShouldBe(patchesBefore, "inspection never patches ADO");
        _transport.PostCount.ShouldBe(postsBefore, "inspection never creates ADO items");
        (await runtime.GetRequiredService<IPrimaryScopeAttachmentStore>()
            .ReadWithRevisionAsync()).Value.Revision.ShouldBe(attachmentRevBefore,
                "attachment revision is never bumped by inspection");
        repeat.PendingEdits.Count.ShouldBe(start.PendingEdits.Count);
        repeat.LocalSeeds.Count.ShouldBe(start.LocalSeeds.Count);
        repeat.OpenPublishIntents.Count.ShouldBe(start.OpenPublishIntents.Count);
        repeat.UnresolvedJournals.Count.ShouldBe(start.UnresolvedJournals.Count);

        // Clear each category only through its own authorized / native path.
        await pending.ClearChangesAsync(42);
        var repo = runtime.GetRequiredService<IWorkItemRepository>();
        var seedRow = (await repo.GetSeedsAsync()).ShouldHaveSingleItem();
        var descriptor = (await lifecycle.DescribeSeedAsync(seedRow.Id))!;
        var seedPublish = await WriteSeedProposalAsync(runtime, "seed-publish", "seed-op", descriptor);
        var seedPublishDigest = (await lifecycle.PreviewAsync(seedPublish)).Digest!;
        (await lifecycle.ApplyAsync(seedPublish, seedPublishDigest, Authorize(seedPublishDigest)))
            .Failed.ShouldBeFalse();
        (await lifecycle.ReconcileAsync(abandoned, abandonedDigest, "abandon-op",
                PlanOutcomeKind.Retired, Authorize(abandonedDigest))).Settled.ShouldBeTrue();

        var eligible = await inspector.InspectAsync(config, paths);
        eligible.PendingEdits.ShouldBeEmpty();
        eligible.LocalSeeds.ShouldBeEmpty();
        eligible.OpenPublishIntents.ShouldBeEmpty();
        eligible.UnresolvedJournals.ShouldBeEmpty();
        eligible.ReservedClaims.ShouldBeEmpty();
        eligible.WorktreeGuards.ShouldBeEmpty();
        eligible.UnknownRows.ShouldBeEmpty();
        eligible.IsEligible.ShouldBeTrue();
        eligible.CurrentBindingId.ShouldNotBeNullOrWhiteSpace();

        // Consumer surface agrees with the inspector: exit 1 when blocked, 0 when eligible.
        (await InvokeConnectionCheckAsync(runtime, inspector, "json")).ShouldBe(0);
        await pending.AddChangeAsync(44, "note", null, null, "Reblock");
        (await InvokeConnectionCheckAsync(runtime, inspector, "json")).ShouldBe(1);
        await pending.ClearChangesAsync(44);
        (await InvokeConnectionCheckAsync(runtime, inspector, "json")).ShouldBe(0);
        (await journal.GetUnresolvedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task UnresolvedBindingSelectionIsNeverEligibleEvenWhenNoLocalBlockerIsPresent()
    {
        var runtime = await AttachAsync(makeDefaultBinding: false);
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();

        var inspector = BuildInspector(runtime);
        var snapshot = await inspector.InspectAsync(config, paths);
        snapshot.PendingEdits.ShouldBeEmpty();
        snapshot.LocalSeeds.ShouldBeEmpty();
        snapshot.OpenPublishIntents.ShouldBeEmpty();
        snapshot.UnresolvedJournals.ShouldBeEmpty();
        snapshot.ReservedClaims.ShouldBeEmpty();
        snapshot.CurrentBindingId.ShouldBeNull(
            "with no default binding, selection must remain unresolved - never a fabricated id");
        snapshot.CurrentIdentityId.ShouldBeNull();
        snapshot.IsEligible.ShouldBeFalse(
            "a snapshot without a resolved selection is never eligible, no matter how clean the other categories read");
    }

    [Fact]
    public async Task ReservedClaimNotPointedAtByTheAttachmentBlocksAdmissionAndClearsOnlyAfterAuthorizedRelease()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var attachment = runtime.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var versioned = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(versioned.Attachment.WithPrimaryScope(new PrimaryScope(42,
            $"https://dev.azure.com/{Organization}/{Project}/_workitems/edit/42", DateTimeOffset.UtcNow)),
            versioned.Revision)).IsSuccess.ShouldBeTrue();

        var claim = runtime.GetRequiredService<ILocalClaimService>();
        var projection = runtime.GetRequiredService<IAdoClaimProjection>();
        var anchor = WorktreeAnchorDetector.Detect(paths.StartDir!)!.Value;
        var mint = (await claim.MintAsync(new MintClaimInput(
                ConnectionRefResolver.Compute(config), PrimaryScopeKinds.AdoWorkItem,
                "42", WorktreeFingerprintProvider.CanonicalJson(anchor),
                "", "Alex Reader", null, null, projection)))
            .ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var linked = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(linked.Attachment with { ActiveClaim = null }, linked.Revision)).IsSuccess.ShouldBeTrue();

        var inspector = BuildInspector(runtime);
        var snapshot = await inspector.InspectAsync(config, paths);
        snapshot.IsEligible.ShouldBeFalse();
        var reserved = snapshot.ReservedClaims.ShouldHaveSingleItem();
        reserved.ClaimId.ShouldBe(mint.Claim.ClaimId);
        reserved.CasToken.ShouldNotBeNullOrWhiteSpace();
        var withoutPointer = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(withoutPointer.Attachment with { ActiveClaim = linked.Attachment.ActiveClaim }, withoutPointer.Revision)).IsSuccess.ShouldBeTrue();
        (await claim.ReleaseAsync(new ReleaseClaimInput(mint.Claim.ClaimId, projection))).ShouldBeOfType<ClaimReleaseOutcome.Succeeded>();
        (await inspector.InspectAsync(config, paths)).IsEligible.ShouldBeTrue();
    }

    [Fact]
    public async Task DanglingAttachmentActiveClaimPointerSurfacesAsAnUnknownBlockerAndIsNeverEligible()
    {
        var runtime = await AttachAsync();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var attachment = runtime.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var versioned = (await attachment.ReadWithRevisionAsync()).Value;

        var pointerOnly = versioned.Attachment
            .WithPrimaryScope(new PrimaryScope(42,
                $"https://dev.azure.com/{Organization}/{Project}/_workitems/edit/42", DateTimeOffset.UtcNow)) with
        {
            ActiveClaim = new ActiveClaimReference(
                ClaimId: "01JFABRICATEDFAKECLAIMIDXYZ1234",
                MintedAt: DateTimeOffset.UtcNow),
        };
        (await attachment.WriteAsync(pointerOnly, versioned.Revision)).IsSuccess.ShouldBeTrue();

        var inspector = BuildInspector(runtime);
        var snapshot = await inspector.InspectAsync(config, paths);
        snapshot.IsEligible.ShouldBeFalse(
            "a drifted claim pointer with no matching registry row must never admit a switch");
        (snapshot.UnknownRows.Any() || snapshot.ReservedClaims.Any() || snapshot.WorktreeGuards.Any())
            .ShouldBeTrue("the drifted pointer surfaces as a durable blocker category, never silently");
    }

    // ── fixture plumbing ──────────────────────────────────────────────────

    private async Task<ServiceProvider> AttachAsync(bool makeDefaultBinding = true)
    {
        var root = Path.Combine(_temp, "checkout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = Organization, Project = Project };
        config.Display.Hints = false;
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), config, root);
        paths = new TwigPaths(paths.TwigDir, paths.ConfigPath, paths.DbPath, root,
            Path.Combine(Home, "display.json"));
        await config.SaveSplitAsync(paths);

        using (var store = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await store.InitializeAsync()).IsSuccess.ShouldBeTrue();

        var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor),
                connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }

        await _bindings.RegisterPatIdentityAsync("actor", config.Organization, ActorPat);
        await _bindings.RegisterPatIdentityAsync("sibling", config.Organization, SiblingPat);
        if (makeDefaultBinding)
            await _bindings.CreateBindingAsync(config.Organization, config.Project, "actor", makeDefault: true);

        _transport.SetItem(42, "Original 42", Actor);
        _transport.SetItem(43, "Original 43", Actor);
        _transport.SetItem(44, "Original 44", Actor);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(config, paths.TwigDir, root);
        services.AddSingleton(paths);
        services.AddSingleton<ISystemWorktreeRegistry>(_ =>
            new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System));
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);

        await runtime.GetRequiredService<IProcessTypeStore>().SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Task",
            States =
            [
                new("To Do", StateCategory.Proposed, null),
                new("Doing", StateCategory.InProgress, null),
                new("Done", StateCategory.Completed, null),
            ],
            DefaultChildType = "Task",
            ValidChildTypes = ["Task"],
        });
        return runtime;
    }

    private static IdentityChangeEligibilityService BuildInspector(ServiceProvider runtime) => new(
        runtime.GetRequiredService<ISystemWorktreeRegistry>(),
        runtime.GetRequiredService<IPrimaryScopeAttachmentStore>(),
        runtime.GetRequiredService<IPendingChangeReader>(),
        runtime.GetRequiredService<IWorkItemRepository>(),
        runtime.GetRequiredService<IPublishIntentRepository>(),
        runtime.GetRequiredService<IPlanJournalRepository>(),
        runtime.GetRequiredService<IConnectionBindingService>());

    private static async Task<int> InvokeConnectionCheckAsync(
        ServiceProvider runtime, IdentityChangeEligibilityService inspector, string outputFormat)
    {
        var cmd = new ConnectionCheckCommand(
            inspector,
            runtime.GetRequiredService<TwigConfiguration>(),
            runtime.GetRequiredService<TwigPaths>(),
            runtime.GetRequiredService<OutputFormatterFactory>());
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            return await cmd.ExecuteAsync(outputFormat, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    private static async Task<int> StageLocalSeedAsync(ServiceProvider runtime, string title)
    {
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var area = AreaPath.Parse(Project).Value;
        var iteration = IterationPath.Parse(Project).Value;
        var created = new SeedFactory().CreateUnparented(title, WorkItemType.Task, area, iteration, minted);
        created.IsSuccess.ShouldBeTrue(created.Error);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(created.Value);
        return minted.Alias.Value;
    }

    private static async Task<string> WriteFieldProposalAsync(
        ServiceProvider runtime, string name, string opId, int workItemId, int revision, string title)
    {
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var file = Path.Combine(runtime.GetRequiredService<TwigPaths>().TwigDir, name + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1,
            workspace = new { organization = config.Organization, project = config.Project },
            operations = new[]
            {
                new
                {
                    id = opId,
                    kind = "batch",
                    workItemId,
                    expectedRevision = revision,
                    fields = new Dictionary<string, string> { ["System.Title"] = title },
                },
            },
        }));
        return file;
    }

    private static async Task<string> WriteSeedProposalAsync(
        ServiceProvider runtime, string name, string opId, PlanSeedDescriptor descriptor)
    {
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var file = Path.Combine(runtime.GetRequiredService<TwigPaths>().TwigDir, name + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1,
            workspace = new { organization = config.Organization, project = config.Project },
            operations = new[]
            {
                new
                {
                    id = opId,
                    kind = "publish-seed",
                    stagedIdentity = descriptor.Identity.ToString(),
                    expectedFingerprint = descriptor.Fingerprint,
                },
            },
        }));
        return file;
    }

    private static ProposalAuthorization Authorize(string digest) => new()
    {
        Digest = digest,
        Mode = ProposalAuthorizationMode.Human,
        AuthorizerIdentity = "Fixture scope owner",
        Rationale = "Isolated eligibility consumer proof",
        AuthorizedAt = DateTimeOffset.UtcNow,
    };

    private sealed class PrincipalTransport : HttpMessageHandler
    {
        private readonly Dictionary<int, StoredItem> _items = new();
        private readonly HashSet<string> _seenPrincipals = new(StringComparer.Ordinal);

        public int WorkRequests { get; private set; }
        public int PostCount { get; private set; }
        public int PatchCount { get; private set; }
        public IReadOnlyCollection<string> SeenPrincipals => _seenPrincipals;

        public void SetItem(int id, string title, string principal)
        {
            if (_items.TryGetValue(id, out var existing))
                _items[id] = existing with { Title = title };
            else
                _items[id] = new StoredItem(title, 1, principal, principal + "@fixture.example");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = request.Headers.Authorization;
            if (header?.Scheme != "Basic")
                return Reply(HttpStatusCode.Unauthorized, new { message = "No fixture credential" });
            var secret = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!))[1..];
            var principal = secret == ActorPat ? Actor : secret == SiblingPat ? Sibling : null;
            if (principal is null)
                return Reply(HttpStatusCode.Unauthorized, new { message = "Unknown fixture credential" });
            _seenPrincipals.Add(principal);

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new
                {
                    authenticatedUser = new { id = principal, providerDisplayName = "Alex Reader" },
                });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { displayName = "Alex Reader", emailAddress = principal + "@fixture.example" });
            if (path.Contains("/_apis/wit/workitems/", StringComparison.OrdinalIgnoreCase))
            {
                WorkRequests++;
                var parts = path[(path.IndexOf("/_apis/wit/workitems/", StringComparison.OrdinalIgnoreCase) + "/_apis/wit/workitems/".Length)..].Split('/');
                var tail = parts[0];
                if (request.Method == HttpMethod.Post)
                {
                    PostCount++;
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var fields = body.RootElement.EnumerateArray()
                        .Where(x => x.GetProperty("path").GetString()!.StartsWith("/fields/", StringComparison.Ordinal))
                        .ToDictionary(x => x.GetProperty("path").GetString()![8..], x => x.GetProperty("value").GetString());
                    var id = 500 + PostCount;
                    _items[id] = new StoredItem(fields.GetValueOrDefault("System.Title", "Published " + id)!, 1, principal, principal + "@fixture.example");
                    return Item(id);
                }
                if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workId)
                    || !_items.TryGetValue(workId, out var item) || item.Principal != principal)
                    return Reply(HttpStatusCode.NotFound, new { message = "Not readable under the selected principal" });
                if (request.Method == HttpMethod.Patch)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var title = item.Title;
                    var assigned = item.Assigned;
                    foreach (var operation in body.RootElement.EnumerateArray())
                    {
                        var opPath = operation.GetProperty("path").GetString();
                        if (opPath == "/rev" && operation.GetProperty("value").GetInt32() != item.Revision)
                            return Reply(HttpStatusCode.PreconditionFailed, new { message = "Revision " + item.Revision });
                        if (opPath == "/fields/System.Title")
                            title = operation.GetProperty("value").GetString()!;
                        if (opPath == "/fields/System.AssignedTo") assigned = !operation.TryGetProperty("value", out var assignment) || assignment.ValueKind == JsonValueKind.Null ? null : assignment.GetString();
                    }
                    PatchCount++;
                    item = item with { Title = title, Revision = item.Revision + 1, Assigned = assigned };
                    _items[workId] = item;
                }
                return Item(workId, item);
            }
            return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });
        }

        private HttpResponseMessage Item(int id) => Item(id, _items[id]);

        private HttpResponseMessage Item(int id, StoredItem item) => Reply(HttpStatusCode.OK, new
        {
            id,
            rev = item.Revision,
            fields = new Dictionary<string, object>
            {
                ["System.Title"] = item.Title,
                ["System.WorkItemType"] = "Task",
                ["System.State"] = "To Do",
                ["System.AreaPath"] = Project,
                ["System.IterationPath"] = Project,
                ["System.AssignedTo"] = item.Assigned is null ? null! : new { displayName = "Alex Reader", uniqueName = item.Assigned },
            },
            relations = Array.Empty<object>(),
        });

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        private sealed record StoredItem(string Title, int Revision, string Principal, string? Assigned);
    }
}
