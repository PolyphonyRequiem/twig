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
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.ChangeProposals;
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

/// <summary>Native receipts over real attached stores and independently maintained, principal-private remote state.</summary>
public sealed class NativePublicationConsumerTests : IAsyncLifetime
{
    private const string Actor = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Sibling = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ActorPat = "fixture-actor-pat";
    private const string SiblingPat = "fixture-sibling-pat";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-publication-consumer-" + Guid.NewGuid().ToString("N"));
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
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RetiredConfirmedIntentCannotReplayBeforeOrAfterRebindingOrReimport()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var file = await ProposalAsync(runtime, "original", "retired", 42, 1, "Refined actor plan");
        var bytes = await File.ReadAllBytesAsync(file);
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        var savedAuthorization = Authorize(digest);
        await journal.TryTransitionOperationAsync(digest, "retired", PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow);
        await journal.ConfirmAsync(digest, DateTimeOffset.UtcNow);
        var unknown = await ProposalAsync(runtime, "unrelated-unknown", "unknown", 43, 1, "Another intent");
        var unknownDigest = (await lifecycle.PreviewAsync(unknown)).Digest!;
        await journal.SaveOperationErrorAsync(unknownDigest, "unknown", "Unacknowledged earlier outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var command = new PlanCommand(lifecycle, runtime.GetRequiredService<OutputFormatterFactory>(),
            runtime.GetRequiredService<ISessionSteeringModeProvider>(), TimeProvider.System, stdout: stdout, stderr: stderr);
        (await command.ReconcileAsync(file, digest, "retired", "retire", "Fixture scope owner", "Deliberately abandon an unadmitted intent",
            null, null, "json", CancellationToken.None)).ShouldBe(0, stderr.ToString());
        using (var receiptOutput = JsonDocument.Parse(stdout.ToString()))
            receiptOutput.RootElement.GetProperty("receipt").GetProperty("kind").GetString().ShouldBe("Retired");
        var workRequests = _transport.WorkRequests;
        (await lifecycle.ApplyAsync(file, digest, savedAuthorization)).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(workRequests, "saved apply must refuse before any work HTTP");
        _transport.CommittedWrites.ShouldBe(0);
        _transport.RevisionOf(42).ShouldBe(1, "the original ADO revision is still admissible absent the native fence");
        (await lifecycle.PreviewAsync(file)).CanApply.ShouldBeFalse();
        var imported = await lifecycle.ValidateAsync(file);
        var captured = (await journal.GetAsync(digest))!.Origin;
        await journal.ImportAsync(imported.Plan!, imported.CanonicalJson!, digest, file, DateTimeOffset.UtcNow, origin: captured);
        (await lifecycle.ApplyAsync(file, digest, savedAuthorization)).Failed.ShouldBeTrue();
        var stored = (await journal.GetAsync(digest))!.Operations.Single();
        stored.State.ShouldBe(PlanOperationState.Confirmed);
        stored.OutcomeReceipt!.Kind.ShouldBe(PlanOutcomeKind.Retired);
        (await journal.GetUnresolvedAsync()).Select(j => j.Digest).ShouldBe(new[] { unknownDigest });

        var fresh = await ProposalAsync(runtime, "fresh-authorized", "fresh", 43, 1, "Fresh permitted plan");
        var freshDigest = (await lifecycle.PreviewAsync(fresh)).Digest!;
        (await lifecycle.ApplyAsync(fresh, freshDigest, Authorize(freshDigest))).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        _transport.TitleOf(43).ShouldBe("Fresh permitted plan");
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var siblingBinding = await _bindings.CreateBindingAsync(config.Organization, config.Project, "sibling", makeDefault: false);
        SwapSelection(config, siblingBinding.BindingId);
        var reconnected = BuildRuntime(config, paths);
        var newLifecycle = reconnected.GetRequiredService<IPlanLifecycleService>();
        workRequests = _transport.WorkRequests;
        (await newLifecycle.ApplyAsync(file, digest, savedAuthorization)).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(workRequests);
        (await newLifecycle.PreviewAsync(file)).CanApply.ShouldBeFalse();
        (await reconnected.GetRequiredService<IPlanJournalRepository>().GetAsync(digest))!.Origin.ShouldBe(captured);
        (await File.ReadAllBytesAsync(file)).ShouldBe(bytes);
        _transport.RevisionOf(42).ShouldBe(1);
        (await reconnected.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).Select(j => j.Digest).ShouldContain(unknownDigest);
    }

    [Fact]
    public async Task AcknowledgedCommitWithUnavailableReadbackIsSettledWithoutReissuingTheWrite()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var file = await ProposalAsync(runtime, "acknowledged", "attempt", 42, 1, "Committed actor plan");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        _transport.FailReadbackAfterCommit = true;
        var attempted = await lifecycle.ApplyAsync(file, digest, Authorize(digest));
        attempted.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        _transport.CommittedWrites.ShouldBe(1);
        _transport.FailReadbackAfterCommit = false;
        var reconciled = await lifecycle.ReconcileAsync(file, digest, "attempt", PlanOutcomeKind.Readback, Authorize(digest));
        reconciled.Settled.ShouldBeTrue(reconciled.Error);
        var row = (await runtime.GetRequiredService<IPlanJournalRepository>().GetAsync(digest))!.Operations.Single();
        row.State.ShouldBe(PlanOperationState.Indeterminate);
        row.OutcomeReceipt!.Kind.ShouldBe(PlanOutcomeKind.Readback);
        _transport.TitleOf(42).ShouldBe("Committed actor plan");
        _transport.CommittedWrites.ShouldBe(1);
    }

    [Fact]
    public async Task MatchingNewerRemoteStateWithoutAcknowledgementDoesNotSettleAnUnknownAttempt()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var file = await ProposalAsync(runtime, "unknown", "unknown", 42, 1, "Coincidental newer title");
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        await journal.SaveOperationErrorAsync(digest, "unknown", "Unknown transport outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        _transport.SetItem(42, "Coincidental newer title", 2, Actor);
        var result = await lifecycle.ReconcileAsync(file, digest, "unknown", PlanOutcomeKind.Readback, Authorize(digest));
        result.Settled.ShouldBeFalse();
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        (await journal.GetUnresolvedAsync()).Select(j => j.Digest).ShouldContain(digest);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task OnlyAnActuallyVerifiedReplacementOfTheOriginalEffectSettlesItsFailedAttempt()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var original = await ProposalAsync(runtime, "original", "original", 42, 1, "Replacement actor plan");
        var digest = (await lifecycle.PreviewAsync(original)).Digest!;
        _transport.RejectNextPatch = true;
        (await lifecycle.ApplyAsync(original, digest, Authorize(digest))).Operations.Single().State.ShouldBe(PlanOperationState.Failed);
        var unrelated = await ProposalAsync(runtime, "unrelated", "unrelated", 43, 1, "Unrelated successful plan");
        var unrelatedDigest = (await lifecycle.PreviewAsync(unrelated)).Digest!;
        (await lifecycle.ApplyAsync(unrelated, unrelatedDigest, Authorize(unrelatedDigest))).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        (await lifecycle.ReconcileAsync(original, digest, "original", PlanOutcomeKind.Superseded, Authorize(digest), unrelatedDigest, "unrelated")).Settled.ShouldBeFalse();
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        var replacement = await ProposalAsync(runtime, "replacement", "replacement", 42, 1, "Replacement actor plan");
        var replacementDigest = (await lifecycle.PreviewAsync(replacement)).Digest!;
        (await lifecycle.ApplyAsync(replacement, replacementDigest, Authorize(replacementDigest))).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var writes = _transport.CommittedWrites;
        var settled = await lifecycle.ReconcileAsync(original, digest, "original", PlanOutcomeKind.Superseded, Authorize(digest), replacementDigest, "replacement");
        settled.Settled.ShouldBeTrue(settled.Error);
        var row = (await journal.GetAsync(digest))!.Operations.Single();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.OutcomeReceipt!.ReplacementDigest.ShouldBe(replacementDigest);
        (await journal.GetUnresolvedAsync()).ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(writes);
    }

    [Fact]
    public async Task ChangedAndUnknownOriginsCannotAdoptTheCurrentActorBeforeWorkHttp()
    {
        var runtime = await AttachAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var original = await ProposalAsync(runtime, "bound", "bound", 42, 1, "Bound original intent");
        var digest = (await lifecycle.PreviewAsync(original)).Digest!;
        var legacy = await ProposalAsync(runtime, "legacy", "legacy", 43, 1, "Unknown legacy intent");
        var parsed = await lifecycle.ValidateAsync(legacy);
        await journal.ImportAsync(parsed.Plan!, parsed.CanonicalJson!, parsed.Digest!, legacy, DateTimeOffset.UtcNow);
        var requests = _transport.WorkRequests;
        (await lifecycle.ApplyAsync(legacy, parsed.Digest!, Authorize(parsed.Digest!))).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(requests);
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var selected = await _bindings.CreateBindingAsync(config.Organization, config.Project, "sibling", false);
        SwapSelection(config, selected.BindingId);
        (await lifecycle.ApplyAsync(original, digest, Authorize(digest))).Failed.ShouldBeTrue();
        (await lifecycle.PreviewAsync(original)).CanApply.ShouldBeFalse();
        var reconnected = BuildRuntime(config, runtime.GetRequiredService<TwigPaths>());
        (await reconnected.GetRequiredService<IPlanLifecycleService>().ApplyAsync(original, digest, Authorize(digest))).Failed.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(requests);
        (await journal.GetAsync(parsed.Digest!))!.Origin.ShouldBeNull();
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task OpenSeedIntentReadbackNeverRedrivesPublicationOrClearsTheUnknownBlocker()
    {
        var runtime = await AttachAsync();
        var paths = runtime.GetRequiredService<TwigPaths>();
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var identity = StagedIdentity.New();
        var file = Path.Combine(paths.TwigDir, "seed-unknown.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1, workspace = new { organization = config.Organization, project = config.Project },
            operations = new[] { new { id = "seed", kind = "publish-seed", stagedIdentity = identity.ToString(), expectedFingerprint = "unproved-fingerprint" } }
        }));
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var intents = runtime.GetRequiredService<IPublishIntentRepository>();
        var digest = (await lifecycle.PreviewAsync(file)).Digest!;
        var originalIntent = await intents.RecordIntentAsync(identity, "Unknown create", "Task");
        await journal.SaveOperationErrorAsync(digest, "seed", "Unknown create outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        var result = await lifecycle.ReconcileAsync(file, digest, "seed", PlanOutcomeKind.Readback, Authorize(digest));
        result.Settled.ShouldBeFalse();
        (await intents.GetOpenIntentsAsync()).Single().RecordedAt.ShouldBe(originalIntent.RecordedAt);
        (await journal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
        _transport.PostCount.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }

    private async Task<ServiceProvider> AttachAsync()
    {
        var root = Path.Combine(_temp, "checkout");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture", Project = "Project" };
        config.Display.Hints = false;
        var defaults = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), config, root);
        var paths = new TwigPaths(defaults.TwigDir, defaults.ConfigPath, defaults.DbPath, root, Path.Combine(Home, "display.json"));
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }
        await _bindings.RegisterPatIdentityAsync("actor", config.Organization, ActorPat);
        await _bindings.RegisterPatIdentityAsync("sibling", config.Organization, SiblingPat);
        await _bindings.CreateBindingAsync(config.Organization, config.Project, "actor", true);
        _transport.SetItem(42, "Original actor plan", 1, Actor);
        _transport.SetItem(43, "Another actor plan", 1, Actor);
        _transport.SetItem(44, "Sibling private plan", 1, Sibling);
        return BuildRuntime(config, paths);
    }

    private ServiceProvider BuildRuntime(TwigConfiguration config, TwigPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(config, paths.TwigDir, paths.StartDir);
        services.AddSingleton(paths);
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices();
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        return runtime;
    }

    private static async Task<string> ProposalAsync(ServiceProvider runtime, string name, string opId, int itemId, int revision, string title)
    {
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var file = Path.Combine(runtime.GetRequiredService<TwigPaths>().TwigDir, name + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1, workspace = new { organization = config.Organization, project = config.Project },
            operations = new[] { new { id = opId, kind = "batch", workItemId = itemId, expectedRevision = revision, fields = new Dictionary<string, string> { ["System.Title"] = title } } }
        }));
        return file;
    }

    private void SwapSelection(TwigConfiguration config, string binding)
    {
        // Fixture-only authoritative CAS; guarded production transition belongs to the subsequent ticket.
        using var database = new SqliteConnection($"Data Source={Path.Combine(Home, "system.db")}");
        database.Open();
        using var current = database.CreateCommand();
        current.CommandText = "SELECT revision FROM connection_defaults WHERE connection_ref = @connection";
        current.Parameters.AddWithValue("@connection", ConnectionRefResolver.Compute(config));
        var revision = Convert.ToInt64(current.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var change = database.CreateCommand();
        change.CommandText = "UPDATE connection_defaults SET binding_id = @binding, revision = revision + 1 WHERE connection_ref = @connection AND revision = @expected";
        change.Parameters.AddWithValue("@binding", binding);
        change.Parameters.AddWithValue("@connection", ConnectionRefResolver.Compute(config));
        change.Parameters.AddWithValue("@expected", revision);
        change.ExecuteNonQuery().ShouldBe(1);
    }

    private static ProposalAuthorization Authorize(string digest) => new()
    {
        Digest = digest, Mode = ProposalAuthorizationMode.Human, AuthorizerIdentity = "Fixture scope owner",
        Rationale = "Isolated native publication proof", AuthorizedAt = DateTimeOffset.UtcNow
    };

    private sealed class PrincipalTransport : HttpMessageHandler
    {
        private readonly Dictionary<int, Item> _items = new();
        private readonly Dictionary<(int Id, int Revision), Item> _history = new();
        public int WorkRequests { get; private set; }
        public int PostCount { get; private set; }
        public int CommittedWrites { get; private set; }
        public bool RejectNextPatch { get; set; }
        public bool FailReadbackAfterCommit { get; set; }
        public string TitleOf(int id) => _items[id].Title;
        public int RevisionOf(int id) => _items[id].Revision;
        public void SetItem(int id, string title, int revision, string principal)
        {
            var item = new Item(title, revision, principal);
            _items[id] = item;
            _history[(id, revision)] = item;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = request.Headers.Authorization;
            if (header?.Scheme != "Basic") return Reply(HttpStatusCode.Unauthorized, new { message = "No fixture credential" });
            var secret = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!))[1..];
            var principal = secret == ActorPat ? Actor : secret == SiblingPat ? Sibling : null;
            if (principal is null) return Reply(HttpStatusCode.Unauthorized, new { message = "Unknown fixture credential" });
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { authenticatedUser = new { id = principal, providerDisplayName = "Same display" } });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { displayName = "Same display", emailAddress = principal + "@fixture.example" });
            const string route = "/_apis/wit/workitems/";
            var routeIndex = path.IndexOf(route, StringComparison.OrdinalIgnoreCase);
            if (routeIndex < 0) return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });
            WorkRequests++;
            if (request.Method == HttpMethod.Post) PostCount++;
            var segments = path[(routeIndex + route.Length)..].Split('/');
            if (!int.TryParse(segments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !_items.TryGetValue(id, out var item) || item.Principal != principal)
                return Reply(HttpStatusCode.NotFound, new { message = "Not readable by the selected principal" });
            if (request.Method == HttpMethod.Patch)
            {
                if (RejectNextPatch)
                {
                    RejectNextPatch = false;
                    return Reply(HttpStatusCode.BadRequest, new { message = "Fixture rejected the transaction before committing" });
                }
                using var operations = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var title = item.Title;
                foreach (var operation in operations.RootElement.EnumerateArray())
                {
                    var target = operation.GetProperty("path").GetString();
                    if (target == "/rev" && operation.GetProperty("value").GetInt32() != item.Revision)
                        return Reply(HttpStatusCode.PreconditionFailed, new { message = "Revision " + item.Revision });
                    if (target == "/fields/System.Title") title = operation.GetProperty("value").GetString()!;
                }
                SetItem(id, title, item.Revision + 1, item.Principal);
                item = _items[id];
                CommittedWrites++;
            }
            else if (FailReadbackAfterCommit && CommittedWrites > 0)
                throw new HttpRequestException("Fixture readback unavailable after acknowledged commit");
            else if (segments.Length == 3 && segments[1] == "revisions")
            {
                var revision = int.Parse(segments[2], CultureInfo.InvariantCulture);
                if (!_history.TryGetValue((id, revision), out item))
                    return Reply(HttpStatusCode.NotFound, new { message = "No historical revision" });
            }
            return Reply(HttpStatusCode.OK, new
            {
                id, rev = item.Revision,
                fields = new Dictionary<string, object>
                {
                    ["System.Title"] = item.Title, ["System.WorkItemType"] = "Task", ["System.State"] = "To Do",
                    ["System.AreaPath"] = "fixture", ["System.IterationPath"] = "fixture",
                    ["System.AssignedTo"] = new { displayName = "Same display", uniqueName = item.Principal }
                },
                relations = Array.Empty<object>()
            });
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        private sealed record Item(string Title, int Revision, string Principal);
    }
}
