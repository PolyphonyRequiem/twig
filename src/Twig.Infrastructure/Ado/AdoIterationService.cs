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
    /// <summary>
    /// The marker ADO puts in the 400 it answers a LOCKED work item type's layout route
    /// with. Matched literally so the catch stays narrow — see
    /// <see cref="FetchFormLayoutAsync"/>.
    /// </summary>
    private const string LockedFormLayoutMarker = "VS403115";

    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string _team;

    // Lazy-initialized caches — safe because CLI is single-threaded
    private Task<AdoWorkItemTypeListResponse?>? _workItemTypesCache;
    // 🔴 The PROCESS's roster, distinct from _workItemTypesCache's PROJECT roster. They
    // disagree on the reference name of every inherited type — see FetchFormLayoutAsync.
    private Task<AdoProcessWorkItemTypeListResponse?>? _processWorkItemTypesCache;
    private Task<AdoProcessTemplate?>? _processTemplateCache;
    private Task<ProcessConfigurationData>? _processConfigCache;
    private Task<IReadOnlyList<FieldDefinition>>? _fieldDefinitionsCache;
    private Task<IReadOnlyList<TeamIteration>>? _teamIterationsCache;
    private readonly Dictionary<string, Task<IReadOnlyList<ProcessRule>>> _processRulesCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<FormLayoutResult>> _formLayoutCache =
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

    public Task<FormLayoutResult> GetFormLayoutAsync(
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
    /// <para>
    /// 🔴 <b>Resolves against the PROCESS's own type roster, not the project's (AB#247).</b>
    /// The two rosters are different, and they give the SAME type different reference
    /// names. Verified live against the <c>Niflheim</c> process: the project route
    /// (<c>_apis/wit/workitemtypes</c>) lists 20 types and reports <c>Task</c> as
    /// <c>Microsoft.VSTS.WorkItemTypes.Task</c> — the STOCK parent type — while the process
    /// route lists the process's own 14 and reports it as <c>Niflheim.Task</c>. Three types
    /// collide this way (<c>Task</c>, <c>Issue</c>, <c>Epic</c>: the ones this process
    /// inherits and re-parents).
    /// </para>
    /// <para>
    /// Resolving through the project roster therefore fetched the PARENT type's layout and
    /// labelled it with the parent's identity. The two forms happen to be identical today
    /// because nothing has customized the child forms — so the defect was invisible in the
    /// output — but the moment one is edited, this would silently serve the stock form
    /// while claiming to describe the process's type. That is the trap Implementation
    /// Decision 11 records — <i>"the project named Twig does not run on the process named
    /// Twig"</i> (<c>docs/specs/process-description.spec.md</c>, branch
    /// <c>docs/process-descriptor-map</c>) — one layer down, and it is why the description
    /// resolves by process reference name.
    /// </para>
    /// <para>
    /// 🔴 <b>The stock PARENT type is not reachable through this method.</b> Only rows of the
    /// process roster are matched; a parent's reference name is not one, and the roster's
    /// <c>inherits</c> field is deliberately not consulted. Naming the parent in full reports
    /// no layout. That is accepted (AB#247, ticket 1004): this verb describes the process's
    /// form, and the parent's form is a different question.
    /// </para>
    /// <para>
    /// 🔴 <b>Both name forms are accepted</b>, matching the sibling <c>process description</c>
    /// verb: the display name (<c>Task</c>) and the process reference name
    /// (<c>Niflheim.Task</c>) reach the same type. Reference name is tried FIRST, because it
    /// is the stable identity and display names lie; the display-name pass is the
    /// convenience. Measured against the live org, no display name collides with any
    /// reference name and no name is duplicated within the roster, so the two passes cannot
    /// currently disagree — and ordering them makes the answer defined if that ever changes.
    /// </para>
    /// <para>
    /// Returns <see cref="FormLayoutResult.Unavailable"/> rather than an empty layout when
    /// the process or type cannot be resolved, or when the server does not serve a layout.
    /// Those are different facts from "this type has a layout with no pages in it", and the
    /// caller reports them differently — whether stock processes serve a layout at all is
    /// unverified, and collapsing the two would hide the answer.
    /// </para>
    /// </remarks>
    private async Task<FormLayoutResult> FetchFormLayoutAsync(
        string workItemTypeName,
        CancellationToken ct)
    {
        var processTemplate = await (_processTemplateCache ??= FetchProcessTemplateAsync(ct));
        if (string.IsNullOrWhiteSpace(processTemplate?.TemplateTypeId))
            return new FormLayoutResult.Unavailable();

        var processTypes = await (_processWorkItemTypesCache ??=
            FetchProcessWorkItemTypesAsync(processTemplate.TemplateTypeId, ct));

        var referenceName = ResolveProcessTypeReferenceName(processTypes, workItemTypeName);
        if (referenceName is null)
            return new FormLayoutResult.Unavailable();

        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processTemplate.TemplateTypeId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(referenceName)}/layout?api-version={AdoApiVersions.ProcessLayout}";

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
            return new FormLayoutResult.Unavailable();
        }
        // 🔴 A LOCKED system type answers this route with 400 VS403115 ("you cannot modify
        // form layout information … as these work item types are locked"), NOT a 404 — so
        // the AdoNotFoundException arm above never sees it, and without this catch the raw
        // server error propagated out and the command exited 1 with no output (AB#247).
        //
        // 🔴 NARROWED to the VS403115 marker, not every 400 — the same discipline the
        // description's FetchLayoutAsync already applies, and for the same reason: a
        // malformed api-version, a bad reference-name escape, or a future validation error
        // must NOT become a silent degraded success. A broad `catch (AdoBadRequestException)`
        // here would be the exception-swallow-too-broad regression, not a fix.
        //
        // Reported as its own Locked arm rather than Unavailable: the process ANSWERED, and
        // its answer is a durable fact about the type ("never, for this one") rather than an
        // absence this method cannot explain.
        catch (AdoBadRequestException ex)
            when (ex.Message.Contains(LockedFormLayoutMarker, StringComparison.Ordinal))
        {
            return new FormLayoutResult.Locked(referenceName);
        }

        // 🔴 Absence of `pages` means this is not a layout payload at all — treat it as a
        // failed fetch, never as a form that has no pages.
        if (result?.Pages is null)
            return new FormLayoutResult.Unavailable();

        return new FormLayoutResult.Served(new FormLayout(
            referenceName,
            processTemplate.TemplateTypeId,
            result.Pages.Select(MapLayoutPage).ToList())
        {
            // 🔴 Carried, not discarded (AB#247). These arrive in the SAME response as
            // `pages` — they were already being deserialized and then thrown away, so the
            // command's rendering of "the form" was missing every control a person sees at
            // the top of a work item (state, reason, assigned-to, area and iteration path,
            // history, links, attachments — 9 per type in this process).
            SystemControls = (result.SystemControls ?? [])
                .OrderBy(c => c.Order ?? int.MaxValue)
                .Select(MapLayoutControl)
                .ToList(),
        });
    }

    /// <summary>
    /// Fetches the PROCESS's own work item type roster — the list the layout route is keyed
    /// by.
    /// </summary>
    /// <remarks>
    /// 🔴 Distinct from <see cref="FetchWorkItemTypesAsync"/>, which reads the PROJECT's
    /// roster. See <see cref="FetchFormLayoutAsync"/>'s remarks: the two disagree on the
    /// reference name of every inherited type, and only this one names the types the
    /// process actually owns.
    /// <para>
    /// The api-version is load-bearing and named from
    /// <see cref="AdoApiVersions.ProcessWorkItemTypes"/> — at the neighbouring preview
    /// version the same URL returns id and class instead of referenceName and
    /// customization, so a version slip loses the identity this resolution depends on
    /// without changing the row count.
    /// </para>
    /// </remarks>
    private async Task<AdoProcessWorkItemTypeListResponse?> FetchProcessWorkItemTypesAsync(
        string processId,
        CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processId)}" +
            $"/workItemTypes?api-version={AdoApiVersions.ProcessWorkItemTypes}";

        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(
                stream,
                TwigJsonContext.Default.AdoProcessWorkItemTypeListResponse,
                ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException)
        {
            // A count-shaped 404 envelope carries an OBJECT where the array belongs, so it
            // fails to deserialize rather than yielding an empty list. That is the correct
            // outcome: a failure, not a process with no types.
            return null;
        }
    }

    /// <summary>
    /// Resolves a caller's type argument to the process's reference name, accepting either
    /// the reference name itself or the display name.
    /// </summary>
    /// <remarks>
    /// 🔴 Reference name is matched FIRST and display name second, deliberately. Reference
    /// name is the stable identity; the display-name pass exists so the layout verb accepts
    /// the same argument a person would type. Ordering them means a future roster in which
    /// one type's display name equals another's reference name resolves to the stable
    /// identity rather than to whichever row was enumerated first. No such collision exists
    /// in the measured org.
    /// <para>
    /// Disabled types are excluded from both passes — a disabled type is not part of the
    /// process's live form surface, matching the existing rules and fields resolution.
    /// </para>
    /// </remarks>
    private static string? ResolveProcessTypeReferenceName(
        AdoProcessWorkItemTypeListResponse? processTypes,
        string workItemTypeName)
    {
        var candidates = processTypes?.Value?
            .Where(type => !type.IsDisabled && !string.IsNullOrWhiteSpace(type.ReferenceName))
            .ToList();

        if (candidates is null || candidates.Count == 0)
            return null;

        var byReferenceName = candidates.FirstOrDefault(type => string.Equals(
            type.ReferenceName, workItemTypeName, StringComparison.OrdinalIgnoreCase));
        if (byReferenceName is not null)
            return byReferenceName.ReferenceName;

        var byDisplayName = candidates.FirstOrDefault(type => string.Equals(
            type.Name, workItemTypeName, StringComparison.OrdinalIgnoreCase));

        return byDisplayName?.ReferenceName;
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
