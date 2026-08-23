using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Implements <see cref="IAdoWorkItemService"/> via ADO REST API.
/// </summary>
/// <remarks>
/// Each route names its own pinned api-version from <see cref="AdoApiVersions"/>, which
/// records what that version buys. Never inline a version literal here.
/// </remarks>
internal sealed class AdoRestClient : IAdoWorkItemService, IRevisionBoundAdoWorkItemService
{
    private const string JsonPatchMediaType = "application/json-patch+json";

    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly WorkItemMapper _mapper;
    private readonly IFieldDefinitionStore? _fieldDefStore;
    private readonly AdoConcurrencyThrottle? _throttle;
    private IReadOnlyDictionary<string, FieldDefinition>? _fieldDefLookup;

    public AdoRestClient(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project,
        WorkItemMapper mapper,
        IFieldDefinitionStore? fieldDefStore = null,
        AdoConcurrencyThrottle? throttle = null)
    {
        if (string.IsNullOrWhiteSpace(orgUrl))
            throw new InvalidOperationException("Organization is not configured. Run 'twig init --org <org> --project <project>' first.");
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("Project is not configured. Run 'twig init --org <org> --project <project>' first.");

        _http = httpClient;
        _authProvider = authProvider;
        _orgUrl = NormalizeOrgUrl(orgUrl);
        _project = project;
        _mapper = mapper;
        _fieldDefStore = fieldDefStore;
        _throttle = throttle;
    }

    /// <summary>
    /// Normalizes a bare org name (e.g. "my-org") to a full ADO URL ("https://dev.azure.com/my-org").
    /// Already-absolute URLs are returned as-is (trimmed).
    /// </summary>
    internal static string NormalizeOrgUrl(string orgUrl)
    {
        var trimmed = orgUrl.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return $"https://dev.azure.com/{trimmed}";
    }

    // ── IAdoWorkItemService ─────────────────────────────────────────

    public async Task<WorkItem> FetchAsync(int id, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?$expand=relations&api-version={AdoApiVersions.WorkItems}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        var lookup = await GetFieldDefLookupAsync(ct);
        var snapshot = AdoResponseMapper.MapToSnapshot(dto, lookup);
        return _mapper.Map(snapshot);
    }

    public async Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?$expand=relations&api-version={AdoApiVersions.WorkItems}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        var lookup = await GetFieldDefLookupAsync(ct);
        var (snapshot, links) = AdoResponseMapper.MapToSnapshotWithLinks(dto, lookup);
        return (_mapper.Map(snapshot), links);
    }

    public async Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default)
    {
        // Flat WIQL query returns queryType="flat" with a workItems array
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.Parent] = {parentId}";
        var ids = await QueryByWiqlAsync(wiql, ct);

        if (ids.Count == 0)
            return Array.Empty<WorkItem>();

        return await FetchBatchAsync(ids, ct);
    }

    public async Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?api-version={AdoApiVersions.WorkItems}";
        var patchDoc = AdoResponseMapper.MapPatchDocument(changes);
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var response = await SendAsync(HttpMethod.Patch, url, content, ifMatch: expectedRevision.ToString(), ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        return dto.Rev;
    }

    public async Task<int> CreateAsync(CreateWorkItemRequest request, CancellationToken ct = default)
    {
        var typeName = Uri.EscapeDataString(request.TypeName);
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/${typeName}?api-version={AdoApiVersions.WorkItemTemplate}";
        var patchDoc = AdoResponseMapper.MapSeedToCreatePayload(request, _orgUrl);
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var response = await SendAsync(HttpMethod.Post, url, content, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        return dto.Id;
    }

    public async Task<int?> FindPublishedIntentAsync(
        PublishIntent intent,
        CancellationToken ct = default)
    {
        var title = intent.Title;
        var typeName = intent.TypeName;
        var createdAtOrAfter = intent.RecordedAt;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(typeName))
            return null;

        // The constant tag narrows to items twig had in flight; title + type + the creation
        // fence identify which one. The fence matters: the tag is reused across publishes, so
        // without it an older item bearing a stale tag could be mistaken for this create.
        //
        // WIQL escapes a single quote by doubling it. Titles are user-supplied, so this is not
        // optional.
        var escapedTitle = title.Replace("'", "''");
        var escapedType = typeName.Replace("'", "''");
        var escapedTag = PublishIntent.IntentTag.Replace("'", "''");

        // Round DOWN to the whole second: the fence is a lower bound, and ADO stores
        // CreatedDate at ~millisecond resolution, so truncating keeps it inclusive of an item
        // created in the same second. A slightly loose bound is safe — title + type must match
        // too. Rounding UP would exclude the very item this query exists to find.
        var fence = createdAtOrAfter.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var wiql =
            $"SELECT [System.Id] FROM WorkItems WHERE [System.Tags] CONTAINS '{escapedTag}' " +
            $"AND [System.Title] = '{escapedTitle}' " +
            $"AND [System.WorkItemType] = '{escapedType}' " +
            $"AND [System.CreatedDate] >= '{fence}'";

        // timePrecision: true — the fence carries a time, which ADO rejects (HTTP 400) unless
        // this is set. Losing it degrades the query to day granularity at best.
        var ids = await ExecuteWiqlAsync(wiql, top: null, timePrecision: true, ct);

        // More than one match means the duplicate this mechanism exists to prevent already
        // happened. Return the lowest — the first create — so recovery adopts the original
        // rather than an accidental copy, and the extras stay visible in ADO for the user.
        return ids.Count == 0 ? null : ids.Min();
    }

    /// <summary>
    /// Removes the in-flight publish tag from a work item once its publish is recorded locally.
    /// Best-effort by contract: the caller must treat a failure as non-fatal, because the
    /// publish itself has already succeeded and the stale tag is cosmetic.
    /// </summary>
    public async Task ClearIntentTagAsync(int id, CancellationToken ct = default)
    {
        // Read current tags rather than blind-writing: System.Tags is a single delimited string,
        // so a replace would clobber any tag a human added to the item.
        var item = await FetchAsync(id, ct);
        if (!item.Fields.TryGetValue("System.Tags", out var current) || current is null)
            return;

        // Split ONCE. Two independently-written splits could drift apart, and the second one
        // drives the no-op short-circuit — a divergence there would silently issue a pointless
        // PATCH, or worse, skip a needed one.
        var tags = current.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var remaining = tags
            .Where(t => !string.Equals(t, PublishIntent.IntentTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (remaining.Count == tags.Length)
            return;

        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?api-version={AdoApiVersions.WorkItems}";
        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/fields/System.Tags",
                Value = System.Text.Json.Nodes.JsonValue.Create(string.Join("; ", remaining)),
            },
        };
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var _ = await SendAsync(HttpMethod.Patch, url, content, ifMatch: null, ct);
    }

    public async Task AddCommentAsync(int id, string text, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}/comments?api-version={AdoApiVersions.WorkItemComments}";
        var request = new AdoCommentRequest { Text = text };
        var json = JsonSerializer.Serialize(request, TwigJsonContext.Default.AdoCommentRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var _ = await SendAsync(HttpMethod.Post, url, content, ifMatch: null, ct);
    }

    public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default)
        => ExecuteWiqlAsync(wiql, top: null, ct);

    public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default)
        => ExecuteWiqlAsync(wiql, top, ct);

    private Task<IReadOnlyList<int>> ExecuteWiqlAsync(string wiql, int? top, CancellationToken ct)
        => ExecuteWiqlAsync(wiql, top, timePrecision: false, ct);

    // timePrecision is a QUERY-STRING parameter, not a body field. Without it ADO rejects any
    // date comparison carrying a time component with HTTP 400:
    //   "You cannot supply a time with the date when running a query using date precision."
    // Verified against live ADO (dangreen-msft/Twig) — a body-level "timePrecision" is silently
    // ignored, and the day-granularity fallback loses the sub-day fence entirely.
    private async Task<IReadOnlyList<int>> ExecuteWiqlAsync(
        string wiql, int? top, bool timePrecision, CancellationToken ct)
    {
        var topParam = top.HasValue ? $"&$top={top.Value}" : "";
        var precisionParam = timePrecision ? "&timePrecision=true" : "";
        var url = $"{_orgUrl}/{_project}/_apis/wit/wiql?api-version={AdoApiVersions.Wiql}{topParam}{precisionParam}";
        var request = new AdoWiqlRequest { Query = wiql };
        var json = JsonSerializer.Serialize(request, TwigJsonContext.Default.AdoWiqlRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await SendAsync(HttpMethod.Post, url, content, ifMatch: null, ct);
        var dto = await DeserializeWiqlAsync(response, ct);

        if (dto.WorkItems is null || dto.WorkItems.Count == 0)
            return Array.Empty<int>();

        return dto.WorkItems.Select(x => x.Id).ToList();
    }

    public Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
        => AddLinkWithCommentAsync(sourceId, targetId, adoLinkType, comment: null, ct);

    /// <inheritdoc />
    public async Task AddLinkWithCommentAsync(int sourceId, int targetId, string adoLinkType, string? comment, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={AdoApiVersions.WorkItems}";
        var relation = new System.Text.Json.Nodes.JsonObject
        {
            ["rel"] = System.Text.Json.Nodes.JsonValue.Create(adoLinkType),
            ["url"] = System.Text.Json.Nodes.JsonValue.Create($"{_orgUrl}/_apis/wit/workitems/{targetId}"),
        };

        // Only emit `attributes` when there is a comment: an empty attributes object is not the
        // same request, and every existing caller must keep sending exactly what it sent before.
        if (!string.IsNullOrWhiteSpace(comment))
        {
            relation["attributes"] = new System.Text.Json.Nodes.JsonObject
            {
                ["comment"] = System.Text.Json.Nodes.JsonValue.Create(comment),
            };
        }

        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/relations/-",
                Value = relation,
            },
        };
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var _ = await SendAsync(HttpMethod.Patch, url, content, ifMatch: null, ct);
    }

    /// <inheritdoc />
    public async Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
    {
        // 1. GET current work item with relations to obtain the Rev (ETag) and relations array.
        var getUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?$expand=relations&api-version={AdoApiVersions.WorkItems}";
        using var getResponse = await SendAsync(HttpMethod.Get, getUrl, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(getResponse, ct);

        // 2. Find the index of the relation matching the link type and target work item ID.
        //    Use EndsWith to handle URL variants (with/without project segment),
        //    consistent with AdoResponseMapper.ExtractParentId / ExtractNonHierarchyLinks.
        var relationIndex = dto.Relations?.FindIndex(r =>
            string.Equals(r.Rel, adoLinkType, StringComparison.OrdinalIgnoreCase) &&
            r.Url is not null &&
            r.Url.EndsWith($"/{targetId}", StringComparison.OrdinalIgnoreCase)) ?? -1;

        // Idempotent: if the relation doesn't exist, return silently.
        if (relationIndex < 0)
            return;

        // 3. PATCH with a JSON Patch "remove" operation and If-Match for optimistic concurrency.
        var patchUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={AdoApiVersions.WorkItems}";
        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "remove",
                Path = $"/relations/{relationIndex}",
            },
        };
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var _ = await SendAsync(HttpMethod.Patch, patchUrl, content, ifMatch: dto.Rev.ToString(), ct);
    }

    /// <inheritdoc />
    public async Task<bool> AddArtifactLinkAsync(int workItemId, string url, string? name = null, CancellationToken ct = default)
    {
        // 1. Fetch current revision for optimistic concurrency
        var workItemUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{workItemId}?api-version={AdoApiVersions.WorkItems}";
        using var getResponse = await SendAsync(HttpMethod.Get, workItemUrl, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(getResponse, ct);

        // 2. Auto-detect relation type
        var isArtifactLink = url.StartsWith("vstfs:///", StringComparison.OrdinalIgnoreCase);
        var relType = isArtifactLink ? "ArtifactLink" : "Hyperlink";

        // 3. Build attributes — ArtifactLink uses Name, Hyperlink uses Comment
        var attributes = isArtifactLink
            ? new AdoArtifactLinkAttributes { Name = name ?? "Artifact" }
            : new AdoArtifactLinkAttributes { Comment = name };

        var relationValue = JsonSerializer.SerializeToNode(
            new AdoArtifactLinkRelation
            {
                Rel = relType,
                Url = url,
                Attributes = attributes,
            },
            TwigJsonContext.Default.AdoArtifactLinkRelation);

        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/relations/-",
                Value = relationValue,
            },
        };

        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var patchContent = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        try
        {
            using var _ = await SendAsync(HttpMethod.Patch, workItemUrl, patchContent, ifMatch: dto.Rev.ToString(), ct);
            return false; // newly created
        }
        catch (AdoDuplicateRelationException)
        {
            return true; // already linked
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?api-version={AdoApiVersions.WorkItems}";
        try
        {
            using var _ = await SendAsync(HttpMethod.Delete, url, content: null, ifMatch: null, ct);
        }
        catch (AdoNotFoundException)
        {
            // 404 is treated as idempotent success — the item is already gone.
        }
    }

    /// <inheritdoc />
    public async Task<int> AddLinkAtRevisionAsync(
        int sourceId,
        string relationType,
        int targetId,
        int expectedRevision,
        CancellationToken ct = default)
    {
        // Strict CAS variant of AddLinkAsync: no fetch, no ConflictRetryHelper, no rebase.
        // The caller's expected revision is sent verbatim as If-Match; ADO's 412 surfaces as
        // AdoConflictException via AdoErrorHandler.
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={AdoApiVersions.WorkItems}";
        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/relations/-",
                Value = new System.Text.Json.Nodes.JsonObject
                {
                    ["rel"] = System.Text.Json.Nodes.JsonValue.Create(relationType),
                    ["url"] = System.Text.Json.Nodes.JsonValue.Create($"{_orgUrl}/_apis/wit/workitems/{targetId}"),
                },
            },
        };
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var response = await SendAsync(HttpMethod.Patch, url, content, ifMatch: expectedRevision.ToString(), ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        return dto.Rev;
    }

    /// <inheritdoc />
    public async Task<int> RemoveLinkAtRevisionAsync(
        int sourceId,
        string relationType,
        int targetId,
        int expectedRevision,
        CancellationToken ct = default)
    {
        // JSON Patch remove requires the relation's array index. ADO exposes no
        // find-and-remove primitive, so we GET the relations to compute the index — but the
        // GET is used ONLY for index resolution. If the fetched revision no longer matches
        // the caller's expectation, we refuse to touch the item and surface it as a
        // concurrency conflict; the PATCH's If-Match still carries the caller's expected
        // revision so the server independently enforces the same invariant.
        var getUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?$expand=relations&api-version={AdoApiVersions.WorkItems}";
        using var getResponse = await SendAsync(HttpMethod.Get, getUrl, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(getResponse, ct);

        if (dto.Rev != expectedRevision)
            throw new AdoConflictException(
                dto.Rev,
                $"Work item #{sourceId} is at revision {dto.Rev}; expected {expectedRevision} for strict remove-link.");

        var relationIndex = dto.Relations?.FindIndex(r =>
            string.Equals(r.Rel, relationType, StringComparison.OrdinalIgnoreCase) &&
            r.Url is not null &&
            r.Url.EndsWith($"/{targetId}", StringComparison.OrdinalIgnoreCase)) ?? -1;

        if (relationIndex < 0)
            throw new AdoRelationNotFoundException(sourceId, relationType, targetId, expectedRevision);

        var patchUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={AdoApiVersions.WorkItems}";
        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "remove",
                Path = $"/relations/{relationIndex}",
            },
        };
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var patchResponse = await SendAsync(HttpMethod.Patch, patchUrl, content, ifMatch: expectedRevision.ToString(), ct);
        var patched = await DeserializeWorkItemAsync(patchResponse, ct);
        return patched.Rev;
    }

    /// <inheritdoc />
    public async Task DeleteAtRevisionAsync(int id, int expectedRevision, CancellationToken ct = default)
    {
        // Strict CAS delete: expected revision as If-Match, no refetch, no retry. Exactly
        // one HTTP DELETE is issued — the auth-retry-on-empty-body branch in SendAsync is
        // suppressed here so a 401/203 challenge surfaces immediately instead of racing a
        // second delete against a possibly-mutated item. 404 is idempotent success — a
        // delete whose goal state is 'gone' has succeeded when the item is already gone.
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?api-version={AdoApiVersions.WorkItems}";
        try
        {
            using var _ = await SendAsync(
                HttpMethod.Delete, url, content: null, ifMatch: expectedRevision.ToString(), ct,
                allowAuthRetry: false);
        }
        catch (AdoNotFoundException)
        {
            // Item already gone — idempotent success.
        }
    }

    // ── Batch fetch ─────────────────────────────────────────────────

    /// <summary>
    /// ADO REST API limit: max 200 IDs per batch request.
    /// </summary>
    internal const int MaxBatchSize = 200;

    /// <summary>
    /// Fetches multiple work items, chunking into groups of ≤200 to respect the ADO batch limit.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var (items, _) = await FetchBatchWithLinksAsync(ids, ct);
        return items;
    }

    /// <summary>
    /// Fetches a SET of work items together with the non-hierarchy edges among them (ADO #154),
    /// chunking into groups of ≤200 to respect the ADO batch limit.
    /// </summary>
    /// <remarks>
    /// Issues exactly the same requests as <see cref="FetchBatchAsync"/> — the batch URL has
    /// always carried <c>$expand=relations</c>, so the links come back on the existing round
    /// trips and this overload simply stops discarding them.
    /// </remarks>
    public async Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct = default)
    {
        if (ids.Count <= MaxBatchSize)
            return await FetchBatchChunkAsync(ids, ct);

        var items = new List<WorkItem>(ids.Count);
        var links = new List<WorkItemLink>();
        for (var offset = 0; offset < ids.Count; offset += MaxBatchSize)
        {
            var count = Math.Min(MaxBatchSize, ids.Count - offset);
            var chunk = new List<int>(count);
            for (var i = offset; i < offset + count; i++)
                chunk.Add(ids[i]);

            var (chunkItems, chunkLinks) = await FetchBatchChunkAsync(chunk, ct);
            items.AddRange(chunkItems);
            links.AddRange(chunkLinks);
        }

        return (items, links);
    }

    private async Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchChunkAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct)
    {
        var idsCsv = string.Join(',', ids);
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems?ids={idsCsv}&$expand=relations&api-version={AdoApiVersions.WorkItems}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoBatchWorkItemResponse, ct);

        if (result?.Value is null || result.Value.Count == 0)
            return (Array.Empty<WorkItem>(), Array.Empty<WorkItemLink>());

        var lookup = await GetFieldDefLookupAsync(ct);
        var items = new List<WorkItem>(result.Value.Count);
        var links = new List<WorkItemLink>();
        foreach (var dto in result.Value)
        {
            // MapToSnapshotWithLinks, not MapToSnapshot: the relations are already on the wire
            // (see $expand=relations above) and the single-item mapper discarded them (ADO #154).
            var (snapshot, itemLinks) = AdoResponseMapper.MapToSnapshotWithLinks(dto, lookup);
            items.Add(_mapper.Map(snapshot));
            links.AddRange(itemLinks);
        }

        return (items, links);
    }

    // ── Work item history (twig#241) ────────────────────────────────

    /// <summary>Page size for the offset-paged updates traversal.</summary>
    internal const int HistoryPageSize = 100;

    /// <summary>Hard cap on pages, guarding against a server that never returns a short page.</summary>
    private const int MaxHistoryPages = 500;

    /// <inheritdoc />
    public async Task<WorkItemHistory> FetchHistoryAsync(
        int id,
        WorkItemHistoryOptions options,
        CancellationToken ct = default)
    {

        // Complete-or-error: any page failure propagates as a typed ADO exception and fails the
        // whole operation. A partial timeline is never reported as success.
        var updates = await FetchAllUpdatePagesAsync(id, ct);

        var enrichment = await EnrichRelationTargetsAsync(updates, ct);

        return WorkItemHistoryProjector.Project(id, updates, options, enrichment);
    }

    /// <summary>
    /// Traverses every page of the updates endpoint. ADO uses offset paging via
    /// <c>$top</c>/<c>$skip</c> with no continuation token; termination keys on a SHORT PAGE.
    /// The per-response <c>count</c> reflects the current page, not the total history, so it is
    /// never used as a terminator.
    /// </summary>
    private async Task<List<AdoWorkItemUpdate>> FetchAllUpdatePagesAsync(int id, CancellationToken ct)
    {
        var all = new List<AdoWorkItemUpdate>();

        for (var page = 0; page < MaxHistoryPages; page++)
        {
            var skip = page * HistoryPageSize;
            var url = $"{_orgUrl}/{_project}/_apis/wit/workItems/{id}/updates" +
                      $"?$top={HistoryPageSize}&$skip={skip}&api-version={AdoApiVersions.WorkItemUpdates}";

            using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoWorkItemUpdatesResponse, ct)
                ?? throw new AdoException("Failed to deserialize ADO work item updates response.");

            var value = dto.Value ?? [];
            all.AddRange(value);

            // Short page ⇒ traversal complete.
            if (value.Count < HistoryPageSize) return all;
        }

        throw new AdoException(
            $"Work item #{id} history exceeded {MaxHistoryPages} pages; refusing to report a truncated timeline.");
    }

    /// <summary>
    /// Enriches work-item relation targets with title, type, and state via a single batch call.
    /// </summary>
    /// <remarks>
    /// <c>errorPolicy=omit</c> is MANDATORY: without it a single deleted or unreadable target
    /// causes the whole batch to return HTTP 404, taking down history for that item (verified
    /// directly — the same request returns 404 plain and 200 with the policy).
    /// Enrichment failure is swallowed: the traversal was complete regardless of whether
    /// decoration succeeded, and conflating the two would fail the command on exactly the items
    /// most worth reading.
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, WorkItemRelationTarget>?> EnrichRelationTargetsAsync(
        IReadOnlyList<AdoWorkItemUpdate> updates,
        CancellationToken ct)
    {
        var targetIds = WorkItemHistoryProjector.CollectRelationTargetIds(updates);
        if (targetIds.Count == 0) return null;

        var resolved = new Dictionary<int, WorkItemRelationTarget>();

        try
        {
            for (var offset = 0; offset < targetIds.Count; offset += MaxBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = targetIds.Skip(offset).Take(MaxBatchSize).ToList();
                var idsCsv = string.Join(',', chunk);
                var url = $"{_orgUrl}/{_project}/_apis/wit/workitems" +
                          $"?ids={idsCsv}&errorPolicy=omit&api-version={AdoApiVersions.WorkItems}";

                using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var batch = await JsonSerializer.DeserializeAsync(
                    stream, TwigJsonContext.Default.AdoBatchWorkItemResponse, ct);

                foreach (var dto in batch?.Value ?? [])
                {
                    resolved[dto.Id] = new WorkItemRelationTarget(
                        dto.Id,
                        ReadField(dto, "System.Title"),
                        ReadField(dto, "System.WorkItemType"),
                        ReadField(dto, "System.State"),
                        Deleted: false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort decoration only — never affects `complete`. Unresolved targets are
            // reported as deleted by the projector.
        }

        return resolved;
    }

    private static string? ReadField(AdoWorkItemResponse dto, string referenceName)
    {
        if (dto.Fields is null) return null;
        if (!dto.Fields.TryGetValue(referenceName, out var value) || value is null) return null;

        return value switch
        {
            string s => s,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
            JsonElement e => e.GetRawText(),
            _ => value.ToString(),
        };
    }

    // ── Field definition lookup (lazy cache) ──────────────────────

    /// <summary>
    /// Lazy-loads and caches field definitions from the store.
    /// Note: intentionally not thread-safe — CLI is single-threaded per command.
    /// Concurrent callers may redundantly build the lookup, which is benign.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, FieldDefinition>?> GetFieldDefLookupAsync(CancellationToken ct)
    {
        if (_fieldDefLookup is not null) return _fieldDefLookup;
        if (_fieldDefStore is null) return null;

        var defs = await _fieldDefStore.GetAllAsync(ct);
        if (defs.Count == 0) return null;

        var lookup = new Dictionary<string, FieldDefinition>(defs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs) lookup[d.ReferenceName] = d;
        _fieldDefLookup = lookup;
        return _fieldDefLookup;
    }

    // ── HTTP plumbing ───────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        string? ifMatch,
        CancellationToken ct,
        bool allowAuthRetry = true)
    {
        try
        {
            return await SendCoreAsync(method, url, content, ifMatch, ct);
        }
        catch (Exception ex) when (allowAuthRetry && AdoErrorHandler.IsAuthChallenge(ex))
        {
            _authProvider.InvalidateToken();
            // Only retry when there's no body content — HttpRequestMessage.Dispose()
            // disposes the content, so it can't be re-sent on writes. The invalidation
            // ensures the next call will use a fresh token.
            if (content is not null) throw;
            return await SendCoreAsync(method, url, content, ifMatch, ct);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        string? ifMatch,
        CancellationToken ct)
    {

        using var request = new HttpRequestMessage(method, url);
        request.Content = content;

        // Auth header
        var token = await _authProvider.GetAccessTokenAsync(ct);
        AdoErrorHandler.ApplyAuthHeader(request, token);

        // If-Match for optimistic concurrency
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        // Acquire concurrency slot (no-op when throttle is not registered)
        using var throttleSlot = _throttle is not null
            ? await _throttle.AcquireAsync(ct)
            : null;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AdoOfflineException(ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AdoOfflineException(ex);
        }


        try
        {
            await AdoErrorHandler.ThrowOnErrorAsync(response, url, ct);
        }
        catch (AdoRateLimitException ex)
        {
            response.Dispose();
            _throttle?.SetPause(ex.RetryAfter);
            throw;
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }

    private static async Task<AdoWorkItemResponse> DeserializeWorkItemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoWorkItemResponse, ct);
        return result ?? throw new AdoException("Failed to deserialize ADO work item response.");
    }

    private static async Task<AdoWiqlResponse> DeserializeWiqlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoWiqlResponse, ct);
        return result ?? throw new AdoException("Failed to deserialize ADO WIQL response.");
    }
}
