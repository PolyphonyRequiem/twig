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
/// </summary>
internal sealed class GitHubWorkItemProvider : IWorkItemSnapshotReader
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _scope;
    private readonly string? _expectedRepository;
    private IReadOnlyList<CapabilityAvailabilityEvidence> _lastReadEvidence = [];

    public GitHubWorkItemProvider(HttpClient http, Uri? graphQlEndpoint = null, string? expectedRepository = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _endpoint = graphQlEndpoint ?? new Uri("https://api.github.com/graphql");
        if (!_endpoint.IsAbsoluteUri || _endpoint.Scheme != Uri.UriSchemeHttps
            || _endpoint.UserInfo.Length != 0 || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0
            || _endpoint.AbsolutePath is not ("/graphql" or "/api/graphql"))
            throw new ArgumentException("An HTTPS GitHub GraphQL endpoint without credentials, query or fragment is required.",
                nameof(graphQlEndpoint));
        _scope = _endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        _expectedRepository = expectedRepository;
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

    public async Task<ProviderWorkItemSnapshot> FetchSnapshotAsync(
        ExternalWorkItemIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        if (identity.ProviderKind != Capabilities.ProviderKind || identity.ResourceScope != _scope)
            throw new ArgumentException("Identity must belong to this GitHub API origin.", nameof(identity));

        try
        {
            var node = await ReadNodeAsync(identity, GitHubIssueQueries.Snapshot, null, ct);
            if (_expectedRepository is not null
                && !string.Equals(Text(Required(node, "repository"), "nameWithOwner"), _expectedRepository,
                    StringComparison.OrdinalIgnoreCase))
                throw new JsonException("GitHub issue is outside the configured repository.");
            var fields = FieldValues.Empty
                .Set("System.Title", new TextValue(Text(node, "title")))
                .Set("System.State", new TextValue(Text(node, "state")))
                .Set("github.number", new IntegerValue(IssueNumber(node)))
                .Set("github.url", new TextValue(Text(node, "url")));
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

            var labels = await ReadConnectionAsync(identity, "labels", ct);
            var labelValues = new StringListValue(labels.Select(l => Text(l, "name")));
            fields = fields.Set("github.labels", labelValues).Set("System.Tags", labelValues);
            var assignees = (await ReadConnectionAsync(identity, "assignees", ct))
                .Select(a => new GitHubIssueAssignee(NativeId(a), Text(a, "login")))
                .OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
            fields = fields.Set("github.assignees", new RawValue(
                JsonSerializer.Serialize(assignees, TwigJsonContext.Default.GitHubIssueAssigneeArray),
                "github.assignees.v1"));
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

            _lastReadEvidence = [];
            return new(identity, null, fields, relationships.ToArray(), RelationshipsComplete: true);
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
        if (result.Errors.ValueKind != JsonValueKind.Undefined)
        {
            if (result.Errors.ValueKind != JsonValueKind.Array)
                throw new JsonException("GitHub returned invalid errors.");
            if (result.Errors.GetArrayLength() != 0)
            {
                var denied = result.Errors.EnumerateArray().Any(e =>
                    e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        && type.GetString() is "FORBIDDEN" or "NOT_FOUND" or "UNAUTHORIZED");
                throw new GitHubGraphQlReadException(denied);
            }
        }
        var node = Required(result.Data, "node");
        if (Text(node, "__typename") != "Issue" || Text(node, "id") != identity.NativeId)
            throw new JsonException("GitHub did not return the requested issue identity.");
        return node;
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
