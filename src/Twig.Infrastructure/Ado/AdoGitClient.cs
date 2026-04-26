using System.Text;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Implements <see cref="IAdoGitService"/> via ADO Git REST API (api-version=7.1).
/// Scoped to the git project (which may differ from the backlog project).
/// </summary>
internal sealed class AdoGitClient : IAdoGitService
{
    private const string ApiVersion = "7.1";

    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string _backlogProject;
    private readonly string _repository;

    public AdoGitClient(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project,
        string repository,
        string? backlogProject = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authProvider);
        if (string.IsNullOrWhiteSpace(orgUrl))
            throw new InvalidOperationException("Organization is not configured.");
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("Git project is not configured.");
        if (string.IsNullOrWhiteSpace(repository))
            throw new InvalidOperationException("Git repository is not configured.");

        _http = httpClient;
        _authProvider = authProvider;
        _orgUrl = AdoRestClient.NormalizeOrgUrl(orgUrl);
        _project = project;
        _backlogProject = string.IsNullOrWhiteSpace(backlogProject) ? project : backlogProject;
        _repository = repository;
    }

    public async Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForBranchAsync(string branchName, CancellationToken ct = default)
    {
        var encodedRepo = Uri.EscapeDataString(_repository);
        var encodedBranch = Uri.EscapeDataString($"refs/heads/{branchName}");
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/git/repositories/{encodedRepo}/pullrequests?searchCriteria.sourceRefName={encodedBranch}&searchCriteria.status=active&api-version={ApiVersion}";

        using var response = await SendAsync(HttpMethod.Get, url, content: null, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoPullRequestListResponse, ct);

        if (result?.Value is null || result.Value.Count == 0)
            return Array.Empty<PullRequestInfo>();

        var prs = new List<PullRequestInfo>(result.Value.Count);
        foreach (var pr in result.Value)
        {
            prs.Add(MapPullRequest(pr));
        }

        return prs;
    }

    public async Task<PullRequestInfo> CreatePullRequestAsync(PullRequestCreate request, CancellationToken ct = default)
    {
        var encodedRepo = Uri.EscapeDataString(_repository);
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/git/repositories/{encodedRepo}/pullrequests?api-version={ApiVersion}";

        var body = new AdoCreatePullRequestRequest
        {
            SourceRefName = request.SourceBranch,
            TargetRefName = request.TargetBranch,
            Title = request.Title,
            Description = request.Description,
            IsDraft = request.IsDraft,
        };

        if (request.WorkItemId.HasValue)
        {
            body.WorkItemRefs = new List<AdoWorkItemRef>
            {
                new() { Id = request.WorkItemId.Value.ToString() }
            };
        }

        var json = JsonSerializer.Serialize(body, TwigJsonContext.Default.AdoCreatePullRequestRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await SendAsync(HttpMethod.Post, url, content, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var pr = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoPullRequestResponse, ct)
            ?? throw new AdoException("Failed to deserialize PR creation response.");

        return MapPullRequest(pr);
    }

    public async Task<string?> GetRepositoryIdAsync(CancellationToken ct = default)
    {
        var encodedRepo = Uri.EscapeDataString(_repository);
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/git/repositories/{encodedRepo}?api-version={ApiVersion}";

        using var response = await SendAsync(HttpMethod.Get, url, content: null, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var repo = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoRepositoryResponse, ct);
        return repo?.Id;
    }

    public async Task<string?> GetProjectIdAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}?api-version={ApiVersion}";

        using var response = await SendAsync(HttpMethod.Get, url, content: null, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var project = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoProjectResponse, ct);
        return project?.Id;
    }

    public async Task AddArtifactLinkAsync(int workItemId, string artifactUri, string linkType, int revision, string? name = null, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_backlogProject)}/_apis/wit/workitems/{workItemId}?api-version={ApiVersion}";
        var relationValue = JsonSerializer.SerializeToNode(
            new AdoArtifactLinkRelation
            {
                Rel = linkType,
                Url = artifactUri,
                Attributes = new AdoArtifactLinkAttributes { Name = name ?? "Pull Request" }
            },
            TwigJsonContext.Default.AdoArtifactLinkRelation);

        var patchDoc = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/relations/-",
                Value = relationValue
            }
        };

        var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
        var content = new StringContent(json, Encoding.UTF8, "application/json-patch+json");

        using var response = await SendAsync(HttpMethod.Patch, url, content, ct,
            req => req.Headers.TryAddWithoutValidation("If-Match", revision.ToString()));
    }

    // ── HTTP plumbing ───────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken ct,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        try
        {
            return await SendCoreAsync(method, url, content, ct, configureRequest);
        }
        catch (Exception ex) when (AdoErrorHandler.IsAuthChallenge(ex))
        {
            _authProvider.InvalidateToken();
            if (content is not null) throw;
            return await SendCoreAsync(method, url, content, ct, configureRequest);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken ct,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Content = content;

        var token = await _authProvider.GetAccessTokenAsync(ct);
        AdoErrorHandler.ApplyAuthHeader(request, token);
        configureRequest?.Invoke(request);

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
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }

    private static PullRequestInfo MapPullRequest(AdoPullRequestResponse pr)
    {
        return new PullRequestInfo(
            pr.PullRequestId,
            pr.Title,
            pr.Status,
            pr.SourceRefName,
            pr.TargetRefName,
            pr.Url);
    }
}
