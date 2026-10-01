using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Spec #1103 acceptance 4, 5 and 14 — PAT-method connection binding behaviour
/// exercised through the real <see cref="TwigCommands"/> command surface:
/// <c>auth pat</c> (stdin-fed secret), <c>auth clear --identity</c>, and
/// <c>show --refresh</c>. Every test uses a real temporary metadata home,
/// SQLite system registry, local attachment store, and an identity-aware
/// transport that maps distinct PAT secrets to stable ADO principal ids;
/// no mock providers or forwarding assertions.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class PatConnectionBindingReadTests : IAsyncLifetime
{
    private const string Org = "fixture";
    private const string Project = "Corporate";

    private const string CorporateReaderPat = "pat-corp-reader-v1";
    private const string CorporateReaderPatRotated = "pat-corp-reader-v2";
    private const string PersonalPat = "pat-personal-v1";

    private const string CorporatePrincipalId = "11111111-1111-1111-1111-111111111111";
    private const string PersonalPrincipalId = "22222222-2222-2222-2222-222222222222";
    private const string CorporateDisplayName = "Alex Reader";
    private const string PersonalDisplayName = "Alex Reader"; // same display name, different principal.

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-pat-binding-" + Guid.NewGuid().ToString("N"));
    private IdentityAwarePatTransport _transport = null!;
    private HttpClient _workHttpClient = null!;
    private HttpClient _patHttpClient = null!;
    private ConnectionBindingService _bindings = null!;
    private TwigConfiguration _config = null!;
    private TwigPaths _paths = null!;
    private string _home = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        var repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(repo);
        InitCommandTestFixture.InitTempWorktree(repo).ShouldBeTrue();
        _home = Path.Combine(_temp, "user");

        _config = new TwigConfiguration { Organization = Org, Project = Project };
        _config.Display.Hints = false;
        _paths = TwigPaths.BuildPaths(Path.Combine(repo, ".twig"), _config, repo);
        _paths = new TwigPaths(_paths.TwigDir, _paths.ConfigPath, _paths.DbPath, repo, Path.Combine(_home, "display.json"));
        await _config.SaveSplitAsync(_paths);

        using (var attachment = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();

        var anchor = WorktreeAnchorDetector.Detect(repo)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(_home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(_config);
            (await registry.UpsertConnectionAsync(connection, _config.Organization, _config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }

        _transport = new IdentityAwarePatTransport(new[]
        {
            new PatPrincipalRecord(CorporateReaderPat, CorporatePrincipalId, CorporateDisplayName, CanReadCorporate: true, "Corporate-only release plan v1"),
            new PatPrincipalRecord(CorporateReaderPatRotated, CorporatePrincipalId, CorporateDisplayName, CanReadCorporate: true, "Corporate-only release plan v2"),
            new PatPrincipalRecord(PersonalPat, PersonalPrincipalId, PersonalDisplayName, CanReadCorporate: false, "Personal backlog"),
        });
        _workHttpClient = new HttpClient(_transport, disposeHandler: false);
        _patHttpClient = new HttpClient(_transport, disposeHandler: false);
        _bindings = new ConnectionBindingService(_home, refresher: null, clock: null, patHttpClient: _patHttpClient);
    }

    public Task DisposeAsync()
    {
        _bindings?.Dispose();
        _workHttpClient?.Dispose();
        _patHttpClient?.Dispose();
        _transport?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort cleanup */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnrollmentThenPrivateConsumerReadReturnsTheBoundPrincipalsPrivateItem()
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);

        (await RunAuthPatAsync(command, identity: "work", org: Org, pat: CorporateReaderPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);

        var attestationsBefore = _transport.ConnectionDataRequests;
        var json = await RunShowCapturedAsync(command);
        _transport.ConnectionDataRequests.ShouldBeGreaterThan(attestationsBefore);
        _transport.WorkRequests.ShouldBe(1);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Corporate-only release plan v1");
    }

    [Fact]
    public async Task WrongSameNamePrincipalRepairIsRefusedAndPreservesTheExistingPatBytes()
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);

        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);

        var credentialPath = await FindCredentialPathAsync("work");
        var savedBytes = await File.ReadAllBytesAsync(credentialPath);
        var workBefore = _transport.WorkRequests;

        (await RunAuthPatAsync(command, "work", Org, PersonalPat)).ShouldBe(1);

        (await File.ReadAllBytesAsync(credentialPath)).ShouldBe(savedBytes);
        _transport.WorkRequests.ShouldBe(workBefore);
    }

    [Fact]
    public async Task RawReplacementOfTheBoundSecretIsRejectedBeforeWorkHttpOnTheAlreadyOpenRuntime()
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);

        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);
        (await RunShowAsync(command)).ShouldBe(0);
        var workBefore = _transport.WorkRequests;
        workBefore.ShouldBe(1);

        // Raw-overwrite the credential JSON with a secret that resolves to a
        // different principal. Admission proof must be re-attested per request
        // from the exact on-disk material; the already-open bound provider
        // cannot carry trust across changed bytes.
        var credentialPath = await FindCredentialPathAsync("work");
        await File.WriteAllTextAsync(credentialPath, "{\"pat\":\"" + PersonalPat + "\"}");

        (await RunShowAsync(command)).ShouldBe(1);
        _transport.WorkRequests.ShouldBe(workBefore);
    }

    [Fact]
    public async Task CommandDrivenSamePrincipalRenewalPreservesPendingChangesAndMirrorReadStateAndReturnsRefreshedPrivateSnapshot()
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);
        var pendingStore = services.GetRequiredService<IPendingChangeStore>();
        var workItemRepo = services.GetRequiredService<IWorkItemRepository>();

        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPat)).ShouldBe(0);
        (await RunAuthPatAsync(command, "personal", Org, PersonalPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);
        var first = await RunShowCapturedAsync(command);
        using (var firstDoc = JsonDocument.Parse(first))
            firstDoc.RootElement.GetProperty("title").GetString().ShouldBe("Corporate-only release plan v1");

        var revisionBefore = (await _bindings.ListBindingsAsync(Org, Project)).Single().Revision;
        var siblingPath = await FindCredentialPathAsync("personal");
        var siblingBefore = await File.ReadAllBytesAsync(siblingPath);
        var cachedBefore = await workItemRepo.GetByIdAsync(42);
        cachedBefore.ShouldNotBeNull();
        await pendingStore.AddChangeAsync(42, "note", null, null, "integration-check");
        await pendingStore.AddChangeAsync(42, "field", "System.Description", null, "locally staged description");
        (await pendingStore.GetChangesAsync(42)).ShouldContain(c => c.NewValue == "integration-check");
        var attestationsBefore = _transport.ConnectionDataRequests;

        // Renew through the real command surface — the credential rotation and
        // its mandatory attestation go through RegisterPatIdentityAsync, not a
        // raw file poke, and the open BoundPatAuthenticationProvider instance
        // re-attests exactly once off the new material before the next work HTTP.
        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPatRotated)).ShouldBe(0);
        _transport.ConnectionDataRequests.ShouldBeGreaterThan(attestationsBefore);


        // A dirty item intentionally retains its local snapshot. Read a clean
        // private item to observe the repaired material, without flushing edits.
        var second = await RunShowCapturedAsync(command, 43);
        using var secondDoc = JsonDocument.Parse(second);
        secondDoc.RootElement.GetProperty("title").GetString().ShouldBe("Corporate-only release plan v2");
        // Durable pending changes and the already-cached mirror snapshot MUST
        // survive a credential renewal — admission proof lifecycle is strictly
        // independent of workspace state.
        (await pendingStore.GetChangesAsync(42)).ShouldContain(c => c.NewValue == "integration-check");
        (await pendingStore.GetChangesAsync(42)).ShouldContain(c => c.FieldName == "System.Description" && c.NewValue == "locally staged description");
        (await workItemRepo.GetByIdAsync(42))!.Title.ShouldBe(cachedBefore!.Title);

        (await _bindings.ListBindingsAsync(Org, Project)).Single().Revision.ShouldBe(revisionBefore);
        (await File.ReadAllBytesAsync(siblingPath)).ShouldBe(siblingBefore);
    }

    [Fact]
    public async Task AuthClearNamedIdentityPreservesStoredBytesForEveryIdentityAndPermitsReattestation()
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);

        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPat)).ShouldBe(0);
        (await RunAuthPatAsync(command, "personal", Org, PersonalPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);
        (await RunShowAsync(command)).ShouldBe(0);

        var workCredentialPath = await FindCredentialPathAsync("work");
        var personalCredentialPath = await FindCredentialPathAsync("personal");
        var workBefore = await File.ReadAllBytesAsync(workCredentialPath);
        var personalBefore = await File.ReadAllBytesAsync(personalCredentialPath);

        (await command.AuthClear("json", CancellationToken.None, identity: "work")).ShouldBe(0);

        (await File.ReadAllBytesAsync(workCredentialPath)).ShouldBe(workBefore);
        (await File.ReadAllBytesAsync(personalCredentialPath)).ShouldBe(personalBefore);

        var attestationsBefore = _transport.ConnectionDataRequests;
        (await RunShowAsync(command)).ShouldBe(0);
        _transport.ConnectionDataRequests.ShouldBeGreaterThan(attestationsBefore);
    }

    [Fact]
    public async Task NamedRenewalUsesItsSavedAuthorityInsteadOfAnUnrelatedAttachedIdentity()
    {
        using var selectedServices = CreateServices();
        var selected = new TwigCommands(selectedServices);
        (await RunAuthPatAsync(selected, "work", Org, CorporateReaderPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);
        (await RunAuthPatAsync(selected, "personal", "other-fixture", PersonalPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync("other-fixture", Project, "personal", makeDefault: true);

        var repo = Path.Combine(_temp, "other-repo");
        Directory.CreateDirectory(repo);
        InitCommandTestFixture.InitTempWorktree(repo).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "other-fixture", Project = Project };
        config.Display.Hints = false;
        var paths = TwigPaths.BuildPaths(Path.Combine(repo, ".twig"), config, repo);
        paths = new TwigPaths(paths.TwigDir, paths.ConfigPath, paths.DbPath, repo, Path.Combine(_home, "display.json"));
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(repo)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(_home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }
        using var otherServices = CreateServices(config, paths);
        var unrelated = new TwigCommands(otherServices);
        (await RunAuthPatAsync(unrelated, "work", null, CorporateReaderPatRotated)).ShouldBe(0);

        // The selected checkout observes the repaired material, not merely a success echo.
        var json = await RunShowCapturedAsync(selected);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Corporate-only release plan v2");
    }

    [Fact]
    public async Task LegacyEnvironmentPatCannotOverrideTheBoundPatRuntimeOrLeakIntoThePrivateReadSnapshot()
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);

        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPat)).ShouldBe(0);
        await _bindings.CreateBindingAsync(Org, Project, "work", makeDefault: true);

        var original = Environment.GetEnvironmentVariable("TWIG_PAT");
        try
        {
            // A spoofed TWIG_PAT would map to the Personal principal (not a
            // reader of the Corporate project). If it leaked, the bound work
            // read would observe Forbidden; its success proves the binding
            // gate ignored the environment entirely.
            Environment.SetEnvironmentVariable("TWIG_PAT", PersonalPat);
            var json = await RunShowCapturedAsync(command);
            using var document = JsonDocument.Parse(json);
            document.RootElement.GetProperty("title").GetString().ShouldBe("Corporate-only release plan v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TWIG_PAT", original);
        }
    }

    [Fact]
    public async Task UnattachedManagementEnrollmentSucceedsButNormalWorkReadFailsBeforeAnyWorkHttp()
    {
        // Erase the local attachment so the fixture is a genuinely unattached
        // workspace — only the central registry rows remain, which the resolver
        // ignores without a validated attachment of its own.
        File.Delete(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName));

        using var services = CreateServices();
        var command = new TwigCommands(services);

        (await RunAuthPatAsync(command, "work", Org, CorporateReaderPat)).ShouldBe(0);
        (await _bindings.ListIdentitiesAsync()).ShouldContain(x => x.Name == "work" && x.Method == "pat");

        var workBefore = _transport.WorkRequests;
        (await RunShowAsync(command)).ShouldBe(1);
        _transport.WorkRequests.ShouldBe(workBefore);
    }

    [Theory]
    [InlineData(AttestationFailure.Unauthorized)]
    [InlineData(AttestationFailure.MissingPrincipal)]
    [InlineData(AttestationFailure.Malformed)]
    public async Task AttestationDiagnosticsNeverEchoThePresentedSecret(AttestationFailure mode)
    {
        using var services = CreateServices();
        var command = new TwigCommands(services);

        _transport.Failure = mode;
        var secret = "pat-leak-check-" + Guid.NewGuid().ToString("N");

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit;
        try
        {
            Console.SetIn(new StringReader(secret + Environment.NewLine));
            Console.SetOut(output);
            Console.SetError(error);
            exit = await command.AuthPat(identity: "work", org: Org, stdin: true, output: "json");
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        exit.ShouldBe(1);
        var combined = output.ToString() + error.ToString();
        combined.ShouldNotContain(secret);
        _transport.WorkRequests.ShouldBe(0);
        // No partial enrollment: the registry MUST stay empty on an attestation refusal.
        (await _bindings.ListIdentitiesAsync()).ShouldBeEmpty();
    }

    // ── fixture plumbing ────────────────────────────────────────────

    private ServiceProvider CreateServices(TwigConfiguration? configuration = null, TwigPaths? paths = null)
    {
        configuration ??= _config;
        paths ??= _paths;
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(configuration, paths.TwigDir, paths.StartDir);
        services.AddSingleton<ISystemWorktreeRegistry>(_ =>
            new SqliteSystemWorktreeRegistry(Path.Combine(_home, "system.db"), TimeProvider.System));
        services.AddSingleton(_workHttpClient);
        services.AddSingleton(paths);
        services.AddTwigNetworkServices(configuration);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private async Task<string> FindCredentialPathAsync(string identityName)
    {
        var identity = (await _bindings.ListIdentitiesAsync()).Single(x => x.Name == identityName);
        var path = Path.Combine(_home, "credentials", identity.CredentialRef + ".json");
        File.Exists(path).ShouldBeTrue($"Expected PAT credential file at {path}");
        return path;
    }

    private static async Task<int> RunAuthPatAsync(TwigCommands command, string identity, string? org, string pat)
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var discardOut = new StringWriter();
        using var discardError = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(pat + Environment.NewLine));
            Console.SetOut(discardOut);
            Console.SetError(discardError);
            return await command.AuthPat(identity: identity, org: org, stdin: true, output: "json");
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<int> RunShowAsync(TwigCommands command)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var discardOut = new StringWriter();
        using var discardError = new StringWriter();
        try
        {
            Console.SetOut(discardOut);
            Console.SetError(discardError);
            return await command.Show(42, "json", refresh: true);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<string> RunShowCapturedAsync(TwigCommands command, int id = 42)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var discardError = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(discardError);
            var exit = await command.Show(id, "json", refresh: true);
            exit.ShouldBe(0, "expected refresh show to succeed; stderr: " + discardError);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        return output.ToString();
    }

    // ── identity-aware ADO transport ────────────────────────────────

    public enum AttestationFailure
    {
        None,
        Unauthorized,
        MissingPrincipal,
        Malformed,
    }

    private sealed record PatPrincipalRecord(
        string Pat,
        string PrincipalId,
        string DisplayName,
        bool CanReadCorporate,
        string PrivateTitle);

    private sealed class IdentityAwarePatTransport : HttpMessageHandler
    {
        private readonly Dictionary<string, PatPrincipalRecord> _byPat;

        public IdentityAwarePatTransport(IEnumerable<PatPrincipalRecord> principals)
        {
            _byPat = new Dictionary<string, PatPrincipalRecord>(StringComparer.Ordinal);
            foreach (var p in principals) _byPat[p.Pat] = p;
        }

        public int ConnectionDataRequests { get; private set; }
        public int WorkRequests { get; private set; }
        public int ProcessRequests { get; private set; }
        public int GitRequests { get; private set; }
        public AttestationFailure Failure { get; set; } = AttestationFailure.None;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var pat = TryDecodePat(request.Headers.Authorization);
            var principal = pat is not null && _byPat.TryGetValue(pat, out var record) ? record : null;

            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
            {
                ConnectionDataRequests++;
                return Failure switch
                {
                    AttestationFailure.Unauthorized => Reply(HttpStatusCode.Unauthorized, "{\"message\":\"Rejected " + pat + "\"}"),
                    AttestationFailure.MissingPrincipal => Reply(HttpStatusCode.OK, "{\"authenticatedUser\":{\"providerDisplayName\":\"" + pat + "\"}}"),
                    AttestationFailure.Malformed => Reply(HttpStatusCode.OK, "{not json " + pat),
                    _ => principal is null
                        ? Reply(HttpStatusCode.Unauthorized, "{\"message\":\"PAT rejected\"}")
                        : Reply(HttpStatusCode.OK, BuildConnectionData(principal)),
                };
            }

            if (path.Contains("/_apis/wit/workitems", StringComparison.Ordinal))
            {
                WorkRequests++;
                if (principal is null) return Reply(HttpStatusCode.Unauthorized, "{\"message\":\"PAT rejected\"}");
                if (!principal.CanReadCorporate) return Reply(HttpStatusCode.Forbidden, "{\"message\":\"Not a reader of this project\"}");
                var id = int.Parse(path.AsSpan(path.LastIndexOf('/') + 1), System.Globalization.CultureInfo.InvariantCulture);
                return Reply(HttpStatusCode.OK, BuildWorkItem(principal, id));
            }

            if (path.Contains("/_apis/git/", StringComparison.Ordinal)
                || path.Contains("/pullrequests", StringComparison.Ordinal))
                GitRequests++;
            if (path.Contains("/_apis/work/processes", StringComparison.Ordinal)
                || path.Contains("/_apis/process/", StringComparison.Ordinal))
                ProcessRequests++;

            return Reply(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");
        }

        private static string BuildConnectionData(PatPrincipalRecord principal) =>
            "{\"authenticatedUser\":{\"id\":\"" + principal.PrincipalId
            + "\",\"providerDisplayName\":\"" + principal.DisplayName + "\"}}";

        private static string BuildWorkItem(PatPrincipalRecord principal, int id)
        {
            var payload = new
            {
                id,
                rev = 1,
                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["System.Title"] = principal.PrivateTitle,
                    ["System.WorkItemType"] = "Task",
                    ["System.State"] = "To Do",
                    ["System.AreaPath"] = "Corporate",
                    ["System.IterationPath"] = "Corporate",
                },
                relations = Array.Empty<object>(),
            };
            return JsonSerializer.Serialize(payload);
        }

        private static string? TryDecodePat(AuthenticationHeaderValue? header)
        {
            if (header is null || !string.Equals(header.Scheme, "Basic", StringComparison.Ordinal)
                || string.IsNullOrEmpty(header.Parameter)) return null;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
                return decoded.StartsWith(":", StringComparison.Ordinal) ? decoded[1..] : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
