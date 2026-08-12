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
    /// <summary>
    /// The ADO error code a LOCKED work item type answers the layout route with.
    /// </summary>
    /// <remarks>
    /// 🔴 The whole reason the layout fetch catches a 400 at all. <c>TestCase</c>,
    /// <c>TestPlan</c> and <c>TestSuite</c> are locked in this process and answer
    /// <i>"you cannot modify form layout information for work item types … as these work item
    /// types are locked"</i> — a 400, where every other failure on this family is a 404 or a
    /// count-shaped body. Matched as a marker so the catch stays bounded to that ONE answer
    /// rather than swallowing every 400.
    /// </remarks>
    private const string LockedFormLayoutMarker = "VS403115";

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
        // 🔴 The SECOND source of requiredness (AB#236). Pinned 7.1, deliberately NOT
        // preview.2. preview.2 additionally carries `customizationType` per rule — the only
        // available filter for the ~54 inherited system rules on a derived type — but this
        // ticket does not report rules, it reads `makeRequired` actions off them, and those
        // are identical at both versions. Moving the constant would be a behaviour change to
        // the shipped `twig process rules` output bought for nothing here; it belongs to the
        // ticket that carries rules into the document (AB#238).
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/workItemTypes/{ref}/rules", AdoApiVersions.ProcessRules),
        new ProcessDescriptionRouteVersion(
            "wit/workitemtypes?$expand=all", AdoApiVersions.ProjectWorkItemTypesExpanded),
        // 🔴 The picklist association source (AB#237). ORG-scoped, not process-scoped: no
        // process route carries `isPicklist` or a picklist reference at any version, with or
        // without $expand. Read for that ONE attribute and joined by reference name — never
        // presented as a type's field list, which is the defect this whole feature fixes.
        new ProcessDescriptionRouteVersion(
            "wit/fields", AdoApiVersions.Fields),
        new ProcessDescriptionRouteVersion(
            "work/processes/lists/{listId}", AdoApiVersions.ProcessLists),
        // 🔴 The behaviour MEMBERSHIP source (AB#238). Note the route segment:
        // `workItemTypesBehaviors`, not `workItemTypes/{ref}/behaviors` — the obvious one
        // returns an HTML 404 for every type, on both an inherited and a stock process.
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/workItemTypesBehaviors/{ref}/behaviors",
            AdoApiVersions.ProcessTypeBehaviors),
        // 🔴 The behaviour CATALOGUE (AB#238). Process-scoped, one call per run, fetched
        // solely to turn the membership route's bare GUID reference into a readable name.
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/behaviors", AdoApiVersions.ProcessBehaviors),
        // 🔴 The form LAYOUT (AB#238), at the same version the shipped `twig process layout`
        // command reads it at — so two surfaces describing the same form cannot disagree
        // because one of them was pinned somewhere else.
        new ProcessDescriptionRouteVersion(
            "work/processes/{processId}/workItemTypes/{ref}/layout", AdoApiVersions.ProcessLayout),
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

        // The fetches for one type are independent GETs and run concurrently. This is
        // the ruled latency mitigation; ordering is not taken from completion order here or
        // anywhere downstream — the assembler sorts.
        var fieldsTask = FetchFieldsAsync(identity.ProcessId, typeReferenceName, ct);
        var statesTask = FetchStatesAsync(identity.ProcessId, typeReferenceName, ct);
        // 🔴 The second requiredness source (AB#236). Keyed by the type's own PROCESS
        // reference name — this is a process route, so unlike the transitions fetch it needs
        // no parent-name fallback.
        var rulesTask = FetchRulesAsync(identity.ProcessId, typeReferenceName, ct);
        // 🔴 The behaviour MEMBERSHIP fetch (AB#238) — on `workItemTypesBehaviors`, not the
        // obvious `workItemTypes/{ref}/behaviors`, which 404s for every type.
        var behavioursTask = FetchBehavioursAsync(identity.ProcessId, typeReferenceName, ct);
        // 🔴 The form LAYOUT fetch (AB#238). Keyed by the type's own process reference name,
        // like the rules and fields calls; this is a process route, so no parent fallback.
        var layoutTask = FetchLayoutAsync(identity.ProcessId, typeReferenceName, ct);

        // 🔴 Gathered with WhenAll before any individual await. Awaiting them one at a time
        // means the FIRST fault propagates while the later tasks are never awaited, and their
        // exceptions become unobserved. The fetches swallow only AdoNotFoundException and
        // JsonException; offline, throttle and unexpected-response faults do propagate, so
        // this is reachable rather than theoretical.
        await Task.WhenAll(fieldsTask, statesTask, rulesTask, behavioursTask, layoutTask)
            .ConfigureAwait(false);

        // States are read before transitions because the transitions fallback needs them:
        // a parent's transitions are only trustworthy for a derived type whose own states the
        // parent's workflow actually covers.
        var fields = await fieldsTask;
        var states = await statesTask;
        var rules = await rulesTask;
        var behaviours = await behavioursTask;
        var layout = await layoutTask;

        var transitions = await FetchTransitionsAsync(
            typeReferenceName, inheritsFrom, states, ct);

        // If every part failed, the type could not be described at all — report that rather
        // than a document row claiming an empty type.
        if (fields is null && states is null && transitions is null && rules is null
            && behaviours is null && layout is null)
        {
            return null;
        }

        // 🔴 A PARTIAL failure is named, not swallowed. Otherwise "the fields call 404'd" and
        // "this type has no fields" render identically, and the second is a confident wrong
        // answer — the exact failure this route family's count-shaped 404 invites.
        var unfetched = new List<string>(6);
        // 🔴 A failed BEHAVIOURS call is named. Without the label, "we could not ask" reads as
        // "this type appears on no backlog level", which a reader would act on.
        if (behaviours is null) unfetched.Add("behaviours");
        if (fields is null) unfetched.Add("fields");
        // 🔴 A failed LAYOUT call is named rather than rendered as a form with no pages —
        // which would be the strongest possible positive claim built on a call that failed.
        if (layout is null) unfetched.Add("formLayout");
        // 🔴 A failed RULES call is named too. Requiredness is merged from it, so without the
        // label a reader would take "nothing is conditionally required" from a call that
        // never came back.
        if (rules is null) unfetched.Add("rules");
        if (states is null) unfetched.Add("states");
        if (transitions is null) unfetched.Add("transitions");

        return new ProcessTypeDetail(
            fields ?? [], states ?? [], transitions ?? [], unfetched, rules, behaviours, layout);
    }

    /// <summary>
    /// Which backlog levels one type belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>The route segment is <c>workItemTypesBehaviors</c>, not
    /// <c>workItemTypes/{ref}/behaviors</c>.</b> The obvious route returns an HTML 404 for
    /// every type on every arm — verified live 2026-08-11 and re-verified 2026-08-12. Note it
    /// is an HTML page rather than this family's usual count-shaped JSON envelope, so it
    /// arrives here as a JSON parse failure rather than as a clean deserialization.
    /// </para>
    /// <para>
    /// 🔴 <b>The count-shaped-body guard is the <c>behavior.id</c> check below, and it is
    /// deliberate rather than accidental.</b> This response deserializes into a
    /// <c>*ListResponse</c> whose <c>Value</c> is a <c>List&lt;T&gt;</c>, so a count-shaped
    /// error body — which puts an OBJECT where the array belongs — throws, the same accidental
    /// defence the sibling list fetches enjoy. But the ROWS are bare objects with no
    /// array-shaped member, so a row that deserialized clean while carrying no reference at all
    /// would become a membership of the empty-string behaviour: a confident claim about a
    /// backlog level that does not exist. A real row always carries <c>behavior.id</c>.
    /// </para>
    /// <para>
    /// Returns <c>null</c> on failure and never an empty list, so "the membership call failed"
    /// cannot be laundered into "this type belongs to no backlog level" — a claim a reader
    /// would act on, and which is TRUE for several types in this org, so the two are
    /// indistinguishable downstream unless the failure is named.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ProcessBehaviourMembership>?> FetchBehavioursAsync(
        string processId,
        string typeReferenceName,
        CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processId)}" +
            $"/workItemTypesBehaviors/{Uri.EscapeDataString(typeReferenceName)}" +
            $"/behaviors?api-version={AdoApiVersions.ProcessTypeBehaviors}";

        AdoTypeBehaviourListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoTypeBehaviourListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        var memberships = new List<ProcessBehaviourMembership>(result.Value.Count);
        foreach (var row in result.Value)
        {
            // 🔴 The row guard. A row with no reference names no backlog level, and carrying it
            // as a membership of "" would be a confident claim about a level that does not
            // exist. Skipped rather than failing the whole call: the other rows are still true.
            if (string.IsNullOrWhiteSpace(row.Behavior?.Id))
                continue;

            // 🔴 The NAME is left empty here and filled by the assembler's join against the
            // process-scoped catalogue. Resolving it in this method would mean one catalogue
            // fetch per type for a process-wide answer.
            memberships.Add(new ProcessBehaviourMembership(
                row.Behavior.Id, string.Empty, null, row.IsDefault));
        }

        return memberships;
    }

    /// <remarks>
    /// 🔴 The behaviour CATALOGUE (AB#238) — process-scoped, so ONE call per description run
    /// however many types are described. It exists only to name what the membership route
    /// references: a custom backlog level's reference name is a GUID, so a document carrying
    /// the membership edge alone would be true, unreadable, and worthless in a diff between two
    /// processes that gave the same level different ids.
    /// <para>
    /// Returns <c>null</c> on failure, never an empty list. An empty catalogue would claim the
    /// process defines no backlog levels at all — a positive claim built on a call that never
    /// came back — while silently stripping every membership of its name.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ProcessBehaviourSummary>?> GetBehaviourCatalogueAsync(
        CancellationToken ct = default)
    {
        var identity = await GetProcessIdentityAsync(ct);
        if (identity is null)
            return null;

        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(identity.ProcessId)}" +
            $"/behaviors?api-version={AdoApiVersions.ProcessBehaviors}";

        AdoProcessBehaviourListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoProcessBehaviourListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        var catalogue = new List<ProcessBehaviourSummary>(result.Value.Count);
        foreach (var behaviour in result.Value)
        {
            // referenceName is the join key. A row without one can name nothing.
            if (string.IsNullOrWhiteSpace(behaviour.ReferenceName))
                continue;

            catalogue.Add(new ProcessBehaviourSummary(
                behaviour.ReferenceName,
                behaviour.Name ?? string.Empty,
                behaviour.Rank));
        }

        return catalogue;
    }

    /// <summary>
    /// One type's form layout, in the document's ordered shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Read at the same api-version the shipped <c>twig process layout</c> command uses</b>
    /// (<see cref="AdoApiVersions.ProcessLayout"/>), so two surfaces describing the same form
    /// cannot disagree because one of them was pinned elsewhere. <c>7.1</c> and
    /// <c>7.1-preview.1</c> return byte-identical bodies, verified live 2026-08-12.
    /// </para>
    /// <para>
    /// 🔴 <b>This DTO needs a DELIBERATE count-shaped-body guard.</b> A count-shaped 404
    /// (<c>{"count":1,"value":{"Message":…}}</c>) carries none of this DTO's keys, and
    /// <c>System.Text.Json</c> ignores unmapped members — so unlike the sibling LIST fetches it
    /// does not throw. Those siblings deserialize into a <c>*ListResponse</c> whose
    /// <c>Value</c> is a <c>List&lt;T&gt;</c> and the count-shaped body puts an object where
    /// the array belongs; that defence is structural, and this DTO — a bare object whose only
    /// array-shaped member is <c>pages</c> — falls outside it. Untreated, a failed fetch
    /// deserializes into an all-null instance and the <c>Pages is null</c> check below is what
    /// turns it into <c>null</c> rather than a layout with no pages. Keeping that check keyed
    /// on <c>pages</c> is therefore load-bearing, not defensive tidiness: a real layout always
    /// carries at least one page.
    /// </para>
    /// <para>
    /// Returns <c>null</c> both when the call failed and when the process serves no layout for
    /// the type. Those are different facts and this method cannot distinguish them, so it makes
    /// the weaker claim: <c>formLayout</c> lands in the type's unfetched list either way, which
    /// says "this document does not answer that for this type" rather than "this type's form is
    /// empty". Reporting an empty layout would be the stronger and unsupportable one.
    /// </para>
    /// <para>
    /// 🔴 <b>No ordering is decided here.</b> Every level is passed through in server order and
    /// the ASSEMBLER sorts on the <c>order</c> key carried alongside — a second ordering
    /// authority would make byte-stability depend on two places agreeing forever.
    /// </para>
    /// </remarks>
    private async Task<ProcessDescriptionLayout?> FetchLayoutAsync(
        string processId,
        string typeReferenceName,
        CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(typeReferenceName)}" +
            $"/layout?api-version={AdoApiVersions.ProcessLayout}";

        AdoFormLayoutResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoFormLayoutResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        // 🔴 A LOCKED system type answers this route with 400 VS403115
        // ("you cannot modify form layout information … as these work item types are locked"),
        // NOT a 404. Found by running the command live against the real process: without this
        // catch, one locked type (TestCase / TestPlan / TestSuite are all locked in this org)
        // propagated out of GetTypeDetailAsync and killed the WHOLE description — 14 types
        // lost to one type's answer. The seam tests could not see it, because a scripted
        // source never returns a 400.
        //
        // Swallowed rather than re-raised because it IS an answer: the process will not serve
        // a layout for this type, ever, and that is a fact about the type rather than a
        // transport failure. It is reported as `formLayout` unfetched — the honest weaker
        // claim, since this method cannot distinguish "locked" from "call failed" and an empty
        // layout would assert the type's form has no pages.
        // 🔴 NARROWED to the VS403115 marker, not every 400. A malformed api-version, a bad
        // reference-name escape or a future validation error must NOT become a silent
        // "formLayout unfetched" with exit 0 — that would be the exception-swallow-too-broad
        // failure, and it is asymmetric with the sibling fetches, which swallow only
        // AdoNotFoundException and JsonException. Only the locked-type answer is an answer.
        catch (AdoBadRequestException ex)
            when (ex.Message.Contains(LockedFormLayoutMarker, StringComparison.Ordinal))
        {
            return null;
        }
        catch (JsonException) { return null; }

        // 🔴 The count-shaped-body guard. Absence of `pages` means this is not a layout payload
        // at all — treat it as a failed fetch, never as a form that has no pages.
        if (result?.Pages is null)
            return null;

        return new ProcessDescriptionLayout(
            SystemControls:
            [
                // 🔴 Carried, not discarded. These arrive in the SAME response as `pages` — so
                // they are reachable, and dropping them would be an omission with no marker
                // while the document's header claims it makes no reservations.
                .. (result.SystemControls ?? []).Select(static control =>
                    new ProcessDescriptionLayoutControl(
                        control.Id ?? string.Empty,
                        control.Label ?? string.Empty,
                        control.ControlType ?? string.Empty,
                        control.ReadOnly,
                        control.Visible ?? true,
                        control.Inherited ?? false,
                        control.IsContribution,
                        control.Order)),
            ],
            Pages:
        [
            .. result.Pages.Select(static page => new ProcessDescriptionLayoutPage(
                page.Id ?? string.Empty,
                page.Label ?? string.Empty,
                page.PageType ?? string.Empty,
                // An absent `visible` means visible; the server omits it on the common case.
                page.Visible ?? true,
                page.Inherited ?? false,
                page.IsContribution,
                page.Order,
                [
                    .. (page.Sections ?? []).Select(static section =>
                        new ProcessDescriptionLayoutSection(
                            section.Id ?? string.Empty,
                            [
                                .. (section.Groups ?? []).Select(static group =>
                                    new ProcessDescriptionLayoutGroup(
                                        group.Id ?? string.Empty,
                                        group.Label ?? string.Empty,
                                        group.Visible ?? true,
                                        group.Inherited ?? false,
                                        group.IsContribution,
                                        group.Order,
                                        [
                                            .. (group.Controls ?? []).Select(static control =>
                                                new ProcessDescriptionLayoutControl(
                                                    control.Id ?? string.Empty,
                                                    control.Label ?? string.Empty,
                                                    // Verbatim: the reader compares the
                                                    // server's vocabulary, not a paraphrase.
                                                    control.ControlType ?? string.Empty,
                                                    control.ReadOnly,
                                                    control.Visible ?? true,
                                                    control.Inherited ?? false,
                                                    control.IsContribution,
                                                    control.Order)),
                                        ])),
                            ])),
                ])),
        ]);
    }

    /// <remarks>
    /// 🔴 The second source of requiredness (AB#236). The per-type fields route reports
    /// UNCONDITIONAL requiredness only; a field made mandatory by a <c>makeRequired</c> rule
    /// reads there as not-required. Verified live: <c>Custom.WayfinderAnswer</c> is
    /// <c>required: null</c> on the fields route while this route carries a
    /// <c>makeRequired</c> action for it.
    /// <para>
    /// Returns <c>null</c> on failure and never an empty list, so "the rules call failed"
    /// cannot be laundered into "this type has no conditional requiredness" — which would
    /// reinstate the exact silent lie this ticket removes.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ProcessRule>?> FetchRulesAsync(
        string processId,
        string typeReferenceName,
        CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/work/processes/{Uri.EscapeDataString(processId)}" +
            $"/workItemTypes/{Uri.EscapeDataString(typeReferenceName)}" +
            $"/rules?api-version={AdoApiVersions.ProcessRules}";

        AdoProcessRuleListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoProcessRuleListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        return
        [
            .. result.Value.Select(rule => new ProcessRule(
                rule.Conditions?.Select(condition => new RuleCondition(
                    condition.ConditionType ?? string.Empty,
                    condition.Field ?? string.Empty,
                    condition.Value)).ToList() ?? [],
                rule.Actions?.Select(action => new RuleAction(
                    action.ActionType ?? string.Empty,
                    action.TargetField ?? string.Empty,
                    action.Value)).ToList() ?? [],
                rule.IsDisabled,
                // 🔴 The customization tag (AB#238) — the ONLY filter available for the ~54
                // inherited system rules a derived type carries, and therefore the thing that
                // makes the carry-everything ruling bearable for a reader. Absent means
                // Unknown, never `system`: reading a missing key as inherited plumbing would
                // let the reader's own filter discard authored rules.
                RuleCustomization.From(rule.CustomizationType),
                rule.Name)),
        ];
    }

    /// <remarks>
    /// 🔴 The picklist association source (AB#237). Two hops, deliberately:
    /// <list type="number">
    /// <item><description>
    /// <c>_apis/wit/fields</c> for the association itself. This is the ONLY route that
    /// carries it — no process route reports <c>isPicklist</c> or a picklist reference at any
    /// api-version, with or without <c>$expand=all</c>. It reports <c>isPicklist</c> on
    /// EVERY row, which is what lets a non-list-backed field be reported as unconstrained as
    /// a stated server FACT rather than as a guess. That explicit negative is what makes the
    /// ban on name-matching costless rather than a sacrifice.
    /// </description></item>
    /// <item><description>
    /// <c>_apis/work/processes/lists/{id}</c> for the contents, once per DISTINCT list. The
    /// list-all route returns metadata only (every entry carries <c>items: []</c>), so there
    /// is no batch form; distinct rather than per field, because several fields may share one
    /// list.
    /// </description></item>
    /// </list>
    /// <para>
    /// 🔴 Returns <c>null</c> on a failed field-list call and never an empty map. An empty map
    /// asserts that every field is unconstrained — a confident claim built on a call that
    /// never came back, and precisely this ticket's own lie in the OVERSTATING direction
    /// inverted.
    /// </para>
    /// <para>
    /// 🔴 A field whose list could not be resolved is <see cref="FieldValueConstraint.Unknown"/>
    /// rather than unconstrained, and the whole run is NOT failed for it: the association is
    /// still known for every other field, and discarding those answers over one bad list
    /// would trade a lot of truth for no extra honesty.
    /// </para>
    /// <para>
    /// Keyed <c>OrdinalIgnoreCase</c>: this route and the per-type fields route are different
    /// surfaces, and an exact join would silently drop a real constraint over a casing
    /// difference — reporting a list-backed field as unconstrained, byte-identical to a field
    /// that genuinely is.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, FieldValueConstraint>?> GetFieldValueConstraintsAsync(
        CancellationToken ct = default)
    {
        // 🔴 GA 7.1 and org-scoped. Read for `isPicklist`/`picklistId` ONLY — this list is
        // identical for every work item type and must never be presented as a type's fields.
        var url = $"{_orgUrl}/_apis/wit/fields?api-version={AdoApiVersions.Fields}";

        AdoOrgFieldListResponse? result;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            result = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoOrgFieldListResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoNotFoundException) { return null; }
        catch (JsonException) { return null; }

        if (result?.Value is null)
            return null;

        // One fetch per DISTINCT list id, gathered before the map is built.
        // 🔴 OrdinalIgnoreCase: a GUID's casing is not part of its identity, so two spellings
        // of one id must not cost two round trips — or, worse, miss the lookup below and
        // report a genuinely list-backed field as Unknown.
        var listIds = result.Value
            .Where(f => f.IsPicklist == true && !string.IsNullOrWhiteSpace(f.PicklistId))
            .Select(f => f.PicklistId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 🔴 Fan-out is BOUNDED. One in-flight GET per distinct list, unthrottled, is fine at
        // this org's seven lists and is a 429 generator at two hundred — and a throttle here
        // degrades exactly the answer this ticket exists to make trustworthy. Four is the same
        // order as the per-type fetch this method runs alongside.
        using var gate = new SemaphoreSlim(4);
        var fetched = await Task.WhenAll(listIds.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { return await FetchPicklistAsync(id, ct); }
            finally { gate.Release(); }
        }));

        var lists = new Dictionary<string, AdoPicklistResponse?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < listIds.Count; i++)
            lists[listIds[i]] = fetched[i];

        var constraints = new Dictionary<string, FieldValueConstraint>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var field in result.Value)
        {
            if (string.IsNullOrWhiteSpace(field.ReferenceName))
                continue;

            var resolved = ResolveOne(field, lists);

            // 🔴 A duplicate reference name must not be settled by WIRE ORDER. The map is
            // OrdinalIgnoreCase and this route is org-scoped, so two rows differing only in
            // casing collapse onto one key — and "last writer wins" would make the document
            // depend on the order the server happened to send them, which is the one thing
            // this feature forbids. When two rows genuinely disagree neither answer is
            // defensible, so the honest report is that we do not know.
            if (constraints.TryGetValue(field.ReferenceName, out var existing)
                && !SameConstraint(existing, resolved))
            {
                constraints[field.ReferenceName] = FieldValueConstraint.Unknown;
                continue;
            }

            constraints[field.ReferenceName] = resolved;
        }

        return constraints;
    }

    /// <summary>
    /// One org field row's value constraint, before the duplicate-key check.
    /// </summary>
    /// <remarks>
    /// 🔴 Four outcomes, and only ONE of them is <see cref="FieldValueConstraint.Unconstrained"/>.
    /// Three separate ways of not knowing all resolve to
    /// <see cref="FieldValueConstraint.Unknown"/> rather than collapsing into the positive
    /// claim — that collapse is the exact lie AB#237 removes.
    /// </remarks>
    private static FieldValueConstraint ResolveOne(
        AdoOrgFieldResponse field,
        IReadOnlyDictionary<string, AdoPicklistResponse?> lists)
    {
        // 🔴 The key is ABSENT, not false. This route is documented to carry `isPicklist` on
        // every row, and the whole no-heuristic design rests on that. If it ever stops, that is
        // a source change — and reading a missing key as the explicit negative would be
        // "absence of evidence rendered as a stated fact", the banned inference in a new hat.
        if (field.IsPicklist is null)
            return FieldValueConstraint.Unknown;

        // 🔴 The explicit negative, carried as a FACT. Not an absence of evidence and not an
        // inference from the field's name or type.
        if (field.IsPicklist == false)
            return FieldValueConstraint.Unconstrained;

        // 🔴 `isPicklist` is TRUE but the pointer is missing. The association is PROVEN present
        // and only the target is unreadable, so this is Unknown — never unconstrained.
        // `picklistId` is a conditional key on this route, so a version slip that drops it
        // while keeping `isPicklist: true` reaches exactly here, and "the server accepts
        // anything" would be the most dangerous of the three wrong answers, because acting on
        // it fails at the server.
        if (string.IsNullOrWhiteSpace(field.PicklistId))
            return FieldValueConstraint.Unknown;

        // 🔴 The field IS list-backed but its list did not come back. Unknown for the same
        // reason: the association is proven, only the values are missing.
        if (!lists.TryGetValue(field.PicklistId, out var list) || list is null)
            return FieldValueConstraint.Unknown;

        // Values are passed through in server order. 🔴 The ASSEMBLER sorts them — the single
        // ordering authority — and doing it here as well would make byte-stability depend on
        // two places agreeing forever.
        //
        // 🔴 Nulls ARE filtered here, at the DTO boundary, because that is a typing concern
        // rather than an ordering one: `items` is a JSON array that may contain null, while
        // FieldValueConstraint.Values is declared non-nullable. An unfiltered null renders as
        // an empty segment in the joined value list — indistinguishable from a real
        // empty-string value the process actually accepts.
        IReadOnlyList<string> values = [.. (list.Items ?? []).Where(static v => v is not null)];

        // 🔴 SUGGESTED is not CONSTRAINED. A suggested picklist offers its values in the web
        // editor while the server still accepts anything, so calling it a constraint would
        // tell a caller its write must come from the list when it need not — the overstatement
        // this ticket removes, arriving through an unread flag rather than a bad guess.
        //
        // Either witness is enough: the field row's flag is primary, and the list's own mirrors
        // it. If they disagree the weaker claim wins, because disagreement between two views of
        // one list is not grounds for asserting the stronger one.
        return field.IsPicklistSuggested == true || list.IsSuggested == true
            ? FieldValueConstraint.SuggestedFrom(list.Name, values)
            : FieldValueConstraint.ConstrainedTo(list.Name, values);
    }

    /// <remarks>
    /// Structural comparison INCLUDING the values. <see cref="FieldValueConstraint"/> is a
    /// record, but its <c>Values</c> is an <c>IReadOnlyList&lt;string&gt;</c>, which records
    /// compare by REFERENCE — so compiler-generated equality would call two identical lists
    /// different and manufacture a conflict that is not there.
    /// </remarks>
    private static bool SameConstraint(FieldValueConstraint left, FieldValueConstraint right)
        => left.Kind == right.Kind
            && string.Equals(left.ListName, right.ListName, StringComparison.Ordinal)
            && left.Values.SequenceEqual(right.Values, StringComparer.Ordinal);

    /// <remarks>
    /// Returns <c>null</c> on failure rather than an empty list: an empty <c>items</c> array
    /// is a REAL state (a list that exists and holds nothing, constraining the field to
    /// nothing), so laundering a failed call into one would assert something false about the
    /// field rather than merely losing detail.
    /// <para>
    /// 🔴 <b>The envelope is VALIDATED, not merely deserialized, and that is load-bearing on
    /// this route family.</b> A count-shaped error body (<c>{"count":1,"value":{"Message":…}}</c>)
    /// carries none of this DTO's keys, and <c>System.Text.Json</c> ignores unmapped members by
    /// default — so unlike every sibling fetch in this class it does NOT throw. Those siblings
    /// deserialize into a <c>*ListResponse</c> whose <c>Value</c> is a <c>List&lt;T&gt;</c>, and
    /// the count-shaped body puts an OBJECT where the array belongs, which is what makes them
    /// fail. That defence is structural, and this DTO — a bare object with no array-shaped
    /// member — falls outside it.
    /// </para>
    /// <para>
    /// Untreated, a failed fetch would deserialize into an all-null instance and be reported as
    /// <c>ListConstrained</c> with NO values: "the server accepts no value at all here", the
    /// strongest possible positive claim, built on a call that failed. That is worse than the
    /// unconstrained collapse this ticket exists to prevent. A real picklist always carries
    /// <c>id</c>, so its absence is the tell.
    /// </para>
    /// </remarks>
    private async Task<AdoPicklistResponse?> FetchPicklistAsync(string listId, CancellationToken ct)
    {
        var url = $"{_orgUrl}/_apis/work/processes/lists/{Uri.EscapeDataString(listId)}" +
            $"?api-version={AdoApiVersions.ProcessLists}";

        AdoPicklistResponse? list;
        try
        {
            using var response = await SendAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            list = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AdoPicklistResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        // 🔴 ANY ADO failure on ONE list yields Unknown for the fields it backs, and leaves
        // every other field's answer intact. Narrower catches (404 + malformed JSON only) would
        // let a throttle or a transient server error on a single list fail the whole
        // description — contradicting this method's own contract, and trading a lot of truth
        // for no extra honesty. Cancellation still propagates: that is the caller's decision,
        // not a source failure.
        catch (AdoException) { return null; }
        catch (JsonException) { return null; }

        // 🔴 The count-shaped-body guard. Absence of `id` means this is not a picklist payload
        // at all — treat it as a failed fetch, never as a list that exists and holds nothing.
        return string.IsNullOrWhiteSpace(list?.Id) ? null : list;
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
