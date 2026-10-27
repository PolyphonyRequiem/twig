using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Services.ReferenceProfile;

namespace Twig.Infrastructure.Tests.Plan;

internal sealed class PolicyFreshnessFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-policy-freshness-{Guid.NewGuid():N}");
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private readonly HttpClient _http;
    private readonly TwigPaths _paths;
    private readonly SqliteWorkItemRepository _items;
    private readonly SqliteWorkItemLinkRepository _links;
    private readonly AdoRestClient _ado;
    private readonly AdoIterationService _process;
    private readonly AttachedOrigin _origin;
    private readonly Action<string>? _diagnostic;
    private int _ambientFactoryCalls;

    public PolicyFreshnessTransport Native { get; } = new();
    public SqlitePlanJournalRepository Journal { get; }
    public PlanOrigin Origin { get; }
    public bool ExpectedPreviewCanApply { get; set; } = true;
    public string PlanPath => Path.Combine(_root, "proposal.json");
    public string PolicyPath => _paths.PolicyPath;

    public PolicyFreshnessFixture(Action<string>? diagnostic = null)
    {
        _diagnostic = diagnostic;
        Directory.CreateDirectory(Path.Combine(_root, ".twig"));
        _paths = new TwigPaths(Path.Combine(_root, ".twig"),
            Path.Combine(_root, ".twig", "config"), Path.Combine(_root, ".twig", "twig.db"), _root,
            Path.Combine(_root, "display.json"));
        var config = new TwigConfiguration { Organization = "acme", Project = "cache" };
        var mapper = new WorkItemMapper();
        _items = new SqliteWorkItemRepository(_store, mapper);
        _links = new SqliteWorkItemLinkRepository(_store);
        Journal = new SqlitePlanJournalRepository(_store);
        _http = new HttpClient(Native) { Timeout = TimeSpan.FromSeconds(5) };
        var credentials = new FixtureCredential(Native);
        _ado = new AdoRestClient(_http, credentials, "https://dev.azure.com/acme", "cache", mapper);
        _process = new AdoIterationService(_http, credentials, "https://dev.azure.com/acme", "cache");
        var services = new ServiceCollection();
        services.AddConnectionServices(config, _paths.TwigDir, _root);
        var defaults = services.ToArray();
        // Remove every default descriptor, including independently registered system/binding
        // factories. Last-wins store overrides do not protect IEnumerable or indirect resolution.
        services.Clear();
        var pending = new SqlitePendingChangeStore(_store);
        var fields = new SqliteFieldDefinitionStore(_store);
        var seeds = new SqliteSeedLinkRepository(_store);
        var registry = new SqliteStagedIdentityRegistry(_store);
        var map = new SqlitePublishIdMapRepository(_store, registry);
        var intents = new SqlitePublishIntentRepository(_store);
        var unitOfWork = new SqliteUnitOfWork(_store);
        var rules = new FileSeedPublishRulesProvider(_paths.TwigDir);
        var profile = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(config));
        var publisher = new SeedPublishOrchestrator(
            _items, _ado, seeds, _links, map, rules, unitOfWork, new BacklogOrderer(_ado, fields),
            pending, intents, new SprintEntryPolicy(profile));
        services.AddSingleton(_store);
        services.AddSingleton(config);
        services.AddSingleton(_paths);
        services.AddSingleton<IWorkItemRepository>(_items);
        services.AddSingleton<IWorkItemLinkRepository>(_links);
        services.AddSingleton<IAdoWorkItemService>(_ado);
        services.AddSingleton<IPendingChangeReader>(pending);
        services.AddSingleton<IPendingChangeStore>(pending);
        services.AddSingleton<IFieldDefinitionStore>(fields);
        services.AddSingleton<ISeedLinkRepository>(seeds);
        services.AddSingleton<IStagedIdentityRegistry>(registry);
        services.AddSingleton<IPublishIdMapRepository>(map);
        services.AddSingleton<IPublishIntentRepository>(intents);
        services.AddSingleton<IUnitOfWork>(unitOfWork);
        services.AddSingleton(publisher);
        services.ShouldAllBe(descriptor => descriptor.ImplementationInstance != null);
        services.AddSingleton<ISystemWorktreeRegistry>(_ => ForbiddenAmbient<ISystemWorktreeRegistry>());
        services.AddSingleton<IAuthenticationProvider>(_ => ForbiddenAmbient<IAuthenticationProvider>());
        services.AddSingleton<Twig.Infrastructure.Auth.IConnectionBindingService>(
            _ => ForbiddenAmbient<Twig.Infrastructure.Auth.IConnectionBindingService>());
        services.ShouldAllBe(descriptor => !defaults.Contains(descriptor));
        _services = services.BuildServiceProvider();
        _services.GetRequiredService<SqliteCacheStore>().ShouldBeSameAs(_store);
        _services.GetRequiredService<IStagedIdentityRegistry>().ShouldBeSameAs(registry);
        _services.GetRequiredService<SeedPublishOrchestrator>().ShouldBeSameAs(publisher);
        _ambientFactoryCalls.ShouldBe(0);
        Origin = new PlanOrigin(_root, "freshness-fixture", 1, "fixture-connection", "fixture-binding", 1,
            "fixture-selection", 1, "fixture-identity", "aad", "fixture-credential", "fixture-tenant",
            "fixture-object", "fixture-issuer", "fixture-authority", null);
        _origin = new AttachedOrigin(Origin);
    }

    public PlanLifecycleService Service() => new(
        new PlanDocumentParser(), Journal,
        _services.GetRequiredService<IPendingChangeReader>(),
        _services.GetRequiredService<IFieldDefinitionStore>(),
        new AdoProviderWorkItemWriter(_ado), AdoProviderRevisionCodec.Instance, _ado,
        _services.GetRequiredService<SeedPublishOrchestrator>(), _items,
        _services.GetRequiredService<ISeedLinkRepository>(),
        _services.GetRequiredService<IStagedIdentityRegistry>(),
        _services.GetRequiredService<IPublishIdMapRepository>(),
        _services.GetRequiredService<IPublishIntentRepository>(),
        _services.GetRequiredService<TwigConfiguration>(), _paths, TimeProvider.System,
        new UnresolvedSessionSteeringModeProvider(), _origin, _process,
        _services.GetRequiredService<IUnitOfWork>(), linkRepo: _links,
        systemRegistryPath: Path.Combine(_root, "system.db"));

    public async Task InitializeAsync()
    {
        for (var id = 1; id <= 3; id++)
        {
            Native.Put(id, id == 2 ? "AllowedParent" : "Child");
            await CacheAsync(id, id == 2 ? "AllowedParent" : "Child");
        }
        var (items, links) = await _ado.FetchBatchWithLinksAsync([1, 2, 3]);
        items.Select(item => item.Id).Order().ShouldBe([1, 2, 3]);
        links.ShouldBeEmpty();
        (await _ado.FetchAtRevisionAsync(1, 1)).Revision.ShouldBe(1);
        (await _process.GetRulesAsync("Child")).ShouldBeEmpty();
        Native.Reads.ShouldContain(uri => uri.Contains("/rules?", StringComparison.OrdinalIgnoreCase));
        Native.Mutations.ShouldBeEmpty();
    }

    public async Task CacheAsync(int id, string type)
    {
        // Deliberately independent cache facts are the point of the stale-mirror cases.
        var item = new WorkItem
        {
            Id = id,
            Title = $"item-{id}",
            Type = Twig.Domain.ValueObjects.WorkItemType.Parse(type).Value,
        };
        item.ChangeState("Open");
        item.MarkSynced(1);
        await _items.SaveAsync(item);
        await _links.SaveLinksAsync(id, []);
        (await _items.GetByIdAsync(id)).ShouldNotBeNull().Type.Value.ShouldBe(type);
        (await _links.GetLinksVerifiedAtAsync(id)).ShouldNotBeNull();
    }

    public async Task AssertOrdinaryProjectionStillReadsAsync(int id)
    {
        var (items, _) = await _ado.FetchBatchWithLinksAsync([id]);
        items.ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    public async Task<string> PreviewAsync(params string[] operations)
    {
        await File.WriteAllTextAsync(PlanPath, """
            { "version": 1, "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
            """ + string.Join(",", operations) + "]}");
        var preview = await Service().PreviewAsync(PlanPath);
        preview.Digest.ShouldNotBeNull();
        preview.Issues.ShouldBeEmpty();
        preview.CanApply.ShouldBe(ExpectedPreviewCanApply, "The cached preview precondition must be reached.");
        var row = (await Journal.GetAsync(preview.Digest!)).ShouldNotBeNull();
        row.Origin.ShouldBe(Origin);
        row.State.ShouldBe(PlanOperationState.Planned);
        row.ExecutionAdmission.ShouldBeNull();
        return preview.Digest!;
    }

    public async Task<PlanApplyResult> ApplyAsync(string digest)
    {
        var row = (await Journal.GetAsync(digest)).ShouldNotBeNull();
        var currentOrigin = await _origin.GetOriginAsync();
        currentOrigin.ShouldBe(Origin);
        row.Origin.ShouldBe(currentOrigin);
        currentOrigin.WorktreeRoot.ShouldBe(_root);
        row.Origin.ShouldNotBeNull().WorktreeFingerprint.ShouldBe(currentOrigin.WorktreeFingerprint);
        _paths.RepoRoot.ShouldBe(_root);
        _paths.StartDir.ShouldBe(_root);
        Path.GetDirectoryName(PlanPath).ShouldBe(_paths.RepoRoot);
        new PlanDocumentParser().Parse(await File.ReadAllTextAsync(PlanPath)).Digest.ShouldBe(digest);
        using var document = System.Text.Json.JsonDocument.Parse(row.CanonicalJson);
        foreach (var operation in document.RootElement.GetProperty("operations").EnumerateArray())
        {
            var operationId = operation.GetProperty("id").GetString();
            var state = row.Operations.Single(candidate => candidate.OpId == operationId).State;
            if (state is PlanOperationState.Planned or PlanOperationState.Confirmed)
                Native.Revision(operation.GetProperty("workItemId").GetInt32())
                    .ShouldBe(operation.GetProperty("expectedRevision").GetInt32());
        }
        (await _services.GetRequiredService<IPendingChangeReader>().GetAllChangesAsync()).ShouldBeEmpty();
        ProposalAuthorizationGate.Evaluate(Authorization(digest), digest, SessionSteeringMode.Unresolved)
            .Authorized.ShouldBeTrue();
        _ambientFactoryCalls.ShouldBe(0);
        var result = await Service().ApplyAsync(PlanPath, digest, Authorization(digest));
        Native.AssertWriteAdmissionsCompleted();
        _diagnostic?.Invoke($"Apply digest={digest}; Failed={result.Failed}; TopError={result.Error ?? "<NULL>"}");
        foreach (var operation in result.Operations)
            _diagnostic?.Invoke($"Result operation={operation.OpId}; State={operation.State}; Error={operation.Error ?? "<NULL>"}; Record={operation}");
        var observed = await Journal.GetAsync(digest);
        _diagnostic?.Invoke($"Journal state={observed?.State}; Error={observed?.Error ?? "<NULL>"}");
        if (observed is not null)
            foreach (var operation in observed.Operations)
                _diagnostic?.Invoke($"Journal operation={operation.OpId}; State={operation.State}; Error={operation.Error ?? "<NULL>"}; Record={operation}");
        foreach (var read in Native.Reads)
            _diagnostic?.Invoke($"NativeRead={read}");
        _diagnostic?.Invoke($"NativeMutations=[{string.Join(",", Native.Mutations)}]");
        _ambientFactoryCalls.ShouldBe(0);
        return result;
    }

    public static ProposalAuthorization Authorization(string digest) => new()
    {
        Digest = digest,
        Mode = ProposalAuthorizationMode.Human,
        AuthorizerIdentity = "Fixture legacy authorizer",
        Rationale = "Exercise current policy, not reusable permission.",
        AuthorizedAt = DateTimeOffset.UnixEpoch,
    };

    public async Task PauseAsync(string digest, int itemId, int completed = 0)
    {
        Native.UnavailableRevisionItem = itemId;
        var result = await ApplyAsync(digest);
        Native.UnavailableRevisionItem = null;
        result.Error.ShouldNotBeNull().ShouldContain("snapshot");
        var row = (await Journal.GetAsync(digest)).ShouldNotBeNull();
        row.State.ShouldBe(PlanOperationState.Confirmed);
        row.ConfirmedAt.ShouldNotBeNull();
        row.Operations.Take(completed).ShouldAllBe(op => op.State == PlanOperationState.Verified);
        row.Operations.Skip(completed).ShouldAllBe(op => op.State == PlanOperationState.Confirmed);
        row.AuthorizationMode.ShouldBe(ProposalAuthorizationMode.Human);
        row.AuthorizerIdentity.ShouldBe("Fixture legacy authorizer");
        row.ReviewModelJson.ShouldNotBeNull();
        row.ExecutionAdmission.ShouldBeNull();
        row.Origin.ShouldBe(Origin);
        ProposalAuthorizationGate.Evaluate(Authorization(digest), digest, SessionSteeringMode.Unresolved)
            .Authorized.ShouldBeTrue();
        Native.Mutations.Count.ShouldBe(completed);
    }

    public string History(string digest)
    {
        using var command = _store.GetConnection().CreateCommand();
        command.CommandText = """
            SELECT canonical_json, origin_json, authorization_mode, authorizer_identity,
                   rationale, review_model_json, authorized_at, execution_admission_json
            FROM pending.proposal_journals WHERE digest = @digest;
            """;
        command.Parameters.AddWithValue("@digest", digest);
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        return string.Join("\n", Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? "<NULL>" : reader.GetString(index)));
    }

    public async Task AssertHistoryAsync(string digest, string before)
    {
        History(digest).ShouldBe(before);
        var row = (await Journal.GetAsync(digest)).ShouldNotBeNull();
        row.Digest.ShouldBe(digest);
        row.Origin.ShouldBe(Origin);
        row.ExecutionAdmission.ShouldBeNull();
    }

    public void Labels(bool allow = true) => File.WriteAllText(PolicyPath, $$"""
        { "version": 1, "rules": [
          { "id": "labels", "kind": "label-applicability", "prefix": "scope:",
            "allowedTypes": ["{{(allow ? "Child" : "Other")}}"] },
          { "id": "dependencies", "kind": "dependency-integrity", "maxNodes": 8 }
        ] }
        """);

    public void Parent(string childType = "Child") => File.WriteAllText(PolicyPath, $$"""
        { "version": 1, "rules": [
          { "id": "parents", "kind": "parent-type", "childType": "{{childType}}",
            "allowedParentTypes": ["AllowedParent"] }
        ] }
        """);

    public static string Batch(string id, int item) => $$"""
        { "id": "{{id}}", "kind": "batch", "workItemId": {{item}}, "expectedRevision": 1,
          "fields": { "System.Tags": "scope:x" } }
        """;

    public static string Link(string relation = "successor") => $$"""
        { "id": "edge", "kind": "add-link", "workItemId": 1, "expectedRevision": 1,
          "relation": "{{relation}}", "otherId": 2 }
        """;

    public void Dispose()
    {
        _services.Dispose();
        _store.Dispose();
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
        _ambientFactoryCalls.ShouldBe(0, "No ambient factory may run, including during fixture cleanup.");
    }

    private T ForbiddenAmbient<T>()
    {
        _ambientFactoryCalls++;
        throw new InvalidOperationException($"Fixture refuses ambient service resolution: {typeof(T).FullName}");
    }

    private sealed class AttachedOrigin(PlanOrigin origin) : IPlanOriginProvider
    {
        public Task<PlanOrigin> GetOriginAsync(CancellationToken ct = default) => Task.FromResult(origin);
    }

    private sealed class FixtureCredential(PolicyFreshnessTransport native)
        : IAuthenticationProvider, IConnectionRemoteWriteGuard
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("fixture-not-a-credential");
        public void InvalidateToken() { }

        public Task<IConnectionRemoteWriteAdmission> BeginRemoteWriteAsync(
            ConnectionRemoteWriteRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(native.AdmitWrite(request));
        }
    }
}

internal sealed class PolicyFreshnessTransport : HttpMessageHandler
{
    private readonly Dictionary<int, JsonObject> _items = [];
    private readonly Dictionary<int, JsonObject> _revisionOne = [];
    public List<int> Mutations { get; } = [];
    public List<string> Reads { get; } = [];
    public int? UnavailableRevisionItem { get; set; }
    public string? NativeFailure { get; set; }
    public Action<int>? AfterMutation { get; set; }
    private readonly List<FixtureWriteAdmission> _admissions = [];

    public IConnectionRemoteWriteAdmission AdmitWrite(ConnectionRemoteWriteRequest request)
    {
        request.Method.ShouldBe("PATCH");
        var target = new Uri(request.Target);
        target.Scheme.ShouldBe("https");
        target.Host.ShouldBe("dev.azure.com");
        target.Port.ShouldBe(443);
        var match = Regex.Match(target.AbsolutePath, @"^/acme/cache/_apis/wit/workitems/([13])$");
        match.Success.ShouldBeTrue("Only owned fixture items may be mutated.");
        var id = int.Parse(match.Groups[1].Value);
        var patch = JsonNode.Parse(request.Payload)!.AsArray();
        patch[0]!["op"]!.GetValue<string>().ShouldBe("test");
        patch[0]!["path"]!.GetValue<string>().ShouldBe("/rev");
        patch[0]!["value"]!.GetValue<int>().ShouldBe(Revision(id));
        request.IfMatch.ShouldBe(Revision(id).ToString(System.Globalization.CultureInfo.InvariantCulture));
        patch.Count.ShouldBeGreaterThan(1);
        var relation = patch[1]!["path"]!.GetValue<string>() == "/relations/-";
        request.EffectKind.ShouldBe(relation ? "link-add" : "workitem-patch");
        request.SeedCorrelation.ShouldBeNull();
        var admission = new FixtureWriteAdmission(request);
        _admissions.Add(admission);
        return admission;
    }

    public void AssertWriteAdmissionsCompleted()
    {
        foreach (var admission in _admissions)
        {
            admission.TransportAttempts.ShouldBe(1);
            admission.ResponseRecords.ShouldBe(1);
        }
    }

    private sealed class FixtureWriteAdmission(ConnectionRemoteWriteRequest request)
        : IConnectionRemoteWriteAdmission
    {
        public ConnectionRemoteWriteRequest Request { get; } = request;
        public int TransportAttempts { get; set; }
        public int ResponseRecords { get; private set; }
        public int? TransportStatus { get; set; }
        public bool IsDefinitivelyRejected { get; private set; }

        public Task RecordResponseAsync(ConnectionRemoteWriteResponse response, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TransportAttempts.ShouldBe(1);
            ResponseRecords.ShouldBe(0);
            response.StatusCode.ShouldBe(TransportStatus.ShouldNotBeNull());
            ResponseRecords++;
            IsDefinitivelyRejected = response.StatusCode is 401 or 403;
            return Task.CompletedTask;
        }
    }

    public void Put(int id, string type)
    {
        _items[id] = new JsonObject
        {
            ["id"] = id, ["rev"] = 1,
            ["fields"] = new JsonObject
            {
                ["System.Id"] = id, ["System.Rev"] = 1, ["System.Title"] = $"item-{id}",
                ["System.WorkItemType"] = type, ["System.State"] = "Open",
                ["System.TeamProject"] = "cache", ["System.Tags"] = "",
            },
            ["relations"] = new JsonArray(),
        };
        _revisionOne[id] = (JsonObject)_items[id].DeepClone();
    }

    public string Type(int id) => _items[id]["fields"]!["System.WorkItemType"]!.GetValue<string>();
    public int Revision(int id) => _items[id]["rev"]!.GetValue<int>();
    public void Retype(int id, string type)
    {
        _items[id]["fields"]!["System.WorkItemType"] = type;
        Advance(id);
    }
    public void Tags(int id, string value) => _items[id]["fields"]!["System.Tags"] = value;
    public void Advance(int id)
    {
        _items[id]["rev"] = Revision(id) + 1;
        _items[id]["fields"]!["System.Rev"] = Revision(id);
    }
    public void Edge(int source, int target) => Relations(source).Add(new JsonObject
    {
        ["rel"] = "System.LinkTypes.Dependency-Forward",
        ["url"] = $"https://dev.azure.com/acme/_apis/wit/workItems/{target}",
    });
    public void PolicyRelationships(int id, string evidence, bool parent = false)
    {
        if (evidence == "missing")
        {
            _items[id].Remove("relations");
            return;
        }
        if (evidence == "null-array")
        {
            _items[id]["relations"] = null;
            return;
        }
        _items[id]["relations"] = new JsonArray();
        if (evidence == "empty")
            return;
        var url = evidence switch
        {
            "null-url" => null,
            "malformed" => "https://dev.azure.com/acme/_apis/wit/workItems/not-an-id",
            "foreign-host" => "https://other.invalid/acme/_apis/wit/workItems/2",
            "foreign-org" => "https://dev.azure.com/another/_apis/wit/workItems/2",
            "foreign-project" => "https://dev.azure.com/acme/another/_apis/wit/workItems/2",
            "query" => "https://dev.azure.com/acme/_apis/wit/workItems/2?other=1",
            "valid" => "https://dev.azure.com/acme/_apis/wit/workItems/3",
            _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
        };
        Relations(id).Add(new JsonObject
        {
            ["rel"] = parent ? "System.LinkTypes.Hierarchy-Reverse" : "System.LinkTypes.Dependency-Forward",
            ["url"] = url,
        });
    }
    private JsonArray Relations(int id) => (JsonArray)_items[id]["relations"]!;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!.AbsoluteUri;
        var path = request.RequestUri.AbsolutePath.ToLowerInvariant();
        if (request.Method == HttpMethod.Get)
        {
            Reads.Add(uri);
            if (path.Contains("/_apis/projects/", StringComparison.Ordinal))
                return Json("""{"capabilities":{"processTemplate":{"templateTypeId":"fixture-process"}}}""");
            if (path.EndsWith("/workitemtypes", StringComparison.Ordinal))
                return Json("""{"value":[{"name":"Child","referenceName":"Fixture.Child"},{"name":"AllowedParent","referenceName":"Fixture.Parent"}]}""");
            if (path.EndsWith("/rules", StringComparison.Ordinal))
                return Json("""{"value":[]}""");
            if (path.EndsWith("/workitems", StringComparison.Ordinal))
            {
                if (NativeFailure == "denied") return Json("{}", HttpStatusCode.Forbidden);
                var idsMatch = Regex.Match(request.RequestUri.Query, @"(?:\?|&)ids=([^&]+)");
                idsMatch.Success.ShouldBeTrue("Unexpected unbounded native batch request.");
                var ids = Uri.UnescapeDataString(idsMatch.Groups[1].Value).Split(',').Select(int.Parse);
                var value = new JsonArray();
                foreach (var id in ids)
                    if (!(NativeFailure is "incomplete" or "missing" && id == 2))
                        value.Add(_items[id].DeepClone());
                return Json(new JsonObject { ["count"] = value.Count, ["value"] = value }.ToJsonString());
            }
            var match = Regex.Match(path, @"/workitems/(\d+)(?:/revisions/(\d+))?$");
            match.Success.ShouldBeTrue($"Unexpected GET {path}");
            var itemId = int.Parse(match.Groups[1].Value);
            if (match.Groups[2].Success)
            {
                if (UnavailableRevisionItem == itemId) return Json("{}", HttpStatusCode.Forbidden);
                int.Parse(match.Groups[2].Value).ShouldBe(1);
                return Json(_revisionOne[itemId].ToJsonString());
            }
            if (NativeFailure == "denied") return Json("{}", HttpStatusCode.Forbidden);
            if (NativeFailure == "missing" && itemId == 2) return Json("{}", HttpStatusCode.NotFound);
            if (NativeFailure == "incomplete" && itemId == 2) return Json(_items[1].ToJsonString());
            return Json(_items[itemId].ToJsonString());
        }

        request.Method.ShouldBe(HttpMethod.Patch, "Only the reviewed revision-bound PATCH is allowed.");
        var target = int.Parse(Regex.Match(path, @"/workitems/(\d+)$").Groups[1].Value);
        var patch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsArray();
        var admission = _admissions.Single(candidate => candidate.TransportAttempts == 0);
        admission.Request.Target.ShouldBe(uri);
        admission.Request.Method.ShouldBe(request.Method.Method);
        admission.Request.Payload.ShouldBe(await request.Content.ReadAsStringAsync(ct));
        request.Headers.GetValues("If-Match").Single().ShouldBe(admission.Request.IfMatch);
        admission.TransportAttempts++;
        patch[0]!["op"]!.GetValue<string>().ShouldBe("test");
        patch[0]!["path"]!.GetValue<string>().ShouldBe("/rev");
        patch[0]!["value"]!.GetValue<int>().ShouldBe(Revision(target), "Native CAS must not be bypassed.");
        foreach (var operation in patch.Skip(1))
        {
            var location = operation!["path"]!.GetValue<string>();
            if (location == "/relations/-")
                Relations(target).Add(operation["value"]!.DeepClone());
            else
            {
                location.ShouldStartWith("/fields/");
                _items[target]["fields"]![location["/fields/".Length..]] = operation["value"]?.DeepClone();
            }
        }
        Advance(target);
        Mutations.Add(target);
        AfterMutation?.Invoke(target);
        admission.TransportStatus = (int)HttpStatusCode.OK;
        return Json(_items[target].ToJsonString());
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
