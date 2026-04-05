using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Implements <see cref="IAdoWorkItemService"/> via ADO REST API (api-version=7.1).
/// </summary>
internal sealed class AdoRestClient : IAdoWorkItemService
{
    private const string ApiVersion = "7.1";
    private const string CommentApiVersion = "7.1-preview.4";
    private const string JsonPatchMediaType = "application/json-patch+json";

    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly IFieldDefinitionStore? _fieldDefStore;
    private IReadOnlyDictionary<string, FieldDefinition>? _fieldDefLookup;

    public AdoRestClient(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project,
        IFieldDefinitionStore? fieldDefStore = null)
    {
        if (string.IsNullOrWhiteSpace(orgUrl))
            throw new InvalidOperationException("Organization is not configured. Run 'twig init --org <org> --project <project>' first.");
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("Project is not configured. Run 'twig init --org <org> --project <project>' first.");

        _http = httpClient;
        _authProvider = authProvider;
        _orgUrl = NormalizeOrgUrl(orgUrl);
        _project = project;
        _fieldDefStore = fieldDefStore;
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
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?$expand=relations&api-version={ApiVersion}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        var lookup = await GetFieldDefLookupAsync(ct);
        return AdoResponseMapper.MapWorkItem(dto, lookup);
    }

    public async Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?$expand=relations&api-version={ApiVersion}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        var lookup = await GetFieldDefLookupAsync(ct);
        return AdoResponseMapper.MapWorkItemWithLinks(dto, lookup);
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
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}?api-version={ApiVersion}";
        var patchDoc = AdoResponseMapper.MapPatchDocument(changes);
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var response = await SendAsync(HttpMethod.Patch, url, content, ifMatch: expectedRevision.ToString(), ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        return dto.Rev;
    }

    public async Task<int> CreateAsync(WorkItem seed, CancellationToken ct = default)
    {
        var typeName = Uri.EscapeDataString(seed.Type.Value);
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/${typeName}?api-version={ApiVersion}";
        var patchDoc = AdoResponseMapper.MapSeedToCreatePayload(seed, _orgUrl, seed.ParentId);
        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);

        using var response = await SendAsync(HttpMethod.Post, url, content, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(response, ct);
        return dto.Id;
    }

    public async Task AddCommentAsync(int id, string text, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{id}/comments?api-version={CommentApiVersion}";
        var request = new AdoCommentRequest { Text = text };
        var json = JsonSerializer.Serialize(request, TwigJsonContext.Default.AdoCommentRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var _ = await SendAsync(HttpMethod.Post, url, content, ifMatch: null, ct);
    }

    public async Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/wiql?api-version={ApiVersion}";
        var request = new AdoWiqlRequest { Query = wiql };
        var json = JsonSerializer.Serialize(request, TwigJsonContext.Default.AdoWiqlRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await SendAsync(HttpMethod.Post, url, content, ifMatch: null, ct);
        var dto = await DeserializeWiqlAsync(response, ct);

        if (dto.WorkItems is null || dto.WorkItems.Count == 0)
            return Array.Empty<int>();

        var ids = new List<int>(dto.WorkItems.Count);
        foreach (var item in dto.WorkItems)
        {
            ids.Add(item.Id);
        }

        return ids;
    }

    public async Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={ApiVersion}";
        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/relations/-",
                Value = new System.Text.Json.Nodes.JsonObject
                {
                    ["rel"] = System.Text.Json.Nodes.JsonValue.Create(adoLinkType),
                    ["url"] = System.Text.Json.Nodes.JsonValue.Create($"{_orgUrl}/_apis/wit/workitems/{targetId}"),
                },
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
        var getUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?$expand=relations&api-version={ApiVersion}";
        using var getResponse = await SendAsync(HttpMethod.Get, getUrl, content: null, ifMatch: null, ct);
        var dto = await DeserializeWorkItemAsync(getResponse, ct);

        // 2. Find the index of the relation matching the link type and target work item ID.
        var targetUrl = $"{_orgUrl}/_apis/wit/workitems/{targetId}";
        var relationIndex = dto.Relations?.FindIndex(r =>
            string.Equals(r.Rel, adoLinkType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Url, targetUrl, StringComparison.OrdinalIgnoreCase)) ?? -1;

        // Idempotent: if the relation doesn't exist, return silently.
        if (relationIndex < 0)
            return;

        // 3. PATCH with a JSON Patch "remove" operation and If-Match for optimistic concurrency.
        var patchUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={ApiVersion}";
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
        if (ids.Count <= MaxBatchSize)
            return await FetchBatchChunkAsync(ids, ct);

        var items = new List<WorkItem>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += MaxBatchSize)
        {
            var count = Math.Min(MaxBatchSize, ids.Count - offset);
            var chunk = new List<int>(count);
            for (var i = offset; i < offset + count; i++)
                chunk.Add(ids[i]);

            var chunkItems = await FetchBatchChunkAsync(chunk, ct);
            items.AddRange(chunkItems);
        }

        return items;
    }

    private async Task<IReadOnlyList<WorkItem>> FetchBatchChunkAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var idsCsv = string.Join(',', ids);
        var url = $"{_orgUrl}/{_project}/_apis/wit/workitems?ids={idsCsv}&$expand=relations&api-version={ApiVersion}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ifMatch: null, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoBatchWorkItemResponse, ct);

        if (result?.Value is null || result.Value.Count == 0)
            return Array.Empty<WorkItem>();

        var lookup = await GetFieldDefLookupAsync(ct);
        var items = new List<WorkItem>(result.Value.Count);
        foreach (var dto in result.Value)
        {
            items.Add(AdoResponseMapper.MapWorkItem(dto, lookup));
        }

        return items;
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

        await AdoErrorHandler.ThrowOnErrorAsync(response, url, ct);
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
