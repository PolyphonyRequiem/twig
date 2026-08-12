using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Fetches everything <c>twig process description</c> needs, live, from ADO.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Deliberately NOT part of <see cref="AdoIterationService"/>, and it must not become
/// part of it.</b> That service memoizes every route it calls, which is correct for the
/// commands it serves and WRONG here: no caching of any kind is a ruling, because a stale
/// description is a wrong description and the entire artifact is a truth claim about a
/// process at a moment in time. A cache would trade away the single property the file
/// exists to have, to save time on a command run rarely and deliberately. This class holds
/// no state between calls for exactly that reason.
/// </para>
/// <para>
/// 🔴 <b>Nothing here writes to twig's local store either.</b> That store is scoped to the
/// workspace's own project, and a description may describe a FOREIGN process — which is the
/// whole point of comparing two. Ingesting one would poison the store.
/// </para>
/// <para>
/// 🔴 <b>The process is resolved BY ID VIA THE PROJECT, never by name.</b> A live, verified
/// trap: the project named "Twig" does not run on the process named "Twig" — that process
/// owns zero projects. Resolving by name silently describes the wrong process. The
/// resolution below goes project → <c>capabilities.processTemplate.templateTypeId</c> →
/// process routes, and never looks a process up by its name.
/// </para>
/// <para>
/// 🔴 <b>A 404 on the process route family arrives with a COUNT-SHAPED body</b>
/// (<c>{"count":1,"value":{"Message":…}}</c>), which is exactly the shape of a thin
/// success. Every fetch below therefore returns <c>null</c> on failure rather than an empty
/// collection: laundering a failed call into "this type has nothing" is the failure mode
/// that produced the original bug report about this endpoint family.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> Implementation Decisions
/// 6, 7 and 11, and the caching ruling under Solution.
/// </para>
/// </remarks>
internal sealed class AdoProcessDescriptionSource : IProcessDescriptionSource
{
    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;

    public AdoProcessDescriptionSource(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project)
    {
        if (string.IsNullOrWhiteSpace(orgUrl))
            throw new InvalidOperationException("Organization is not configured. Run 'twig init --org <org> --project <project>' first.");
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("Project is not configured. Run 'twig init --org <org> --project <project>' first.");

        _http = httpClient;
        _authProvider = authProvider;
        _orgUrl = AdoRestClient.NormalizeOrgUrl(orgUrl);
        _project = project;
    }

    /// <summary>
    /// The routes this source calls and the version pinned for each, for the document
    /// header.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="AdoApiVersions"/> rather than restated, so a version recorded in
    /// a document cannot drift from the version actually called. A header line claiming a
    /// version the fetch did not use would be worse than no header line at all.
    /// </remarks>
    internal static IReadOnlyList<ProcessDescriptionRouteVersion> RouteVersions =>
    [
        new ProcessDescriptionRouteVersion(
            "core/projects/{project}", AdoApiVersions.Projects),
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/workItemTypes", AdoApiVersions.ProcessWorkItemTypes),
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/workItemTypes/{ref}/fields", AdoApiVersions.ProcessWorkItemTypeFields),
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/workItemTypes/{ref}/states", AdoApiVersions.ProcessWorkItemTypeStates),
        new ProcessDescriptionRouteVersion(
            "wit/workitemtypes?$expand=all", AdoApiVersions.ProjectWorkItemTypesExpanded),
    ];

    public async Task<ProcessIdentity?> GetProcessIdentityAsync(CancellationToken ct = default)
    {
        // 🔴 project -> processTemplate id. Never a lookup by process NAME.
        var url = $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}" +
            $"?includeCapabilities=true&api-version={AdoApiVersions.Projects}";

        AdoProjectWithCapabilitiesResponse? project;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            project = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoProjectWithCapabilitiesResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        var template = project?.Capabilities?.ProcessTemplate;
        if (string.IsNullOrWhiteSpace(template?.TemplateTypeId))
            return null;

        return new ProcessIdentity(
            _orgUrl,
            _project,
            template.TemplateTypeId,
            // The name is for the reader's orientation only and is never resolved on. An
            // absent one is not a failure — the id is the identity that matters.
            template.TemplateName ?? string.Empty);
    }

    public async Task<IReadOnlyList<ProcessTypeSummary>?> GetTypesAsync(CancellationToken ct = default)
    {
        var identity = await GetProcessIdentityAsync(ct);
        if (identity is null)
            return null;

        // 🔴 preview.2 buys referenceName + customization. At preview.1 the same URL returns
        // id + class instead and carries neither, so a version slip loses stable identity
        // AND authored-vs-inherited without changing the row count.
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(identity.ProcessId)}" +
            $"/workItemTypes?api-version={AdoApiVersions.ProcessWorkItemTypes}";

        AdoProcessWorkItemTypeListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoProcessWorkItemTypeListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException)
        {
            // A count-shaped 404 envelope carries an OBJECT where the array belongs, so it
            // fails to deserialize here rather than yielding an empty list. That is the
            // correct outcome: it is a failure, not a process with no types.
            return null;
        }

        if (result?.Value is null)
            return null;

        var types = new List<ProcessTypeSummary>(result.Value.Count);
        foreach (var type in result.Value)
        {
            // referenceName is the identity everything else is fetched by and the only
            // attribute two processes can be matched on. A row without one is unusable.
            if (string.IsNullOrWhiteSpace(type.ReferenceName))
                continue;

            types.Add(new ProcessTypeSummary(
                type.ReferenceName,
                type.Name ?? type.ReferenceName,
                type.Description ?? string.Empty,
                // Verbatim: 'custom' | 'inherited' | 'system'.
                type.Customization ?? string.Empty,
                string.IsNullOrWhiteSpace(type.Inherits) ? null : type.Inherits,
                type.IsDisabled));
        }

        return types;
    }

    public async Task<ProcessTypeDetail?> GetTypeDetailAsync(
        string typeReferenceName,
        string? inheritsFrom = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(typeReferenceName))
            return null;

        var identity = await GetProcessIdentityAsync(ct);
        if (identity is null)
            return null;

        // The three fetches for one type are independent GETs and run concurrently. This is
        // the ruled latency mitigation; ordering is not taken from completion order here or
        // anywhere downstream — the assembler sorts.
        var fieldsTask = FetchFieldsAsync(identity.ProcessId, typeReferenceName, ct);
        var statesTask = FetchStatesAsync(identity.ProcessId, typeReferenceName, ct);

        // States are awaited before transitions because the transitions fallback needs them:
        // a parent's transitions are only trustworthy for a derived type whose own states the
        // parent's workflow actually covers.
        var fields = await fieldsTask;
        var states = await statesTask;

        var transitions = await FetchTransitionsAsync(
            typeReferenceName, inheritsFrom, states, ct);

        // If every part failed, the type could not be described at all — report that rather
        // than a document row claiming an empty type.
        if (fields is null && states is null && transitions is null)
            return null;

        // 🔴 A PARTIAL failure is named, not swallowed. Otherwise "the fields call 404'd" and
        // "this type has no fields" render identically, and the second is a confident wrong
        // answer — the exact failure this route family's count-shaped 404 invites.
        var unfetched = new List<string>(3);
        if (fields is null) unfetched.Add("fields");
        if (states is null) unfetched.Add("states");
        if (transitions is null) unfetched.Add("transitions");

        return new ProcessTypeDetail(fields ?? [], states ?? [], transitions ?? [], unfetched);
    }

    private async Task<IReadOnlyList<ProcessTypeField>?> FetchFieldsAsync(
        string processId,
        string typeReferenceName,
        CancellationToken ct)
    {
        // 🔴 preview.2 buys required, defaultValue and customization. The same URL at
        // preview.1 returns a disjoint attribute set carrying none of the three, with
        // IDENTICAL counts — so a version slip is invisible in the row count and shows up
        // only as silently blank data.
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(typeReferenceName)}" +
            $"/fields?api-version={AdoApiVersions.ProcessWorkItemTypeFields}";

        AdoProcessTypeFieldListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoProcessTypeFieldListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        var fields = new List<ProcessTypeField>(result.Value.Count);
        foreach (var f in result.Value)
        {
            if (string.IsNullOrWhiteSpace(f.ReferenceName))
                continue;

            fields.Add(new ProcessTypeField(
                f.ReferenceName,
                f.Name ?? f.ReferenceName,
                f.Type ?? "string",
                string.IsNullOrEmpty(f.DefaultValue) ? null : f.DefaultValue,
                // 🔴 UNCONDITIONAL requiredness only. A field made mandatory by a rule reads
                // as false here. The merge with the rules route is AB#236's work and the
                // document declares the gap until then.
                f.Required ?? false,
                f.Customization ?? string.Empty,
                f.IsLocked,
                f.Description ?? string.Empty));
        }

        return fields;
    }

    private async Task<IReadOnlyList<ProcessTypeState>?> FetchStatesAsync(
        string processId,
        string typeReferenceName,
        CancellationToken ct)
    {
        // 🔴 GA 7.1 here, NOT preview.2 — the server rejects preview.2 on this route with
        // VssVersionOutOfRangeException. See AdoApiVersions.ProcessWorkItemTypeStates.
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(typeReferenceName)}" +
            $"/states?api-version={AdoApiVersions.ProcessWorkItemTypeStates}";

        AdoProcessStateListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoProcessStateListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        var states = new List<ProcessTypeState>(result.Value.Count);
        foreach (var s in result.Value)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
                continue;

            states.Add(new ProcessTypeState(
                s.Name,
                s.StateCategory ?? string.Empty,
                s.Order ?? 0,
                s.Color ?? string.Empty,
                s.CustomizationType ?? string.Empty,
                // Absent 'hidden' means visible; the server omits it on the common case.
                s.Hidden ?? false));
        }

        return states;
    }

    /// <remarks>
    /// 🔴 This is the one fetch that leaves the modern process API, and it is forced: the
    /// process-scoped transitions routes 404 at every version and no <c>$expand</c> on the
    /// process type list carries transitions. Deriving them from the state list is NOT an
    /// option — 4 of 20 types probed are not fully connected, so derivation would report
    /// transitions that do not exist. See
    /// <c>AdoApiVersions.ProjectWorkItemTypesExpanded</c>.
    /// <para>
    /// 🔴 <b>The two routes do not agree on a derived type's name.</b> A type derived from a
    /// system one is <c>Niflheim.Epic</c> on the process routes and
    /// <c>Microsoft.VSTS.WorkItemTypes.Epic</c> here. Matching on the process name alone
    /// finds nothing and silently yields ZERO transitions — observed live on exactly the
    /// three derived types in this process, and invisible without checking, because "no
    /// transitions" is a plausible-looking answer. So the parent reference name is tried as
    /// a fallback.
    /// </para>
    /// <para>
    /// This route returns MORE types than the process reports, so it is filtered down to the
    /// requested type rather than trusted as the roster.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ProcessTypeTransition>?> FetchTransitionsAsync(
        string typeReferenceName,
        string? inheritsFrom,
        IReadOnlyList<ProcessTypeState>? ownStates,
        CancellationToken ct)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/workitemtypes" +
            $"?$expand=all&api-version={AdoApiVersions.ProjectWorkItemTypesExpanded}";

        AdoWitTypeTransitionsListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoWitTypeTransitionsListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        var type = result.Value.FirstOrDefault(t => string.Equals(
            t.ReferenceName, typeReferenceName, StringComparison.OrdinalIgnoreCase));

        // 🔴 Fallback for DERIVED types, which this route names by their PARENT reference
        // name. Only consulted when the direct match failed, so it can never shadow a type
        // this route does report under its own name.
        //
        // 🔴 KNOWN LIMITATION, stated rather than hidden: if a derived type has CUSTOMISED its
        // workflow (added or removed a state, or changed which transitions are allowed), this
        // reports the PARENT's transitions for it. There is no better source — the modern
        // process API serves no transitions route at any version — so the choice is between
        // the parent's answer and none at all. The guard below narrows it: a parent answer is
        // only accepted when the derived type's own states are a subset of the states the
        // parent's transitions mention, so a type that genuinely diverged reports its
        // transitions as unfetched instead of borrowing a wrong set.
        var borrowedFromParent = false;
        if (type is null && !string.IsNullOrWhiteSpace(inheritsFrom))
        {
            type = result.Value.FirstOrDefault(t => string.Equals(
                t.ReferenceName, inheritsFrom, StringComparison.OrdinalIgnoreCase));
            borrowedFromParent = type is not null;
        }

        // The type exists in the process but this project-scoped route does not report it.
        // Not "no transitions" — we could not ask.
        if (type?.Transitions is null)
            return null;

        var transitions = new List<ProcessTypeTransition>();
        foreach (var (fromState, destinations) in type.Transitions)
        {
            if (destinations is null)
                continue;

            foreach (var destination in destinations)
            {
                if (string.IsNullOrWhiteSpace(destination.To))
                    continue;

                // 🔴 An empty fromState is the INITIAL transition — what state a new work
                // item enters. Carried, not dropped: it is a real and useful fact.
                transitions.Add(new ProcessTypeTransition(fromState ?? string.Empty, destination.To));
            }
        }

        // 🔴 The borrowed-from-parent guard. A derived type that customised its workflow must
        // not be handed its parent's transitions as though they were its own — that would be
        // a confident wrong answer about the thing the reader is comparing. If the type
        // declares a state the parent's transitions never mention, the workflows have
        // genuinely diverged and "we could not read this" is the honest report.
        if (borrowedFromParent && ownStates is { Count: > 0 })
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transition in transitions)
            {
                if (transition.FromState.Length > 0)
                    covered.Add(transition.FromState);
                covered.Add(transition.ToState);
            }

            if (ownStates.Any(state => !covered.Contains(state.Name)))
                return null;
        }

        return transitions;
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
