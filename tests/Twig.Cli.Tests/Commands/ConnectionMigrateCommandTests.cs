using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Collection("ConsoleRedirect")]
[Trait("Category", "HostInventory")]
public sealed class ConnectionMigrateCommandTests : IAsyncLifetime
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Principal = "22222222-2222-2222-2222-222222222222";
    private const string Pat = "fixture-private-pat";
    private readonly string _temp = CanonicalTempRoot.Create("twig-migration-consumer-");
    private readonly PrincipalTransport _transport = new();
    private ConnectionBindingService _bindings = null!;
    private ConnectionMigrationService _migration = null!;
    private TwigConfiguration _configuration = null!;
    private TwigPaths _paths = null!;
    private HttpClient _http = null!;
    private string Home => Path.Combine(_temp, "home");

    public async Task InitializeAsync()
    {
        var repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(repo);
        InitCommandTestFixture.InitTempWorktree(repo).ShouldBeTrue();
        _configuration = new TwigConfiguration { Organization = "fixture", Project = "Work" };
        _configuration.Display.Hints = false;
        _paths = TwigPaths.BuildPaths(Path.Combine(repo, ".twig"), _configuration, repo);
        await _configuration.SaveSplitAsync(_paths);
        using (var attachment = new WorktreeLocalAttachmentStore(_paths, _configuration, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        Directory.CreateDirectory(Home);
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var anchor = WorktreeAnchorDetector.Detect(repo)!.Value;
            var reference = ConnectionRefResolver.Compute(_configuration);
            (await registry.UpsertConnectionAsync(reference, "fixture", "Work", null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), reference, repo)).IsSuccess.ShouldBeTrue();
        }
        var refresh = new TwigRefreshTokenStore(Path.Combine(Home, ".refresh-token"));
        refresh.TryWrite(new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "fixture-private-refresh",
            ClientId = "fixture-client",
            TenantId = Tenant,
            ObjectId = Principal,
            AuthorityHost = "login.microsoftonline.com",
            Source = "fixture"
        });
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DbPath)!);
        using (var store = new SqliteCacheStore($"Data Source={_paths.DbPath}"))
        {
            var repository = new SqliteWorkItemRepository(store, new WorkItemMapper());
            var oldRead = new WorkItem { Id = 99, Type = WorkItemType.Task, Title = "Legacy private read" };
            oldRead.MarkSynced(1);
            await repository.SaveAsync(oldRead);
        }
        _http = new HttpClient(_transport);
        _bindings = new ConnectionBindingService(Home, new FixtureRefresher(), patHttpClient: _http);
        _migration = new ConnectionMigrationService(Home, _bindings);
    }

    public Task DisposeAsync()
    {
        _migration.Dispose();
        _bindings.Dispose();
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [HostMigrationFact]
    public async Task ActualLegacyProviderAndStoreMustCloseBeforeActivationThenCurrentReadUsesVerifiedPrincipal()
    {
        var legacyCache = new TwigTokenFileCache(Path.Combine(Home, ".token-cache"));
        legacyCache.TryWrite(Token(), DateTimeOffset.UtcNow.AddHours(1));
        using var legacyProvider = new AdoAccessTokenProvider(fileCache: legacyCache,
            refreshStore: new TwigRefreshTokenStore(Path.Combine(Home, ".refresh-token")));
        var legacyClient = new AdoRestClient(_http, legacyProvider, "fixture", "Work", new WorkItemMapper());
        (await legacyClient.FetchAsync(42)).Title.ShouldBe("AAD private release plan");
        var beforeMigrationRequests = _transport.WorkRequests;
        using var legacyStore = new SqliteCacheStore($"Data Source={_paths.DbPath}");
        var blocked = await InvokeMigrationAsync("legacy", "aad");
        blocked.Exit.ShouldBe(1);
        blocked.Body.ShouldNotBeNull(blocked.Error);
        blocked.Body!.RootElement.GetProperty("blockers").EnumerateArray()
            .Any(x => x.GetString()!.Contains("legacy-store-open", StringComparison.Ordinal)).ShouldBeTrue();
        File.Exists(_paths.DbPath).ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(beforeMigrationRequests);
        legacyStore.Dispose();
        legacyProvider.Dispose();
        var eligible = await InvokeMigrationAsync("legacy", "aad");
        eligible.Exit.ShouldBe(0, eligible.Error);
        var applied = await InvokeMigrationAsync("legacy", "aad", eligible.Body!.RootElement.GetProperty("digest").GetString());
        applied.Exit.ShouldBe(0, applied.Error);
        applied.Body!.RootElement.GetProperty("state").GetString().ShouldBe("active");
        await AssertReadAsync("AAD private release plan");
        Should.Throw<InvalidOperationException>(() => new SqliteCacheStore($"Data Source={_paths.DbPath}"));
        using var rawReopen = new SqliteConnection($"Data Source={_paths.DbPath};Pooling=False");
        Should.Throw<SqliteException>(() => rawReopen.Open());
        Should.Throw<InvalidOperationException>(() => new AdoAccessTokenProvider(fileCache: legacyCache,
            refreshStore: new TwigRefreshTokenStore(Path.Combine(Home, ".refresh-token"))));
        var workRequests = _transport.WorkRequests;
        await Should.ThrowAsync<ObjectDisposedException>(() => legacyClient.FetchAsync(42));
        _transport.WorkRequests.ShouldBe(workRequests);
    }

    [HostMigrationFact]
    public async Task MigrationPreservesPendingNotesSeedAndHistoricalNativeJournalWithoutPublishingOrDiscarding()
    {
        await RegisterExistingIdentityAsync();
        string digest;
        int seedAlias;
        var proposal = Path.Combine(_paths.TwigDir, "historical.json");
        await File.WriteAllTextAsync(proposal,
            "{\"version\":1,\"workspace\":{\"organization\":\"fixture\",\"project\":\"Work\"},\"operations\":[{\"id\":\"old-unknown\",\"kind\":\"batch\",\"workItemId\":42,\"expectedRevision\":1,\"fields\":{\"System.Title\":\"Unproved old intent\"}}]}");
        using (var runtime = Services(_paths))
        {
            var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
            seedAlias = minted.Alias.Value;
            var seed = new SeedFactory().CreateUnparented("Private local draft", WorkItemType.Task,
                AreaPath.Parse("Work").Value, IterationPath.Parse("Work").Value, minted);
            seed.IsSuccess.ShouldBeTrue(seed.Error);
            await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(seed.Value);
            var editing = new WorkItem { Id = 77, Type = WorkItemType.Task, Title = "Pending actor work" };
            editing.UpdateField("System.Title", "Unpublished edit");
            await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(editing);
            var pending = runtime.GetRequiredService<IPendingChangeStore>();
            await pending.AddChangeAsync(77, "field", "System.Title", "Pending actor work", "Unpublished edit");
            await pending.AddChangeAsync(77, "note", null, null, "Private pending note");
            digest = (await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(proposal)).Digest!;
            await runtime.GetRequiredService<IPlanJournalRepository>().SaveOperationErrorAsync(digest, "old-unknown",
                "Earlier unacknowledged outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        }
        var portable = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var legacyCredential = await File.ReadAllBytesAsync(Path.Combine(Home, ".refresh-token"));
        var preview = await InvokeMigrationAsync("legacy", null);
        preview.Exit.ShouldBe(0, preview.Error);
        var apply = await InvokeMigrationAsync("legacy", null, preview.Body!.RootElement.GetProperty("digest").GetString());
        apply.Exit.ShouldBe(0, apply.Error);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(portable);
        (await File.ReadAllBytesAsync(Path.Combine(Home, ".refresh-token"))).ShouldBe(legacyCredential);
        using var current = Services(CurrentPaths());
        var edits = await current.GetRequiredService<IPendingChangeStore>().GetChangesAsync(77);
        edits.Select(x => x.NewValue).ShouldBe(new[] { "Unpublished edit", "Private pending note" });
        var seeds = await current.GetRequiredService<IWorkItemRepository>().GetSeedsAsync();
        seeds.ShouldHaveSingleItem().Id.ShouldBe(seedAlias);
        seeds.Single().Title.ShouldBe("Private local draft");
        var journal = await current.GetRequiredService<IPlanJournalRepository>().GetAsync(digest);
        journal!.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        File.Exists(proposal).ShouldBeTrue();
        _transport.Mutations.ShouldBe(0);
        // The pending note remains durable even though an unprotected read-only cached item is cold.
        (await current.GetRequiredService<IWorkItemRepository>().GetByIdAsync(99)).ShouldBeNull();
    }

    [HostMigrationFact]
    public async Task VerifiedPatImportIsIdempotentAndCannotResumeTheRetiredLegacyPatProvider()
    {
        await File.WriteAllTextAsync(_paths.ConfigPath, "{\"auth\":{\"pat\":\"" + Pat + "\"},\"display\":{\"icons\":\"unicode\"}}");
        using var legacy = new PatAuthProvider(_ => null, () => Pat, Home);
        var legacyClient = new AdoRestClient(_http, legacy, "fixture", "Work", new WorkItemMapper());
        (await legacyClient.FetchAsync(42)).Title.ShouldBe("PAT private release plan");
        (await InvokeMigrationAsync("pat-legacy", "pat")).Exit.ShouldBe(1);
        legacy.Dispose();
        var preview = await InvokeMigrationAsync("pat-legacy", "pat");
        preview.Exit.ShouldBe(0, preview.Error);
        var active = await InvokeMigrationAsync("pat-legacy", "pat", preview.Body!.RootElement.GetProperty("digest").GetString());
        active.Exit.ShouldBe(0, active.Error);
        await AssertReadAsync("PAT private release plan");
        var identities = await _bindings.ListIdentitiesAsync();
        identities.ShouldHaveSingleItem().AdoPrincipalId.ShouldBe(Principal);
        var credentialReference = identities.Single().CredentialRef;
        var repeat = await InvokeMigrationAsync("pat-legacy", "pat");
        repeat.Exit.ShouldBe(0, repeat.Error);
        (await InvokeMigrationAsync("pat-legacy", "pat", repeat.Body!.RootElement.GetProperty("digest").GetString())).Exit.ShouldBe(0);
        (await _bindings.ListIdentitiesAsync()).ShouldHaveSingleItem().CredentialRef.ShouldBe(credentialReference);
        Should.Throw<InvalidOperationException>(() => new PatAuthProvider(_ => null, () => Pat, Home));
        var requests = _transport.WorkRequests;
        await Should.ThrowAsync<ObjectDisposedException>(() => legacyClient.FetchAsync(42));
        _transport.WorkRequests.ShouldBe(requests);
        repeat.Output.ShouldNotContain(Pat);
        active.Output.ShouldNotContain(Pat);
    }

    [HostMigrationFact]
    public async Task InterruptedActivationCannotExposePartialGenerationAndResumesOnlyTheOriginalNativeIntent()
    {
        await RegisterExistingIdentityAsync();
        var preview = await InvokeMigrationAsync("legacy", null);
        preview.Exit.ShouldBe(0, preview.Error);
        var marker = Path.Combine(_paths.TwigDir, "cache", MirrorAdmission.MarkerFile);
        Directory.CreateDirectory(marker); // A real local write failure after the native intent and mirror preparation.
        var interrupted = await InvokeMigrationAsync("legacy", null, preview.Body!.RootElement.GetProperty("digest").GetString());
        interrupted.Exit.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => _bindings.ResolveAsync(_configuration, _paths));
        _transport.WorkRequests.ShouldBe(0);
        Directory.Delete(marker);
        var recovery = await InvokeMigrationAsync("legacy", null);
        recovery.Exit.ShouldBe(0, recovery.Error);
        recovery.Body!.RootElement.GetProperty("state").GetString().ShouldBe("preparing");
        var resumed = await InvokeMigrationAsync("legacy", null, recovery.Body.RootElement.GetProperty("digest").GetString());
        resumed.Exit.ShouldBe(0, resumed.Error);
        await AssertReadAsync("AAD private release plan");
    }

    [Fact]
    public void SidecarMoveFailureKeepsRelocatedLegacyDatabaseSealed()
    {
        var source = Path.Combine(_temp, "retired-mirror.db");
        var destination = Path.Combine(_temp, "admitted-mirror.db");
        File.WriteAllText(source, "Protected baseline bytes");
        File.WriteAllText(source + "-shm", "Protected sidecar bytes");
        Directory.CreateDirectory(destination + "-shm");

        Should.Throw<IOException>(() => ConnectionMigrationService.MoveDatabase(source, destination, retireSource: true));

        Should.Throw<Exception>(() =>
        {
            using var reopened = new FileStream(source, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        });
        File.ReadAllText(destination).ShouldBe("Protected baseline bytes");
        File.ReadAllText(source + "-shm").ShouldBe("Protected sidecar bytes");
    }

    [Fact]
    public async Task MissingExplicitMappingAndUnsupportedVersionLeaveLegacyStateUntouched()
    {
        var before = await File.ReadAllBytesAsync(_paths.DbPath);
        var ambiguous = await InvokeMigrationAsync(null, "aad");
        ambiguous.Exit.ShouldBe(1);
        (await File.ReadAllBytesAsync(_paths.DbPath)).ShouldBe(before);
        using (var db = new SqliteConnection($"Data Source={_paths.DbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE metadata SET value='999' WHERE key='schema_version';";
            cmd.ExecuteNonQuery();
        }
        var unsupported = await InvokeMigrationAsync("legacy", "aad");
        unsupported.Exit.ShouldBe(1);
        unsupported.Body!.RootElement.GetProperty("blockers").EnumerateArray()
            .Any(x => x.GetString()!.Contains("migration-mirror-version-unsupported", StringComparison.Ordinal)).ShouldBeTrue();
        File.Exists(_paths.DbPath).ShouldBeTrue();
        (await _bindings.ListIdentitiesAsync()).ShouldBeEmpty();
        _transport.WorkRequests.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RawBaselineSqliteHandleBlocksWithoutParticipatingInAnyNewLeaseLedger()
    {
        using var baseline = new SqliteConnection($"Data Source={_paths.DbPath};Pooling=False");
        baseline.Open();
        using (var previousShape = baseline.CreateCommand())
        {
            previousShape.CommandText = "ALTER TABLE work_items DROP COLUMN assigned_to_unique_name; UPDATE metadata SET value='16' WHERE key='schema_version';";
            previousShape.ExecuteNonQuery();
        }
        var blocked = await InvokeMigrationAsync("legacy", "aad");
        blocked.Exit.ShouldBe(1);
        File.Exists(_paths.DbPath).ShouldBeTrue();
        baseline.Dispose();
        var closed = await InvokeMigrationAsync("legacy", "aad");
        closed.Exit.ShouldBe(0, closed.Error);
        (await InvokeMigrationAsync("legacy", "aad", closed.Body!.RootElement.GetProperty("digest").GetString())).Exit.ShouldBe(0);
        await AssertReadAsync("AAD private release plan");
    }

    [HostMigrationFact]
    public async Task InconclusiveOsInspectionIsAConsumerVisibleBlockerNotAnEmptyInventory()
    {
        using var unavailable = new ConnectionMigrationService(Home, _bindings,
            _ => throw new UnauthorizedAccessException("Fixture OS command-line inspection denied"));
        var snapshot = await unavailable.PreviewAsync(_configuration, _paths, "legacy", "aad");
        snapshot.CanApply.ShouldBeFalse();
        snapshot.Blockers.Any(x => x.Contains("host-inventory-inconclusive", StringComparison.Ordinal)).ShouldBeTrue();
        var refused = await unavailable.ApplyAsync(_configuration, _paths, "legacy", "aad", snapshot.Digest);
        refused.CanApply.ShouldBeFalse();
        File.Exists(_paths.DbPath).ShouldBeTrue();
        (await _bindings.ListIdentitiesAsync()).ShouldBeEmpty();
        _transport.WorkRequests.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ReplacedWrongAccountCredentialRefusesBeforeCreatingDefaultOrMovingLegacyState()
    {
        var identity = await _bindings.RegisterPatIdentityAsync("broken", "fixture", Pat);
        new PatCredentialStore(Path.Combine(Home, "credentials", identity.CredentialRef + ".json")).Write("wrong-account-pat");
        var before = await File.ReadAllBytesAsync(_paths.DbPath);
        var preview = await InvokeMigrationAsync("broken", null);
        preview.Exit.ShouldBe(0, preview.Error);
        var apply = await InvokeMigrationAsync("broken", null, preview.Body!.RootElement.GetProperty("digest").GetString());
        apply.Exit.ShouldBe(1);
        (await _bindings.ListBindingsAsync("fixture", "Work")).ShouldBeEmpty();
        (await File.ReadAllBytesAsync(_paths.DbPath)).ShouldBe(before);
        _transport.WorkRequests.ShouldBe(0);
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task AnotherMetadataHomeCannotAdoptAnAdmittedCacheEvenForACacheOnlyCliRead()
    {
        var preview = await InvokeMigrationAsync("legacy", "aad");
        preview.Exit.ShouldBe(0, preview.Error);
        (await InvokeMigrationAsync("legacy", "aad", preview.Body!.RootElement.GetProperty("digest").GetString())).Exit.ShouldBe(0);
        await AssertReadAsync("AAD private release plan");
        using var otherAuthority = new ConnectionBindingService(Path.Combine(_temp, "other-home"), new FixtureRefresher(), patHttpClient: _http);
        using var otherRuntime = Services(CurrentPaths(), otherAuthority);
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var requests = _transport.WorkRequests;
        try
        {
            Console.SetOut(output);
            Console.SetError(errors);
            await Should.ThrowAsync<InvalidOperationException>(() => new TwigCommands(otherRuntime).Show(42, "json", refresh: false));
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
        output.ToString().ShouldNotContain("AAD private release plan");
        _transport.WorkRequests.ShouldBe(requests);
    }

    [HostMigrationFact]
    public async Task PortableEditDuringPrincipalVerificationRefusesOriginalConfirmationAndKeepsRecoverableIntent()
    {
        var originalManifest = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var originalMirror = await File.ReadAllBytesAsync(_paths.DbPath);
        var changedManifest = new TwigConfiguration { Organization = "fixture", Project = "ConcurrentEndpoint" };
        using var verifier = new ConnectionBindingService(Home, new FixtureRefresher(() =>
            changedManifest.SaveRepoAsync(_paths.RepoConfigPath).GetAwaiter().GetResult()), patHttpClient: _http);
        using var migration = new ConnectionMigrationService(Home, verifier);
        var preview = await migration.PreviewAsync(_configuration, _paths, "legacy", "aad");
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            migration.ApplyAsync(_configuration, _paths, "legacy", "aad", preview.Digest));
        (await File.ReadAllBytesAsync(_paths.DbPath)).ShouldBe(originalMirror);
        _transport.WorkRequests.ShouldBe(0);
        _transport.Mutations.ShouldBe(0);
        await Should.ThrowAsync<InvalidOperationException>(() => verifier.ResolveAsync(_configuration, _paths));

        await File.WriteAllBytesAsync(_paths.RepoConfigPath, originalManifest);
        var recovery = await migration.PreviewAsync(_configuration, _paths, "legacy", "aad");
        recovery.CanApply.ShouldBeTrue(string.Join("\n", recovery.Blockers));
        recovery.State.ShouldBe("preparing");
        (await migration.ApplyAsync(_configuration, _paths, "legacy", "aad", recovery.Digest)).State.ShouldBe("active");
        await AssertReadAsync("AAD private release plan");
    }

    private async Task RegisterExistingIdentityAsync()
    {
        await _bindings.RegisterAadIdentityAsync("legacy", new TwigRefreshTokenStore(Path.Combine(Home, ".refresh-token")).TryRead()!);
        await _bindings.CreateBindingAsync("fixture", "Work", "legacy", makeDefault: true);
    }

    private TwigPaths CurrentPaths() => TwigPaths.BuildPaths(_paths.TwigDir, _configuration, _paths.StartDir);

    private ServiceProvider Services(TwigPaths paths, IConnectionBindingService? bindingAuthority = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(bindingAuthority ?? _bindings);
        services.AddSingleton<ISystemWorktreeRegistry>(_ => new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System));
        services.AddConnectionServices(_configuration, paths.TwigDir, paths.StartDir);
        services.AddSingleton(paths);
        services.AddSingleton(_http);
        services.AddSingleton<IConnectionMigrationService>(_migration);
        services.AddTwigNetworkServices(_configuration);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private async Task<(int Exit, JsonDocument? Body, string Output, string Error)> InvokeMigrationAsync(
        string? identity, string? method, string? confirm = null, string output = "json")
    {
        using var runtime = Services(CurrentPaths());
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var text = new StringWriter();
        using var errors = new StringWriter();
        try
        {
            Console.SetOut(text);
            Console.SetError(errors);
            var exit = await new TwigCommands(runtime).ConnectionMigrate(identity, method, confirm, output);
            var body = text.ToString();
            return (exit, body.Length == 0 || output != "json" ? null : JsonDocument.Parse(body), body, errors.ToString() + "\n" + body);
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }

    private async Task AssertReadAsync(string expectedTitle)
    {
        using var runtime = Services(CurrentPaths());
        var stdout = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            (await new TwigCommands(runtime).Show(42, "json", refresh: true)).ShouldBe(0);
        }
        finally { Console.SetOut(stdout); }
        using var document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("title").GetString().ShouldBe(expectedTitle);
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(99)).ShouldBeNull();
    }

    private static string Token()
    {
        var payload = JsonSerializer.Serialize(new
        {
            aud = "499b84ac-1321-427f-aa17-267ca6975798",
            tid = Tenant,
            oid = Principal,
            iss = "https://sts.windows.net/" + Tenant + "/",
            exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        });
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
    }

    private sealed class FixtureRefresher(Action? duringRefresh = null) : ITokenRefresher
    {
        public Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> TryRefreshAsync(
            string refreshToken, string clientId, string tenantId, string authorityHost, CancellationToken ct = default)
        {
            duringRefresh?.Invoke();
            return Task.FromResult<(string?, string?, bool)>((Token(), null, false));
        }
    }

    private sealed class PrincipalTransport : HttpMessageHandler
    {
        internal int WorkRequests { get; private set; }
        internal int Mutations { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) Mutations++;
            var header = request.Headers.Authorization;
            var pat = header?.Scheme == "Basic" && header.Parameter == Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));
            var aad = header?.Scheme == "Bearer" && JwtAccessTokenInspector.TryDecode(header.Parameter)?.ObjectId == Principal;
            if (request.RequestUri!.AbsolutePath.EndsWith("connectionData", StringComparison.Ordinal))
                return Response(pat ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
                    "{\"authenticatedUser\":{\"id\":\"" + Principal + "\",\"providerDisplayName\":\"Fixture actor\"}}");
            if (request.RequestUri.AbsolutePath.Contains("/workitems", StringComparison.Ordinal))
            {
                WorkRequests++;
                if (!aad && !pat) return Response(HttpStatusCode.Forbidden, "{\"message\":\"No authority\"}");
                return Response(HttpStatusCode.OK, "{\"id\":42,\"rev\":1,\"fields\":{\"System.Title\":\"" + (pat ? "PAT" : "AAD")
                    + " private release plan\",\"System.WorkItemType\":\"Task\",\"System.State\":\"To Do\",\"System.AreaPath\":\"Work\",\"System.IterationPath\":\"Work\"},\"relations\":[]}");
            }
            return Response(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");
        }
        private static Task<HttpResponseMessage> Response(HttpStatusCode code, string body)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
