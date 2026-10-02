using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Claims;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Collection("ConsoleRedirect")]
public sealed class BoundPrincipalConsumerTests : IAsyncLifetime
{
    private const string First = "alex@work.example";
    private const string Second = "alex@personal.example";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-principal-consumer-" + Guid.NewGuid().ToString("N"));
    private readonly PrincipalTransport _transport = new();
    private HttpClient _http = null!;
    private ConnectionBindingService _bindings = null!;
    private readonly List<ServiceProvider> _services = [];
    private string Home => Path.Combine(_temp, "user");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        _http = new HttpClient(_transport);
        _bindings = new ConnectionBindingService(Home, patHttpClient: _http);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var service in _services) service.Dispose();
        _bindings.Dispose();
        _http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InterleavedSameNamePrincipalsHaveSeparateSelfViewsAndAnExplicitTeamView()
    {
        var first = await CreateRuntimeAsync("Work", First);
        var second = await CreateRuntimeAsync("Personal", Second);
        foreach (var runtime in new[] { first, second })
            await FillMirrorAsync(runtime);

        foreach (var runtime in new[] { first, second, first, second })
        {
            var own = runtime == first ? 42 : 43;
            var workingSet = await runtime.GetRequiredService<WorkingSetService>().ComputeAsync();
            workingSet.SprintItemIds.ShouldBe(new[] { own });
            var cli = new TwigCommands(runtime);
            var self = await CaptureAsync(() => cli.Workspace(output: "json", noLive: true, flat: true));
            self.Exit.ShouldBe(0, self.Error);
            self.Output.ShouldContain("Private " + own);
            self.Output.ShouldNotContain("Private " + (own == 42 ? 43 : 42));
        }
        var team = await CaptureAsync(() => new TwigCommands(first).Workspace(output: "json", all: true, noLive: true, flat: true));
        team.Exit.ShouldBe(0, team.Error);
        team.Output.ShouldContain("Private 42");
        team.Output.ShouldContain("Private 43");
    }

    [Fact]
    public async Task DefaultSeedPublishesUnderCanonicalBoundAccountDespiteUnknownGlobalDisplayName()
    {
        foreach (var (project, account) in new[] { ("Work", First), ("Personal", Second) })
        {
            var runtime = await CreateRuntimeAsync(project, account);
            var command = new TwigCommands(runtime);
            var created = await CaptureAsync(() => command.SeedNew(title: "Bound draft", type: "Task", noParent: true, output: "json"));
            created.Exit.ShouldBe(0, created.Error);
            var repo = runtime.GetRequiredService<IWorkItemRepository>();
            var seed = (await repo.GetSeedsAsync()).ShouldHaveSingleItem();
            var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
            var descriptor = (await lifecycle.DescribeSeedAsync(seed.Id))!;
            var file = await WriteSeedProposalAsync(runtime, descriptor);
            var preview = await lifecycle.PreviewAsync(file);
            preview.CanApply.ShouldBeTrue();
            var digest = preview.Digest ?? throw new InvalidOperationException("Native preview produced no digest.");
            var applied = await lifecycle.ApplyAsync(file, digest, Authorize(digest));
            applied.Failed.ShouldBeFalse(applied.Error);
            var row = applied.Operations.ShouldHaveSingleItem();
            row.State.ShouldBe(PlanOperationState.Verified, row.Error);
            using var payload = JsonDocument.Parse(row.ResultJson!);
            var publishedId = payload.RootElement.GetProperty("publishedId").GetInt32();
            var refreshed = await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(publishedId);
            // The server accepts only its recognized canonical identities, then renders both
            // as Alex Reader. Its stored owner, not a request echo, proves successful authorship.
            _transport.OwnerOf(publishedId).ShouldBe(account);
            refreshed.AssignedTo.ShouldBe("Alex Reader");
            (await repo.GetSeedsAsync()).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedDefaultSelfRuleIsReboundWithoutLosingPins(bool selected)
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        await FillMirrorAsync(runtime);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var legacy = await benches.GetOrCreateDefaultAsync(new[]
        {
            BenchSelector.ForCurrentSprint("Alex Reader"), BenchSelector.ForItem(43),
        });
        if (selected) await benches.SetCurrentAsync(legacy.Id);
        var view = await runtime.GetRequiredService<WorkingSetService>().ComputeAsync();
        view.SprintItemIds.ShouldBe(new[] { 42 });
        view.TrackedItemIds.ShouldBe(new[] { 43 });
    }

    [Fact]
    public async Task PublishedDisplayNameCannotBorrowCanonicalSelfAuthorityWhenEvidenceIsMissing()
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        _transport.UnprovedAssignment = true;
        await FillMirrorAsync(runtime);
        var view = await CaptureAsync(() => new TwigCommands(runtime).Workspace(output: "json", noLive: true, flat: true));
        view.Exit.ShouldBe(0, view.Error);
        view.Output.ShouldContain("Private 42");
        view.Output.ShouldNotContain("Private 43");
        (await runtime.GetRequiredService<WorkingSetService>().ComputeAsync()).SprintItemIds.ShouldBe(new[] { 42 });
    }

    [Fact]
    public async Task LocalAssigneeEditCannotKeepThePreviousOwnersServerEvidence()
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        var item = await runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42);
        var repo = runtime.GetRequiredService<IWorkItemRepository>();
        await repo.SaveAsync(item);
        item.UpdateField("System.AssignedTo", Second);
        await repo.SaveAsync(item);
        var iteration = IterationPath.Parse("Work").Value;
        (await repo.GetByIterationAndAssigneeAsync(iteration, First)).ShouldBeEmpty();
        var reassigned = (await repo.GetByIterationAndAssigneeAsync(iteration, Second)).ShouldHaveSingleItem();
        reassigned.Id.ShouldBe(42);
        reassigned.AssignedToUniqueName.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelfDerivedRefreshConsumersFormatMissingIdentityRefusal(bool tree)
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        await FillMirrorAsync(runtime);
        _transport.MissingCanonicalIdentity = true;
        var command = new TwigCommands(runtime);
        var result = await CaptureAsync(() => tree
            ? command.Show(42, output: "json", tree: true, refresh: true)
            : command.Sync(output: "json", pullOnly: true));
        result.Exit.ShouldBe(1);
        using var error = JsonDocument.Parse(result.Error);
        error.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        _transport.CreatedCount.ShouldBe(0);
        _transport.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task MissingCanonicalMetadataRefusesDefaultAuthoringWithoutCreatingDrafts()
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        _transport.MissingCanonicalIdentity = true;
        var result = await CaptureAsync(() => new TwigCommands(runtime).SeedNew(title: "Must not persist", type: "Task", noParent: true, output: "json"));
        result.Exit.ShouldBe(1);
        using var error = JsonDocument.Parse(result.Error);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        (await runtime.GetRequiredService<IWorkItemRepository>().GetSeedsAsync()).ShouldBeEmpty();
        _transport.CreatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ChangedSeedFingerprintAndWrongDigestCannotPublishOrRewriteOwnership()
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        var created = await CaptureAsync(() => new TwigCommands(runtime).SeedNew(title: "Draft", type: "Task", noParent: true, output: "json"));
        created.Exit.ShouldBe(0, created.Error);
        var repo = runtime.GetRequiredService<IWorkItemRepository>();
        var seed = (await repo.GetSeedsAsync()).Single();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var descriptor = (await lifecycle.DescribeSeedAsync(seed.Id))!;
        var file = await WriteSeedProposalAsync(runtime, descriptor);
        var preview = await lifecycle.PreviewAsync(file);
        var digest = preview.Digest ?? throw new InvalidOperationException("Native preview produced no digest.");
        var wrong = new string('0', 64);
        (await lifecycle.ApplyAsync(file, wrong, Authorize(wrong))).Failed.ShouldBeTrue();
        await repo.SaveAsync(seed.WithSeedFields("Changed after preview", seed.Fields));
        (await lifecycle.ApplyAsync(file, digest, Authorize(digest))).Failed.ShouldBeTrue();
        _transport.CreatedCount.ShouldBe(0);
        (await repo.GetSeedsAsync()).Single().Title.ShouldBe("Changed after preview");
    }

    [Fact]
    public async Task ClaimHolderUsesBoundAccountAndRejectsSameNameAlternateActorWithoutMutation()
    {
        var runtime = await CreateRuntimeAsync("Work", First);
        _transport.SetOwner(42, Second);
        var holder = await runtime.GetRequiredService<IClaimHolderResolver>().ResolveAsync();
        holder.IsSuccess.ShouldBeTrue(holder.Error);
        var paths = runtime.GetRequiredService<TwigPaths>();
        var cfg = runtime.GetRequiredService<TwigConfiguration>();
        var store = runtime.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var versioned = (await store.ReadWithRevisionAsync()).Value;
        (await store.WriteAsync(versioned.Attachment.WithPrimaryScope(new PrimaryScope(42,
            "https://dev.azure.com/fixture/Work/_workitems/edit/42", DateTimeOffset.UtcNow)), versioned.Revision)).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(paths.StartDir!)!.Value;
        var claim = runtime.GetRequiredService<ILocalClaimService>();
        var projection = runtime.GetRequiredService<IAdoClaimProjection>();
        var input = new MintClaimInput(ConnectionRefResolver.Compute(cfg), PrimaryScopeKinds.AdoWorkItem,
            "42", WorktreeFingerprintProvider.CanonicalJson(anchor), Second, "Alex Reader", null, null, projection);
        (await claim.MintAsync(input)).ShouldBeOfType<ClaimMintOutcome.HolderUnavailable>();
        _transport.PatchCount.ShouldBe(0);
        _transport.OwnerOf(42).ShouldBe(Second);
        var minted = (await claim.MintAsync(input with { HolderIdentity = "", HolderDisplay = "Unrecognized global name" }))
            .ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        minted.Claim.HolderIdentity.ShouldBe(First);
        var patches = _transport.PatchCount;
        (await claim.MintAsync(input with { HolderIdentity = First })).ShouldBeOfType<ClaimMintOutcome.PrimaryScopeAlreadyClaimed>();
        (await claim.UpdateLabelAsync(new UpdateClaimLabelInput(minted.Claim.ClaimId, "renamed", "stale-cas")))
            .ShouldBeOfType<ClaimLabelUpdateOutcome.ConcurrentClaimWrite>();
        var sibling = await CreateRuntimeAsync("Personal", Second);
        var alternateActor = new Twig.Infrastructure.Services.Claims.LocalClaimService(
            runtime.GetRequiredService<ISystemWorktreeRegistry>(), store,
            runtime.GetRequiredService<IClaimIdGenerator>(), runtime.GetRequiredService<IClaimCasTokenGenerator>(),
            sibling.GetRequiredService<IClaimHolderResolver>(), TimeProvider.System);
        (await alternateActor.ReleaseAsync(new ReleaseClaimInput(minted.Claim.ClaimId,
            sibling.GetRequiredService<IAdoClaimProjection>())))
            .ShouldBeOfType<ClaimReleaseOutcome.InvalidRequest>();
        _transport.PatchCount.ShouldBe(patches);
        _transport.OwnerOf(42).ShouldBe(First);
        var lookup = (await claim.LookupByTupleAsync(new ClaimTupleQuery(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId)))
            .ShouldBeOfType<ClaimLookupOutcome.Found>();
        lookup.Claim.HolderIdentity.ShouldBe(First);
        lookup.Claim.CasToken.ShouldBe(minted.Claim.CasToken);
    }

    private async Task<ServiceProvider> CreateRuntimeAsync(string project, string account)
    {
        var repo = Path.Combine(_temp, project);
        Directory.CreateDirectory(repo);
        InitCommandTestFixture.InitTempWorktree(repo).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture", Project = project };
        config.User.DisplayName = "Unknown global display name";
        config.Display.Hints = false;
        var paths = TwigPaths.BuildPaths(Path.Combine(repo, ".twig"), config, repo);
        paths = new TwigPaths(paths.TwigDir, paths.ConfigPath, paths.DbPath, repo, Path.Combine(Home, "display.json"));
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(repo)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertConnectionAsync(connection, "fixture", project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }
        await _bindings.RegisterPatIdentityAsync(project, "fixture", account);
        await _bindings.CreateBindingAsync("fixture", project, project, makeDefault: true);
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(config, paths.TwigDir, repo);
        services.AddSingleton(paths);
        services.AddSingleton<ISystemWorktreeRegistry>(_ => new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System));
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        var provider = services.BuildServiceProvider();
        _services.Add(provider);
        await provider.GetRequiredService<IProcessTypeStore>().SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new("To Do", Twig.Domain.Enums.StateCategory.Proposed, null), new("Doing", Twig.Domain.Enums.StateCategory.InProgress, null), new("Done", Twig.Domain.Enums.StateCategory.Completed, null)],
            DefaultChildType = "Task",
            ValidChildTypes = ["Task"],
        });
        return provider;
    }

    private static async Task FillMirrorAsync(ServiceProvider runtime)
    {
        var ado = runtime.GetRequiredService<IAdoWorkItemService>();
        var repo = runtime.GetRequiredService<IWorkItemRepository>();
        foreach (var id in new[] { 42, 43 }) await repo.SaveAsync(await ado.FetchAsync(id));
        var project = runtime.GetRequiredService<TwigConfiguration>().Project;
        await runtime.GetRequiredService<IIterationCalendar>().SaveAsync(new[]
        {
            new TeamIteration(project, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)),
        });
    }

    private static ProposalAuthorization Authorize(string digest) => new()
    {
        Digest = digest,
        Mode = ProposalAuthorizationMode.Human,
        AuthorizerIdentity = "Fixture scope owner",
        Rationale = "Isolated consumer proof",
        AuthorizedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<string> WriteSeedProposalAsync(ServiceProvider runtime, PlanSeedDescriptor descriptor)
    {
        var config = runtime.GetRequiredService<TwigConfiguration>();
        var file = Path.Combine(runtime.GetRequiredService<TwigPaths>().TwigDir, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            version = 1,
            workspace = new { organization = config.Organization, project = config.Project },
            operations = new[] { new { id = "publish-bound", kind = "publish-seed", stagedIdentity = descriptor.Identity.ToString(), expectedFingerprint = descriptor.Fingerprint } },
        }));
        return file;
    }

    private static async Task<(int Exit, string Output, string Error)> CaptureAsync(Func<Task<int>> run)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try { Console.SetOut(output); Console.SetError(error); return (await run(), output.ToString(), error.ToString()); }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }

    private sealed class PrincipalTransport : HttpMessageHandler
    {
        private readonly Dictionary<int, (string Title, string Owner, string Project, int Revision)> _items = new()
        {
            [42] = ("Private 42", First, "Work", 1),
            [43] = ("Private 43", Second, "Personal", 1),
        };
        public int CreatedCount { get; private set; }
        public int PatchCount { get; private set; }
        public bool MissingCanonicalIdentity { get; set; }
        public bool UnprovedAssignment { get; set; }
        public string OwnerOf(int id) => _items[id].Owner;
        public void SetOwner(int id, string owner)
        {
            var item = _items[id];
            item.Owner = owner;
            _items[id] = item;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var header = request.Headers.Authorization;
            if (header?.Scheme != "Basic") return Reply(HttpStatusCode.Unauthorized, new { message = "Missing fixture credential" });
            var account = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!))[1..];
            if (account != First && account != Second) return Reply(HttpStatusCode.Unauthorized, new { message = "Unknown fixture account" });
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { authenticatedUser = new { id = account == First ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222", providerDisplayName = "Alex Reader" } });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { displayName = "Alex Reader", emailAddress = MissingCanonicalIdentity ? null : account });
            var project = path.Contains("/Personal/", StringComparison.Ordinal) ? "Personal" : "Work";
            if (path.Contains("teamsettings/iterations", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { value = new[] { new { id = "sprint", path = project, name = "Sprint", attributes = new { startDate = DateTimeOffset.UtcNow.AddDays(-1), finishDate = DateTimeOffset.UtcNow.AddDays(1) } } } });
            if (path.Contains("/_apis/wit/workitems/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = path[(path.LastIndexOf('/') + 1)..];
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var fields = body.RootElement.EnumerateArray().Where(x => x.GetProperty("path").GetString()!.StartsWith("/fields/", StringComparison.Ordinal))
                        .ToDictionary(x => x.GetProperty("path").GetString()![8..], x => x.GetProperty("value").GetString());
                    var owner = fields.GetValueOrDefault("System.AssignedTo");
                    // Recognized-identity resolution, not field forwarding: ADO rejects an
                    // unknown global display-name default even if authentication is valid.
                    if (owner != First && owner != Second) return Reply(HttpStatusCode.BadRequest, new { message = "Unknown assignee" });
                    var id = 100 + ++CreatedCount;
                    _items[id] = (fields["System.Title"]!, owner, project, 1);
                    return Item(id, project);
                }
                var workId = int.Parse(tail, System.Globalization.CultureInfo.InvariantCulture);
                if (request.Method == HttpMethod.Patch)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var item = _items[workId];
                    foreach (var operation in body.RootElement.EnumerateArray())
                    {
                        var opPath = operation.GetProperty("path").GetString();
                        if (opPath == "/rev" && operation.GetProperty("value").GetInt32() != item.Revision)
                            return Reply(HttpStatusCode.PreconditionFailed, new { message = "Revision conflict" });
                        if (opPath == "/fields/System.AssignedTo") item.Owner = operation.GetProperty("value").GetString()!;
                    }
                    PatchCount++;
                    item.Revision++;
                    _items[workId] = item;
                }
                return Item(workId, project);
            }
            return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });
        }

        private HttpResponseMessage Item(int id, string project)
        {
            var item = _items[id];
            return Reply(HttpStatusCode.OK, new
            {
                id,
                rev = item.Revision,
                fields = new Dictionary<string, object>
                {
                    ["System.Title"] = item.Title,
                    ["System.WorkItemType"] = "Task",
                    ["System.State"] = "To Do",
                    ["System.AreaPath"] = project,
                    ["System.IterationPath"] = project,
                    ["System.AssignedTo"] = new
                    {
                        displayName = UnprovedAssignment && id == 43 ? First : "Alex Reader",
                        uniqueName = UnprovedAssignment && id == 43 ? null : item.Owner,
                    },
                },
                relations = Array.Empty<object>(),
            });
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}
