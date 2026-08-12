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
/// <remarks>
/// Each route names its own pinned api-version from <see cref="AdoApiVersions"/>, which
/// records what that version buys. Never inline a version literal here.
/// </remarks>
internal sealed class AdoIterationService : IIterationService, IProcessRuleProvider, IFormLayoutProvider, IProcessTypeFieldProvider
{
    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string _team;

    // Lazy-initialized caches — safe because CLI is single-threaded
    private Task<AdoWorkItemTypeListResponse?>? _workItemTypesCache;
    private Task<AdoProcessTemplate?>? _processTemplateCache;
    private Task<ProcessConfigurationData>? _processConfigCache;
    private Task<IReadOnlyList<FieldDefinition>>? _fieldDefinitionsCache;
    private Task<IReadOnlyList<TeamIteration>>? _teamIterationsCache;
    private readonly Dictionary<string, Task<IReadOnlyList<ProcessRule>>> _processRulesCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<FormLayout?>> _formLayoutCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<IReadOnlyList<ProcessTypeField>?>> _processTypeFieldsCache =
        new(StringComparer.OrdinalIgnoreCase);

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
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/{Uri.EscapeDataString(_team)}/_apis/work/teamsettings/iterations?$timeframe=current&api-version={AdoApiVersions.TeamIterations}";
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
        var processTemplate = await (_processTemplateCache ??= FetchProcessTemplateAsync(ct));
        return processTemplate?.TemplateName;
    }

    private async Task<AdoProcessTemplate?> FetchProcessTemplateAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}?includeCapabilities=true&api-version={AdoApiVersions.Projects}";
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var adoResponse = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoProjectWithCapabilitiesResponse, ct);
        return adoResponse?.Capabilities?.ProcessTemplate;
    }

    private async Task<string?> DetectTemplateNameByHeuristicAsync(CancellationToken ct)
    {
        var result = await (_workItemTypesCache ??= FetchWorkItemTypesAsync(ct));

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
        var result = await (_workItemTypesCache ??= FetchWorkItemTypesAsync(ct));

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
        var result = await (_workItemTypesCache ??= FetchWorkItemTypesAsync(ct));

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

    public Task<ProcessConfigurationData> GetProcessConfigurationAsync(CancellationToken ct = default) =>
        _processConfigCache ??= FetchProcessConfigurationAsync(ct);

    public Task<IReadOnlyList<FieldDefinition>> GetFieldDefinitionsAsync(CancellationToken ct = default) =>
        _fieldDefinitionsCache ??= FetchFieldDefinitionsAsync(ct);

    public Task<IReadOnlyList<TeamIteration>> GetTeamIterationsAsync(CancellationToken ct = default) =>
        _teamIterationsCache ??= FetchTeamIterationsAsync(ct);

    public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(
        string workItemTypeName,
        CancellationToken ct = default)
    {
        if (!_processRulesCache.TryGetValue(workItemTypeName, out var rulesTask))
        {
            rulesTask = FetchProcessRulesAsync(workItemTypeName, ct);
            _processRulesCache[workItemTypeName] = rulesTask;
        }

        return rulesTask;
    }

    public Task<FormLayout?> GetFormLayoutAsync(
        string workItemTypeName,
        CancellationToken ct = default)
    {
        if (!_formLayoutCache.TryGetValue(workItemTypeName, out var layoutTask))
        {
            layoutTask = FetchFormLayoutAsync(workItemTypeName, ct);
            _formLayoutCache[workItemTypeName] = layoutTask;
        }

        return layoutTask;
    }

    public Task<IReadOnlyList<ProcessTypeField>?> GetTypeFieldsAsync(
        string workItemTypeName,
        CancellationToken ct = default)
    {
        if (!_processTypeFieldsCache.TryGetValue(workItemTypeName, out var fieldsTask))
        {
            fieldsTask = FetchProcessTypeFieldsAsync(workItemTypeName, ct);
            _processTypeFieldsCache[workItemTypeName] = fieldsTask;
        }

        return fieldsTask;
    }

    public async Task<IReadOnlyList<(string Path, bool IncludeChildren)>> GetTeamAreaPathsAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/{Uri.EscapeDataString(_team)}/_apis/work/teamsettings/teamfieldvalues?api-version={AdoApiVersions.TeamFieldValues}";
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
            var url = $"https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version={AdoApiVersions.Profile}";
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

    private async Task<AdoWorkItemTypeListResponse?> FetchWorkItemTypesAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/workitemtypes?api-version={AdoApiVersions.WorkItemTypes}";
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoWorkItemTypeListResponse, ct);
    }

    private async Task<IReadOnlyList<ProcessRule>> FetchProcessRulesAsync(
        string workItemTypeName,
        CancellationToken ct)
    {
        var processTemplate = await (_processTemplateCache ??= FetchProcessTemplateAsync(ct));
        var workItemTypes = await (_workItemTypesCache ??= FetchWorkItemTypesAsync(ct));
        var workItemType = workItemTypes?.Value?.FirstOrDefault(type =>
            !type.IsDisabled &&
            string.Equals(type.Name, workItemTypeName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(processTemplate?.TemplateTypeId) ||
            string.IsNullOrWhiteSpace(workItemType?.ReferenceName))
        {
            return Array.Empty<ProcessRule>();
        }

        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processTemplate.TemplateTypeId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(workItemType.ReferenceName)}/rules?api-version={AdoApiVersions.ProcessRules}";
        using var response = await SendAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(
            stream,
            TwigJsonContext.Default.AdoProcessRuleListResponse,
            ct);

        if (result?.Value is null)
            return Array.Empty<ProcessRule>();

        return result.Value.Select(rule => new ProcessRule(
            rule.Conditions?.Select(condition => new RuleCondition(
                condition.ConditionType ?? string.Empty,
                condition.Field ?? string.Empty,
                condition.Value)).ToList() ?? [],
            rule.Actions?.Select(action => new RuleAction(
                action.ActionType ?? string.Empty,
                action.TargetField ?? string.Empty,
                action.Value)).ToList() ?? [],
            rule.IsDisabled)).ToList();
    }

    /// <summary>
    /// Fetches and parses the server-defined form layout for one work item type.
    /// </summary>
    /// <remarks>
    /// Reuses the same process-template + work-item-type resolution as
    /// <see cref="FetchProcessRulesAsync"/>: the layout endpoint is process-scoped and
    /// keyed by the type's REFERENCE name, not its display name.
    /// <para>
    /// Returns <c>null</c> rather than an empty layout when the process or type cannot be
    /// resolved, or when the server does not serve a layout. Those are different facts
    /// from "this type has a layout with no pages in it", and the caller reports them
    /// differently — whether stock processes serve a layout at all is unverified, and
    /// collapsing the two would hide the answer.
    /// </para>
    /// </remarks>
    private async Task<FormLayout?> FetchFormLayoutAsync(
        string workItemTypeName,
        CancellationToken ct)
    {
        var processTemplate = await (_processTemplateCache ??= FetchProcessTemplateAsync(ct));
        var workItemTypes = await (_workItemTypesCache ??= FetchWorkItemTypesAsync(ct));
        var workItemType = workItemTypes?.Value?.FirstOrDefault(type =>
            !type.IsDisabled &&
            string.Equals(type.Name, workItemTypeName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(processTemplate?.TemplateTypeId) ||
            string.IsNullOrWhiteSpace(workItemType?.ReferenceName))
        {
            return null;
        }

        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processTemplate.TemplateTypeId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(workItemType.ReferenceName)}/layout?api-version={AdoApiVersions.ProcessLayout}";

        AdoFormLayoutResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream,
                TwigJsonContext.Default.AdoFormLayoutResponse,
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException)
        {
            // The process serves no layout for this type. A real answer, not a failure.
            return null;
        }

        if (result?.Pages is null)
            return null;

        return new FormLayout(
            workItemType.ReferenceName,
            processTemplate.TemplateTypeId,
            result.Pages.Select(MapLayoutPage).ToList());
    }

    /// <summary>
    /// Fetches the fields belonging to ONE work item type from the process-scoped per-type
    /// fields route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This is the route the per-type field list must come from.</b>
    /// <see cref="FetchFieldDefinitionsAsync"/> reads <c>_apis/wit/fields</c>, which is
    /// PROJECT-wide and identical for every type — handing it to a per-type view reports
    /// fields a type does not carry. That defect is what this method exists to fix at the
    /// source (AB#234).
    /// </para>
    /// <para>
    /// Reuses the same process-template + work-item-type resolution as
    /// <see cref="FetchProcessRulesAsync"/> and <see cref="FetchFormLayoutAsync"/>: the
    /// route is process-scoped and keyed by the type's REFERENCE name, not its display
    /// name. Sending the display name 404s against a real server.
    /// </para>
    /// <para>
    /// 🔴 The api-version is load-bearing and is named from
    /// <see cref="AdoApiVersions.ProcessWorkItemTypeFields"/>. At the neighbouring preview
    /// version this URL returns the same COUNT of rows with a disjoint attribute set that
    /// carries neither <c>required</c> nor <c>defaultValue</c> — so a version slip is
    /// invisible in the row count and shows up only as silently blank data.
    /// </para>
    /// <para>
    /// Returns <c>null</c> rather than an empty list when the process or type cannot be
    /// resolved, or when the route does not answer. 🔴 That distinction is load-bearing on
    /// this family of routes specifically: a 404 from them arrives with a COUNT-SHAPED
    /// body (<c>{"count":1,"value":{"Message":…}}</c>), which is exactly the shape of a
    /// successful thin response. Collapsing "could not ask" into "this type has no fields"
    /// would launder a failed call into a confident wrong answer — the failure mode that
    /// produced the original bug report about this endpoint family.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ProcessTypeField>?> FetchProcessTypeFieldsAsync(
        string workItemTypeName,
        CancellationToken ct)
    {
        var processTemplate = await (_processTemplateCache ??= FetchProcessTemplateAsync(ct));
        var workItemTypes = await (_workItemTypesCache ??= FetchWorkItemTypesAsync(ct));
        var workItemType = workItemTypes?.Value?.FirstOrDefault(type =>
            !type.IsDisabled &&
            string.Equals(type.Name, workItemTypeName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(processTemplate?.TemplateTypeId) ||
            string.IsNullOrWhiteSpace(workItemType?.ReferenceName))
        {
            return null;
        }

        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processTemplate.TemplateTypeId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(workItemType.ReferenceName)}" +
            $"/fields?api-version={AdoApiVersions.ProcessWorkItemTypeFields}";

        AdoProcessTypeFieldListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream,
                TwigJsonContext.Default.AdoProcessTypeFieldListResponse,
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException)
        {
            // The route does not answer for this process/type. Not "no fields".
            return null;
        }
        catch (JsonException)
        {
            // A count-shaped error envelope carries an OBJECT where the field array
            // belongs. Deserializing it fails here rather than yielding an empty list,
            // and that is the correct outcome: it is a failure, not thin data.
            return null;
        }

        if (result?.Value is null)
            return null;

        var fields = new List<ProcessTypeField>(result.Value.Count);
        foreach (var f in result.Value)
        {
            // referenceName is the field's identity and the only attribute a caller can
            // match on across processes. A row without one is unusable, not a default.
            if (string.IsNullOrWhiteSpace(f.ReferenceName))
                continue;

            fields.Add(new ProcessTypeField(
                f.ReferenceName,
                f.Name ?? f.ReferenceName,
                f.Type ?? "string",
                // Absent and empty are both "no default" on this route; the server omits
                // the attribute entirely on the ~97% of rows that have none.
                string.IsNullOrEmpty(f.DefaultValue) ? null : f.DefaultValue,
                // Absent `required` means not-required-unconditionally. It does NOT mean
                // not-required: see ProcessTypeField's remarks and the rules route.
                f.Required ?? false,
                // Carried verbatim: 'custom' | 'inherited' | 'system'. Twig does not
                // reinterpret the server's vocabulary here.
                f.Customization ?? string.Empty,
                f.IsLocked,
                f.Description ?? string.Empty));
        }

        return fields;
    }

    private static LayoutPage MapLayoutPage(AdoLayoutPageResponse page) => new(
        page.Id ?? string.Empty,
        page.Label ?? string.Empty,
        page.PageType ?? "custom",
        // Absent 'visible' means visible: ADO omits the flag on the common case.
        page.Visible ?? true,
        page.IsContribution,
        // Sections (the web form's COLUMNS) are preserved, not flattened. Merging them
        // into one column is a rendering choice and stays with the renderer — see
        // FormLayout's remarks, and LayoutPage.AllGroups for the merged projection.
        (page.Sections ?? []).Select(MapLayoutSection).ToList());

    private static LayoutSection MapLayoutSection(AdoLayoutSectionResponse section) => new(
        section.Id ?? string.Empty,
        (section.Groups ?? [])
            .OrderBy(g => g.Order ?? int.MaxValue)
            .Select(MapLayoutGroup)
            .ToList());

    private static LayoutGroup MapLayoutGroup(AdoLayoutGroupResponse group) => new(
        group.Id ?? string.Empty,
        group.Label ?? string.Empty,
        group.Visible ?? true,
        group.IsContribution,
        (group.Controls ?? [])
            .OrderBy(c => c.Order ?? int.MaxValue)
            .Select(MapLayoutControl)
            .ToList());

    private static LayoutControl MapLayoutControl(AdoLayoutControlResponse control) => new(
        control.Id ?? string.Empty,
        control.Label ?? string.Empty,
        control.ControlType ?? string.Empty,
        control.ReadOnly,
        control.Visible ?? true,
        control.IsContribution);


    private async Task<ProcessConfigurationData> FetchProcessConfigurationAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/work/processconfiguration?api-version={AdoApiVersions.ProcessConfiguration}";
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

    private async Task<IReadOnlyList<FieldDefinition>> FetchFieldDefinitionsAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/fields?api-version={AdoApiVersions.Fields}";
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

    public async Task<AreaTreeNode> GetAreaTreeAsync(CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/classificationnodes/areas?$depth=10&api-version={AdoApiVersions.ClassificationNodes}";
        using var response = await SendAsync(url, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var node = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoClassificationNodeResponse, ct);

        return MapNode(node);
    }

    private static AreaTreeNode MapNode(AdoClassificationNodeResponse? node)
    {
        if (node is null)
            return new AreaTreeNode("", "", []);

        var name = node.Name ?? "";
        // ADO returns paths like "\ProjectName\Area\SubArea" — normalize to backslash-separated without leading slash
        var path = (node.Path ?? name).TrimStart('\\').Replace("\\Area", "").TrimStart('\\');
        if (string.IsNullOrEmpty(path))
            path = name;

        var children = node.Children is { Count: > 0 }
            ? node.Children.Select(MapNode).ToList()
            : (IReadOnlyList<AreaTreeNode>)[];

        return new AreaTreeNode(name, path, children);
    }

    private async Task<IReadOnlyList<TeamIteration>> FetchTeamIterationsAsync(CancellationToken ct)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/{Uri.EscapeDataString(_team)}/_apis/work/teamsettings/iterations?api-version={AdoApiVersions.TeamIterations}";
        using var response = await SendAsync(url, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoIterationListResponse, ct);

        if (result?.Value is null || result.Value.Count == 0)
            return Array.Empty<TeamIteration>();

        var iterations = new List<TeamIteration>(result.Value.Count);
        foreach (var item in result.Value)
        {
            if (item.Path is null)
                continue;

            DateTimeOffset? startDate = ParseDate(item.Attributes?.StartDate);
            DateTimeOffset? endDate = ParseDate(item.Attributes?.FinishDate);

            iterations.Add(new TeamIteration(item.Path, startDate, endDate));
        }

        return iterations;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        try
        {
            return await SendCoreAsync(url, ct);
        }
        catch (Exception ex) when (AdoErrorHandler.IsAuthChallenge(ex))
        {
            _authProvider.InvalidateToken();
            return await SendCoreAsync(url, ct);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var token = await _authProvider.GetAccessTokenAsync(ct);
        AdoErrorHandler.ApplyAuthHeader(request, token);

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
}
