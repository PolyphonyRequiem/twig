using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Implements <see cref="IIterationService"/> via ADO REST API.
/// Provides current iteration detection and process template inference.
/// </summary>
internal sealed class AdoIterationService : IIterationService
{
    private const string ApiVersion = "7.1";

    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string _team;

    private Task<AdoWorkItemTypeListResponse?>? _workItemTypesCache;

    public AdoIterationService(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project,
        string? team = null)
    {
        if (string.IsNullOrWhiteSpace(orgUrl))
            throw new InvalidOperationException("Organization is not configured. Run 'twig init --org <org> --project <project>' first.");
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("Project is not configured. Run 'twig init --org <org> --project <project>' first.");

        _http = httpClient;
        _authProvider = authProvider;
        _orgUrl = AdoRestClient.NormalizeOrgUrl(orgUrl);
        _project = project;
        _team = team ?? project; // default team name = project name
    }

    public async Task<IterationPath> GetCurrentIterationAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/{Uri.EscapeDataString(_team)}/_apis/work/teamsettings/iterations?$timeframe=current&api-version={ApiVersion}";
        using var response = await SendAsync(url, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoIterationListResponse, ct);

        if (result?.Value is null || result.Value.Count == 0)
            throw new AdoException("No current iteration found.");

        var iteration = result.Value[0];
        var pathResult = IterationPath.Parse(iteration.Path);

        if (!pathResult.IsSuccess)
            throw new AdoException($"Invalid iteration path from ADO: '{iteration.Path}'.");

        return pathResult.Value;
    }

    public async Task<string?> DetectTemplateNameAsync(CancellationToken ct = default)
    {
        try
        {
            var apiResult = await DetectTemplateNameByApiAsync(ct);
            if (!string.IsNullOrEmpty(apiResult))
                return apiResult;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // API call failed — fall back to heuristic
        }

        return await DetectTemplateNameByHeuristicAsync(ct);
    }

    private async Task<string?> DetectTemplateNameByApiAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}?includeCapabilities=true&api-version={ApiVersion}";
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var adoResponse = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoProjectWithCapabilitiesResponse, ct);
        return adoResponse?.Capabilities?.ProcessTemplate?.TemplateName;
    }

    private async Task<string?> DetectTemplateNameByHeuristicAsync(CancellationToken ct)
    {
        var result = await GetWorkItemTypesResponseAsync(ct);

        if (result?.Value is null || result.Value.Count == 0)
            return "Basic";

        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in result.Value)
        {
            if (type.Name is not null)
                typeNames.Add(type.Name);
        }

        // Heuristic: check for distinguishing type names
        if (typeNames.Contains("User Story"))
            return "Agile";

        if (typeNames.Contains("Product Backlog Item"))
            return "Scrum";

        if (typeNames.Contains("Requirement"))
            return "CMMI";

        return "Basic";
    }

    public async Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default)
    {
        var result = await GetWorkItemTypesResponseAsync(ct);

        if (result?.Value is null || result.Value.Count == 0)
            return Array.Empty<WorkItemTypeAppearance>();

        var appearances = new List<WorkItemTypeAppearance>();
        foreach (var type in result.Value)
        {
            if (type.Name is null || type.Color is null || type.IsDisabled)
                continue;

            appearances.Add(new WorkItemTypeAppearance(type.Name, type.Color, type.Icon?.Id));
        }

        return appearances;
    }

    public async Task<IReadOnlyList<WorkItemTypeWithStates>> GetWorkItemTypesWithStatesAsync(CancellationToken ct = default)
    {
        var result = await GetWorkItemTypesResponseAsync(ct);

        if (result?.Value is null || result.Value.Count == 0)
            return Array.Empty<WorkItemTypeWithStates>();

        var types = new List<WorkItemTypeWithStates>();
        foreach (var type in result.Value)
        {
            if (type.Name is null || type.IsDisabled)
                continue; // skip disabled; retain null-color types

            var sortedStates = SortStates(type.States);

            if (type.States is { Count: > 0 } && sortedStates.Count == 0)
            {
                // Defensive: states list non-empty but all failed to sort — retain originals
                Console.Error.WriteLine($"⚠ States not populated in list response for type '{type.Name}'; state transition validation unavailable.");
            }

            types.Add(new WorkItemTypeWithStates
            {
                Name = type.Name,
                Color = type.Color,
                IconId = type.Icon?.Id,
                States = sortedStates,
            });
        }

        return types;
    }

    public async Task<ProcessConfigurationData> GetProcessConfigurationAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/work/processconfiguration?api-version={ApiVersion}";
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var adoResponse = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoProcessConfigurationResponse, ct);

            return new ProcessConfigurationData
            {
                TaskBacklog = MapBacklogLevel(adoResponse?.TaskBacklog),
                RequirementBacklog = MapBacklogLevel(adoResponse?.RequirementBacklog),
                PortfolioBacklogs = adoResponse?.PortfolioBacklogs?
                    .Select(MapBacklogLevel)
                    .Where(b => b is not null)
                    .Cast<BacklogLevelConfiguration>()
                    .ToList()
                    ?? (IReadOnlyList<BacklogLevelConfiguration>)Array.Empty<BacklogLevelConfiguration>(),
                BugWorkItems = MapBacklogLevel(adoResponse?.BugWorkItems),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is AdoNotFoundException or AdoException)
        {
            Console.Error.WriteLine($"⚠ Could not fetch process configuration: {ex.Message}. Parent-child relationships will not be populated.");
            return new ProcessConfigurationData();
        }
    }

    public async Task<IReadOnlyList<FieldDefinition>> GetFieldDefinitionsAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/fields?api-version={ApiVersion}";
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoFieldListResponse, ct);

            if (result?.Value is null || result.Value.Count == 0)
                return Array.Empty<FieldDefinition>();

            var defs = new List<FieldDefinition>(result.Value.Count);
            foreach (var f in result.Value)
            {
                if (f.ReferenceName is null || f.Name is null)
                    continue;
                defs.Add(new FieldDefinition(f.ReferenceName, f.Name, f.Type ?? "string", f.ReadOnly));
            }
            return defs;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is AdoNotFoundException or AdoException)
        {
            Console.Error.WriteLine($"⚠ Could not fetch field definitions: {ex.Message}. Dynamic columns will use derived display names.");
            return Array.Empty<FieldDefinition>();
        }
    }

    public async Task<IReadOnlyList<(string Path, bool IncludeChildren)>> GetTeamAreaPathsAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/{Uri.EscapeDataString(_team)}/_apis/work/teamsettings/teamfieldvalues?api-version={ApiVersion}";
        using var response = await SendAsync(url, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoTeamFieldValuesResponse, ct);

        if (result?.Values is null || result.Values.Count == 0)
            return result?.DefaultValue is not null ? [(result.DefaultValue, true)] : Array.Empty<(string, bool)>();

        var paths = new List<(string Path, bool IncludeChildren)>(result.Values.Count);
        foreach (var v in result.Values)
        {
            if (v.Value is not null)
                paths.Add((v.Value, v.IncludeChildren));
        }

        return paths;
    }

    public async Task<string?> GetAuthenticatedUserDisplayNameAsync(CancellationToken ct = default)
    {
        try
        {
            // Use the VSSPS profile endpoint — works reliably with both PAT and az cli tokens
            var url = "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1";
            using var response = await SendAsync(url, ct);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoProfileResponse, ct);

            return result?.DisplayName;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Graceful fallback — user identity detection is best-effort
            return null;
        }
    }

    /// <summary>
    /// Sorts states by category rank (Proposed=0, InProgress=1, Resolved=2, Completed=3, Removed=4, Unknown=5),
    /// preserving original within-category order via stable sort on original index.
    /// Algorithm documented in twig-dynamic-process.plan.md §7 "State Ordering Algorithm".
    /// </summary>
    private static IReadOnlyList<WorkItemTypeState> SortStates(List<AdoWorkItemStateColor>? rawStates)
    {
        if (rawStates is null || rawStates.Count == 0)
            return Array.Empty<WorkItemTypeState>();

        static int CategoryRank(string? category) => category?.ToLowerInvariant() switch
        {
            "proposed" => 0,
            "inprogress" => 1,
            "resolved" => 2,
            "completed" => 3,
            "removed" => 4,
            _ => 5,
        };

        return rawStates
            .Select((s, i) => (state: s, index: i))
            .Where(x => x.state.Name is not null)
            .OrderBy(x => CategoryRank(x.state.Category))
            .ThenBy(x => x.index)
            .Select(x => new WorkItemTypeState
            {
                Name = x.state.Name!,
                Category = x.state.Category ?? string.Empty,
                Color = x.state.Color,
            })
            .ToList();
    }

    private static BacklogLevelConfiguration? MapBacklogLevel(AdoCategoryConfiguration? cat)
    {
        if (cat is null) return null;
        return new BacklogLevelConfiguration
        {
            Name = cat.Name ?? string.Empty,
            WorkItemTypeNames = cat.WorkItemTypes?
                .Where(t => t.Name is not null)
                .Select(t => t.Name!)
                .ToList()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
        };
    }

    private async Task<AdoWorkItemTypeListResponse?> GetWorkItemTypesResponseAsync(CancellationToken ct)
    {
        // Lazy initialization — safe because CLI is single-threaded
        _workItemTypesCache ??= FetchWorkItemTypesAsync(ct);
        return await _workItemTypesCache;
    }

    private async Task<AdoWorkItemTypeListResponse?> FetchWorkItemTypesAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/workitemtypes?api-version={ApiVersion}";
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoWorkItemTypeListResponse, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var token = await _authProvider.GetAccessTokenAsync(ct);
        AdoErrorHandler.ApplyAuthHeader(request, token);

        var response = await _http.SendAsync(request, ct);
        await AdoErrorHandler.ThrowOnErrorAsync(response, url, ct);
        return response;
    }
}
