using System.Net;
using System.Text;
using System.Text.Json;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Read-only GitHub issue adapter. Authentication and transport lifetime belong to the caller.
/// When <c>expectedRepository</c> is supplied, snapshots of issues in any other repository are refused.
/// Normal composition supplies an immutable target resolver instead; locator names are not authority.
/// </summary>
internal sealed class GitHubWorkItemProvider : IWorkItemSnapshotReader, IProviderSchemaMetadataReader
{
    private const string ProcessRulesUnsupportedReason = "provider-does-not-expose-process-rules";
    private const string FieldRulesUnsupportedReason = "provider-does-not-expose-field-rules";
    private const string FieldNotInSchemaReason = "field-not-in-schema";
    private const string TypeNotInSchemaReason = "type-not-in-schema";
    private const string RelationshipFieldsNotInSchemaReason = "relationship-fields-not-in-schema";
    private const string RelationshipNotSupportedReason = "relationship-not-supported-by-adapter";

    private static readonly string[] SchemaFieldNames =
    [
        "number", "title", "state", "body", "url", "issueType", "parent", "milestone", "repository",
        "labels", "assignees", "subIssues", "blockedBy", "blocking",
    ];

    private static readonly (LinkKind Kind, string[] Fields)[] SchemaRelationships =
    [
        (LinkKind.ParentChild, ["parent", "subIssues"]),
        (LinkKind.PredecessorSuccessor, ["blockedBy", "blocking"]),
        (LinkKind.Related, []),
    ];

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _scope;
    private readonly string? _expectedRepository;
    private readonly Func<CancellationToken, Task<GitHubBoundReadContext>>? _resolveTarget;
    private readonly Func<GitHubBoundReadContext, CancellationToken, Task>? _validateTarget;
    private IReadOnlyList<CapabilityAvailabilityEvidence> _lastReadEvidence = [];

    public GitHubWorkItemProvider(HttpClient http, Uri? graphQlEndpoint = null, string? expectedRepository = null,
        Func<CancellationToken, Task<GitHubBoundReadContext>>? resolveTarget = null,
        Func<GitHubBoundReadContext, CancellationToken, Task>? validateTarget = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if ((resolveTarget is null) != (validateTarget is null))
            throw new ArgumentException("A bound read requires both target capture and final validation.");
        _http = http;
        _endpoint = graphQlEndpoint ?? new Uri("https://api.github.com/graphql");
        if (!_endpoint.IsAbsoluteUri || _endpoint.Scheme != Uri.UriSchemeHttps
            || _endpoint.UserInfo.Length != 0 || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0
            || _endpoint.AbsolutePath is not ("/graphql" or "/api/graphql"))
            throw new ArgumentException("An HTTPS GitHub GraphQL endpoint without credentials, query or fragment is required.",
                nameof(graphQlEndpoint));
        _scope = _endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        _expectedRepository = expectedRepository;
        _resolveTarget = resolveTarget;
        _validateTarget = validateTarget;
    }

    public ProviderCapabilityDescriptor Capabilities => ProviderCapabilityCatalog.GitHub;

    public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(_scope);
    }

    /// <summary>
    /// Returns the last read's failure evidence, without initiating requests. A successful read
    /// does not establish mutation permission and leaves availability unprobed.
    /// </summary>
    public Task<IReadOnlyList<CapabilityAvailabilityEvidence>> ProbeCapabilitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_lastReadEvidence);
    }

    public async Task<ProviderSchemaMetadata> DiscoverSchemaMetadataAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var data = await ReadSchemaDataAsync(ct).ConfigureAwait(false);
            return ParseSchemaMetadata(data);
        }
        catch (HttpRequestException ex)
        {
            var denied = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Gone;
            return UnavailableSchemaMetadata(
                denied ? ProviderSchemaDiscoveryAvailability.Unavailable : ProviderSchemaDiscoveryAvailability.Unknown,
                denied ? "read-denied" : "read-failed");
        }
        catch (GitHubGraphQlReadException ex)
        {
            return UnavailableSchemaMetadata(
                ex.Denied ? ProviderSchemaDiscoveryAvailability.Unavailable : ProviderSchemaDiscoveryAvailability.Unknown,
                ex.Denied ? "read-denied" : "read-incomplete");
        }
        catch (GitHubCredentialUnavailableException)
        {
            return UnavailableSchemaMetadata(ProviderSchemaDiscoveryAvailability.Unavailable, "credential-unavailable");
        }
        catch (JsonException)
        {
            return UnavailableSchemaMetadata(ProviderSchemaDiscoveryAvailability.Unknown, "read-incomplete");
        }
        catch (IOException)
        {
            return UnavailableSchemaMetadata(ProviderSchemaDiscoveryAvailability.Unknown, "read-failed");
        }
    }

    public async Task<ProviderWorkItemSnapshot> FetchSnapshotAsync(
        ExternalWorkItemIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        if (identity.ProviderKind != Capabilities.ProviderKind || identity.ResourceScope != _scope)
            throw new ArgumentException("Identity must belong to this GitHub API origin.", nameof(identity));

        try
        {
            var target = _resolveTarget is null ? null : await _resolveTarget(ct).ConfigureAwait(false);
            var node = await ReadNodeAsync(identity, GitHubIssueQueries.Snapshot, null, ct);
            if (target is not null)
            {
                var repository = Required(node, "repository");
                var observed = new GitHubRepositoryTarget(
                    NativeId(Required(repository, "owner")), NativeId(repository));
                if (observed != target.Target)
                    throw new JsonException("GitHub repository or owner identity changed; explicit rediscovery and rebind are required.");
            }
            if (_expectedRepository is not null
                && !string.Equals(Text(Required(node, "repository"), "nameWithOwner"), _expectedRepository,
                    StringComparison.OrdinalIgnoreCase))
                throw new JsonException("GitHub issue is outside the configured repository.");
            var fields = FieldValues.Empty
                .Set("System.Title", new TextValue(Text(node, "title")))
                .Set("System.State", new TextValue(Text(node, "state")))
                .Set("github.number", new IntegerValue(IssueNumber(node)))
                .Set("github.url", new TextValue(Text(node, "url")));
            if (node.TryGetProperty("updatedAt", out _))
                fields = fields.Set("github.updatedAt", new TextValue(Text(node, "updatedAt")));
            var issueType = Required(node, "issueType");
            fields = fields.Set("System.WorkItemType", issueType.ValueKind == JsonValueKind.Null
                ? (FieldValue)NullValue.Instance : new TextValue(Text(issueType, "name")));
            var body = Required(node, "body");
            fields = fields.Set("System.Description",
                body.ValueKind == JsonValueKind.Null || Text(node, "body").Length == 0
                    ? (FieldValue)NullValue.Instance
                    : new RichTextValue(Text(node, "body"), RichTextFormat.Markdown));
            var milestone = Required(node, "milestone");
            fields = fields.Set("github.milestone", milestone.ValueKind == JsonValueKind.Null
                ? (FieldValue)NullValue.Instance : new TextValue(Text(milestone, "title")));
            if (milestone.ValueKind == JsonValueKind.Null)
                fields = fields.Set("github.milestoneId", NullValue.Instance);
            else if (milestone.TryGetProperty("id", out _))
                fields = fields.Set("github.milestoneId", new TextValue(NativeId(milestone)));

            var labels = await ReadConnectionAsync(identity, "labels", ct);
            var labelValues = new StringListValue(labels.Select(l => Text(l, "name")).Order(StringComparer.Ordinal));
            fields = fields.Set("github.labels", labelValues).Set("System.Tags", labelValues);
            if (labels.All(l => l.TryGetProperty("id", out _)))
                fields = fields.Set("github.labelIds", new StringListValue(labels.Select(NativeId).Order(StringComparer.Ordinal)));
            var assignees = (await ReadConnectionAsync(identity, "assignees", ct))
                .Select(a => new GitHubIssueAssignee(NativeId(a), Text(a, "login")))
                .OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
            fields = fields.Set("github.assignees", new RawValue(
                JsonSerializer.Serialize(assignees, TwigJsonContext.Default.GitHubIssueAssigneeArray),
                "github.assignees.v1"));
            fields = fields.Set("github.assigneeIds", new StringListValue(assignees.Select(a => a.Id)));
            if (assignees.Length == 0)
                fields = fields.Set("System.AssignedTo", NullValue.Instance);
            else if (assignees.Length == 1)
                fields = fields.Set("System.AssignedTo", new IdentityValue(assignees[0].Id, assignees[0].Login));

            var relationships = new HashSet<ProviderWorkItemRelationship>();
            var parent = Required(node, "parent");
            if (parent.ValueKind != JsonValueKind.Null)
                AddEdge(relationships, External(parent), identity, LinkKind.ParentChild);
            foreach (var child in await ReadConnectionAsync(identity, "subIssues", ct))
                AddEdge(relationships, identity, External(child), LinkKind.ParentChild);
            foreach (var blocker in await ReadConnectionAsync(identity, "blockedBy", ct))
                AddEdge(relationships, External(blocker), identity, LinkKind.PredecessorSuccessor);
            foreach (var blocked in await ReadConnectionAsync(identity, "blocking", ct))
                AddEdge(relationships, identity, External(blocked), LinkKind.PredecessorSuccessor);

            if (target is not null)
                await _validateTarget!(target, ct).ConfigureAwait(false);
            _lastReadEvidence = [];
            return new(identity, GitHubObservationToken.Create(identity, fields, relationships),
                fields, relationships.ToArray(), RelationshipsComplete: true);
        }
        catch (HttpRequestException ex)
        {
            var denied = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Gone;
            RecordFailure(denied ? CapabilityAvailability.Unavailable : CapabilityAvailability.Unknown,
                denied ? "read-denied" : "read-failed");
            throw;
        }
        catch (GitHubGraphQlReadException ex)
        {
            RecordFailure(ex.Denied ? CapabilityAvailability.Unavailable : CapabilityAvailability.Unknown,
                ex.Denied ? "read-denied" : "read-incomplete");
            throw;
        }
        catch (JsonException)
        {
            RecordFailure(CapabilityAvailability.Unknown, "read-incomplete");
            throw;
        }
        catch (GitHubCredentialUnavailableException)
        {
            RecordFailure(CapabilityAvailability.Unavailable, "credential-unavailable");
            throw;
        }
    }

    private void RecordFailure(CapabilityAvailability availability, string reason)
        => _lastReadEvidence =
        [
            new(LinkKind.ParentChild, availability, reason),
            new(LinkKind.PredecessorSuccessor, availability, reason),
        ];

    private ExternalWorkItemIdentity External(JsonElement node) => new("github", _scope, NativeId(node));

    private static string NativeId(JsonElement node)
    {
        var id = Text(node, "id");
        return !string.IsNullOrWhiteSpace(id) && string.Equals(id, id.Trim(), StringComparison.Ordinal)
            ? id : throw new JsonException("GitHub returned an invalid native identity.");
    }

    private static long IssueNumber(JsonElement node)
    {
        var number = Required(node, "number");
        return number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var value) && value > 0
            ? value : throw new JsonException("GitHub returned an invalid issue number.");
    }

    private static void AddEdge(HashSet<ProviderWorkItemRelationship> edges,
        ExternalWorkItemIdentity source, ExternalWorkItemIdentity target, LinkKind kind)
    {
        if (source == target)
            throw new JsonException("GitHub returned a self relationship.");
        edges.Add(new(source, target, kind));
    }

    private async Task<IReadOnlyList<JsonElement>> ReadConnectionAsync(
        ExternalWorkItemIdentity identity, string field, CancellationToken ct)
    {
        var query = GitHubIssueQueries.Connection(field);
        var nodes = new List<JsonElement>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var node = await ReadNodeAsync(identity, query, cursor, ct);
            var connection = Required(node, field);
            var pageNodes = Required(connection, "nodes");
            if (pageNodes.ValueKind != JsonValueKind.Array)
                throw new JsonException("GitHub returned an incomplete connection.");
            foreach (var item in pageNodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new JsonException("GitHub returned an incomplete connection node.");
                nodes.Add(item);
            }
            var pageInfo = Required(connection, "pageInfo");
            var hasNext = Required(pageInfo, "hasNextPage");
            if (hasNext.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("GitHub returned invalid pagination information.");
            if (!hasNext.GetBoolean())
                break;
            cursor = Text(pageInfo, "endCursor");
            if (cursor.Length == 0 || !seenCursors.Add(cursor))
                throw new JsonException("GitHub pagination did not advance.");
        } while (true);
        return nodes;
    }

    private async Task<JsonElement> ReadNodeAsync(
        ExternalWorkItemIdentity identity, string query, string? cursor, CancellationToken ct)
    {
        var variables = new Dictionary<string, string?> { ["id"] = identity.NativeId };
        if (GitHubIssueQueries.TryMatch(query, out var paginated) && paginated)
            variables["after"] = cursor;
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Content = new StringContent(JsonSerializer.Serialize(
            new GitHubIssueGraphQlRequest(query, variables), TwigJsonContext.Default.GitHubIssueGraphQlRequest),
            Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.GitHubIssueGraphQlResponse, ct)
            ?? throw new JsonException("GitHub returned no response.");
        EnsureNoGraphQlErrors(result.Errors);
        var node = Required(result.Data, "node");
        if (Text(node, "__typename") != "Issue" || Text(node, "id") != identity.NativeId)
            throw new JsonException("GitHub did not return the requested issue identity.");
        return node;
    }

    private async Task<JsonElement> ReadSchemaDataAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Content = new StringContent(JsonSerializer.Serialize(
            new GitHubIssueGraphQlRequest(GitHubIssueQueries.SchemaMetadata, new Dictionary<string, string?>()),
            TwigJsonContext.Default.GitHubIssueGraphQlRequest), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync(
            stream, TwigJsonContext.Default.GitHubIssueGraphQlResponse, ct).ConfigureAwait(false)
            ?? throw new JsonException("GitHub returned no schema metadata response.");
        EnsureNoGraphQlErrors(result.Errors);
        if (result.Data.ValueKind != JsonValueKind.Object)
            throw new JsonException("GitHub returned incomplete schema metadata.");
        return result.Data;
    }

    private ProviderSchemaMetadata ParseSchemaMetadata(JsonElement data)
    {
        var issueType = Required(data, "issue");
        var issueSupported = issueType.ValueKind == JsonValueKind.Object
            && Text(issueType, "kind") == "OBJECT"
            && Text(issueType, "name") == "Issue";
        var issueFields = issueSupported ? ReadIssueFields(issueType) : new Dictionary<string, string>(StringComparer.Ordinal);
        var issueReason = issueSupported ? null : TypeNotInSchemaReason;

        var fields = SchemaFieldNames.Select(name =>
        {
            if (issueFields.TryGetValue(name, out var typeName))
                return new ProviderSchemaFieldMetadata(name, typeName, ProviderSchemaElementSupport.Supported, null);
            var reason = issueSupported ? FieldNotInSchemaReason : issueReason;
            return new ProviderSchemaFieldMetadata(name, null, ProviderSchemaElementSupport.Unsupported, reason);
        }).ToArray();

        var rawStates = Required(data, "issueState");
        var stateSupport = rawStates.ValueKind == JsonValueKind.Object
            && Text(rawStates, "kind") == "ENUM"
            && Text(rawStates, "name") == "IssueState";
        var stateValues = stateSupport ? ReadEnumValues(rawStates) : [];
        var stateReason = stateSupport
            ? stateValues.Count == 0 ? "state-values-empty" : null
            : TypeNotInSchemaReason;
        if (stateValues.Count == 0)
            stateSupport = false;

        var relationships = SchemaRelationships.Select(relation =>
        {
            var support = relation.Kind == LinkKind.Related
                ? ProviderSchemaElementSupport.Unsupported
                : !issueSupported || relation.Fields.Any(field => !issueFields.ContainsKey(field))
                    ? ProviderSchemaElementSupport.Unsupported
                    : ProviderSchemaElementSupport.Supported;
            var reason = relation.Kind == LinkKind.Related
                ? RelationshipNotSupportedReason
                : !issueSupported
                    ? TypeNotInSchemaReason
                    : support == ProviderSchemaElementSupport.Unsupported
                        ? RelationshipFieldsNotInSchemaReason
                        : null;
            return new ProviderSchemaRelationshipMetadata(relation.Kind, support, relation.Fields, reason);
        }).ToArray();

        return new ProviderSchemaMetadata(
            "github",
            ProviderSchemaDiscoveryAvailability.Available,
            null,
            [new ProviderSchemaTypeMetadata("Issue", issueSupported
                ? ProviderSchemaElementSupport.Supported : ProviderSchemaElementSupport.Unsupported, issueReason)],
            fields,
            new ProviderSchemaStateMetadata(
                "IssueState",
                stateSupport ? ProviderSchemaElementSupport.Supported : ProviderSchemaElementSupport.Unsupported,
                stateValues,
                stateSupport ? null : stateReason),
            relationships,
            UnsupportedRules());
    }

    private ProviderSchemaMetadata UnavailableSchemaMetadata(
        ProviderSchemaDiscoveryAvailability availability,
        string reasonCode)
    {
        return new ProviderSchemaMetadata(
            "github",
            availability,
            reasonCode,
            [new ProviderSchemaTypeMetadata("Issue", ProviderSchemaElementSupport.Unknown, reasonCode)],
            SchemaFieldNames.Select(name =>
                new ProviderSchemaFieldMetadata(name, null, ProviderSchemaElementSupport.Unknown, reasonCode)).ToArray(),
            new ProviderSchemaStateMetadata(
                "IssueState", ProviderSchemaElementSupport.Unknown, [], reasonCode),
            SchemaRelationships.Select(relation =>
                new ProviderSchemaRelationshipMetadata(
                    relation.Kind, ProviderSchemaElementSupport.Unknown, relation.Fields, reasonCode)).ToArray(),
            UnsupportedRules());
    }

    private static ProviderSchemaRuleSupport UnsupportedRules() =>
        new(ProviderSchemaElementSupport.Unsupported, ProcessRulesUnsupportedReason,
            ProviderSchemaElementSupport.Unsupported, FieldRulesUnsupportedReason);

    private static Dictionary<string, string> ReadIssueFields(JsonElement issueType)
    {
        var rawFields = Required(issueType, "fields");
        if (rawFields.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub returned invalid issue schema fields.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in rawFields.EnumerateArray())
        {
            var name = Text(field, "name");
            if (string.IsNullOrWhiteSpace(name) || !fields.TryAdd(name, TypeSignature(Required(field, "type"))))
                throw new JsonException("GitHub returned duplicate or invalid issue schema fields.");
        }

        return fields;
    }

    private static IReadOnlyList<string> ReadEnumValues(JsonElement enumType)
    {
        var rawValues = Required(enumType, "enumValues");
        if (rawValues.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub returned invalid issue-state values.");
        return rawValues.EnumerateArray()
            .Select(value => Text(value, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string TypeSignature(JsonElement type)
    {
        var kind = Text(type, "kind");
        if (kind is "NON_NULL" or "LIST")
        {
            var inner = TypeSignature(Required(type, "ofType"));
            return kind == "NON_NULL" ? inner + "!" : $"[{inner}]";
        }

        var name = Text(type, "name");
        return !string.IsNullOrWhiteSpace(name)
            ? name
            : throw new JsonException("GitHub returned an unnamed issue schema type.");
    }

    private static void EnsureNoGraphQlErrors(JsonElement errors)
    {
        if (errors.ValueKind == JsonValueKind.Undefined)
            return;
        if (errors.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub returned invalid GraphQL errors.");
        if (errors.GetArrayLength() == 0)
            return;

        var denied = errors.EnumerateArray().Any(error =>
            error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() is "FORBIDDEN" or "NOT_FOUND" or "UNAUTHORIZED");
        throw new GitHubGraphQlReadException(denied);
    }

    private static JsonElement Required(JsonElement node, string property)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value)
            ? value : throw new JsonException($"GitHub response is missing '{property}'.");

    private static string Text(JsonElement node, string property)
    {
        var value = Required(node, property);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new JsonException($"GitHub response has invalid '{property}'.");
    }

    private sealed class GitHubGraphQlReadException(bool denied)
        : JsonException("GitHub could not complete the issue read; no snapshot was returned.")
    {
        public bool Denied { get; } = denied;
    }
}
