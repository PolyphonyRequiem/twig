namespace Twig.Infrastructure.Ado;

/// <summary>
/// The api-version pinned for each ADO REST route Twig calls.
/// </summary>
/// <remarks>
/// 🔴 <b>The api-version is part of the contract, not decoration.</b> On these routes it
/// selects the response <i>schema</i>, not merely the route version, so it cannot be
/// treated as a shared constant that any route may drift onto:
/// <list type="bullet">
/// <item><description>
/// The same per-type fields URL returns <b>disjoint attribute sets</b> at
/// <c>7.1-preview.1</c> (<c>description/id/isIdentity/isLocked/name/type/url</c>) and
/// <c>7.1-preview.2</c> (<c>customization/defaultValue/isLocked/name/referenceName/
/// required/type/url</c>). A whole-process survey reported <b>59</b> required fields at
/// preview.2 and <b>ZERO</b> at preview.1 — same URL, same counts, different schema.
/// </description></item>
/// <item><description>
/// The process-wide fields route <b>404s at plain <c>7.1</c></b>; that version is not
/// valid on it. It works at <c>7.1-preview.1</c>.
/// </description></item>
/// <item><description>
/// A 404 envelope from the process routes is <b>count-shaped</b>
/// (<c>{"count":1,"value":{"Message":…}}</c>), so a failed call misreads as thin-but-valid
/// data. Never treat a count-shaped body as success on these routes.
/// </description></item>
/// </list>
/// Evidence: branch <c>docs/process-descriptor-map</c>,
/// <c>wayfinder-process-descriptor/assets/0001-endpoint-findings.md</c> (probed live
/// 2026-08-11). Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c>
/// Implementation Decision 7.
/// <para>
/// Every constant below states <b>what that version buys</b>. A version that is merely
/// "what worked when this was written" is how this silently regresses — if you change one,
/// change its comment to say why, or leave it alone.
/// </para>
/// </remarks>
internal static class AdoApiVersions
{
    // ── Work item tracking (wit) ──────────────────────────────────────────────
    // These are the long-shipped routes. 7.1 is the GA version and is valid on all of
    // them; nothing here needs a preview surface, and pinning GA is what keeps them
    // stable across server-side preview churn.

    /// <summary>
    /// <c>_apis/wit/workitems</c> — read, create, update, delete, and the batch/relations
    /// variants. GA <c>7.1</c>: the full field/relations shape Twig maps is stable here,
    /// and no preview surface adds anything Twig reads.
    /// </summary>
    internal const string WorkItems = "7.1";

    /// <summary>
    /// <c>_apis/wit/workitems/$&lt;type&gt;</c> — the new-work-item template. GA <c>7.1</c>:
    /// same shape as the work item read route, deliberately pinned together with it.
    /// </summary>
    internal const string WorkItemTemplate = "7.1";

    /// <summary>
    /// <c>_apis/wit/workItems/{id}/updates</c> — the revision timeline used to project
    /// history. GA <c>7.1</c>: carries the <c>fields</c> old/new pairs and relation adds
    /// and removes that the projector needs, with <c>$top</c>/<c>$skip</c> offset paging.
    /// </summary>
    internal const string WorkItemUpdates = "7.1";

    /// <summary>
    /// <c>_apis/wit/wiql</c> — query execution. GA <c>7.1</c>: required for
    /// <c>timePrecision=true</c>, which is what buys the sub-day fence on date queries.
    /// </summary>
    internal const string Wiql = "7.1";

    /// <summary>
    /// <c>_apis/wit/workitems/{id}/comments</c> — 🔴 <b>preview only.</b> Comments have
    /// never gone GA on this route; <c>7.1-preview.4</c> is the current preview and the
    /// one that returns the comment <c>text</c>/<c>createdBy</c> shape Twig posts and
    /// reads. Plain <c>7.1</c> is not valid here.
    /// </summary>
    internal const string WorkItemComments = "7.1-preview.4";

    /// <summary>
    /// <c>_apis/wit/workitemtypes</c> — the project-scoped type list. GA <c>7.1</c>:
    /// carries <c>name</c>, <c>referenceName</c>, <c>color</c>, <c>icon</c> and
    /// <c>isDisabled</c>, which is everything the type resolution needs.
    /// <para>
    /// 🔴 Not to be confused with the <b>process</b>-scoped
    /// <c>_apis/work/processes/{id}/workItemTypes</c> route, whose schema is
    /// version-split — see <see cref="ProcessWorkItemTypes"/>.
    /// </para>
    /// </summary>
    internal const string WorkItemTypes = "7.1";

    /// <summary>
    /// <c>_apis/wit/fields</c> — the project-wide field definition list. GA <c>7.1</c>:
    /// carries <c>referenceName</c>, <c>name</c> and <c>type</c> for identity/type-aware
    /// field handling.
    /// <para>
    /// 🔴 This list is <b>project-wide and identical for every work item type</b>. It is
    /// not type-scoped and must never be presented as a type's field list. Type-scoped
    /// fields come from <see cref="ProcessWorkItemTypeFields"/>.
    /// </para>
    /// <para>
    /// 🔴 <b>Two different URLs share this constant.</b> The project-scoped
    /// <c>{project}/_apis/wit/fields</c> (<c>AdoIterationService</c>, the field definition
    /// sync) and the ORG-scoped <c>_apis/wit/fields</c> with no project segment
    /// (<c>AdoProcessDescriptionSource</c>, AB#237, read for <c>isPicklist</c> /
    /// <c>picklistId</c> only). Same version, same schema, different scope — if you change
    /// this constant, check both callers.
    /// </para>
    /// </summary>
    internal const string Fields = "7.1";

    /// <summary>
    /// <c>_apis/wit/classificationnodes/areas</c> — the area path tree. GA <c>7.1</c>:
    /// honours <c>$depth</c>, which is what buys the whole tree in one call.
    /// </summary>
    internal const string ClassificationNodes = "7.1";

    // ── Work / team settings ──────────────────────────────────────────────────

    /// <summary>
    /// <c>_apis/work/teamsettings/iterations</c> — team iterations, with and without
    /// <c>$timeframe=current</c>. GA <c>7.1</c>: carries the <c>attributes</c> block
    /// (<c>startDate</c>/<c>finishDate</c>/<c>timeFrame</c>) the current-sprint detection
    /// keys on.
    /// </summary>
    internal const string TeamIterations = "7.1";

    /// <summary>
    /// <c>_apis/work/teamsettings/teamfieldvalues</c> — the team's default area path.
    /// GA <c>7.1</c>.
    /// </summary>
    internal const string TeamFieldValues = "7.1";

    /// <summary>
    /// <c>_apis/work/processconfiguration</c> — backlog level configuration. GA
    /// <c>7.1</c>: carries the portfolio/requirement/task backlog categories and their
    /// work item type lists.
    /// </summary>
    internal const string ProcessConfiguration = "7.1";

    // ── Core ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>_apis/projects/{project}</c> — project lookup, including
    /// <c>?includeCapabilities=true</c> for the process template id. GA <c>7.1</c>: the
    /// <c>capabilities.processTemplate.templateTypeId</c> field is what every
    /// process-scoped route below is keyed by, and it is present at GA.
    /// </summary>
    internal const string Projects = "7.1";

    /// <summary>
    /// <c>app.vssps.visualstudio.com/_apis/profile/profiles/me</c> — the authenticated
    /// identity. GA <c>7.1</c>: carries <c>displayName</c>, and this host is the one that
    /// answers reliably for BOTH a PAT and an <c>az</c> CLI token.
    /// </summary>
    internal const string Profile = "7.1";

    // ── Git ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>_apis/git/repositories/{repo}/pullrequests</c> — PR list and branch search.
    /// GA <c>7.1</c>: carries <c>pullRequestId</c>, <c>status</c> and the source/target
    /// ref names the branch matching needs.
    /// </summary>
    internal const string GitPullRequests = "7.1";

    /// <summary>
    /// <c>_apis/git/repositories/{repo}</c> — repository lookup. GA <c>7.1</c>.
    /// </summary>
    internal const string GitRepositories = "7.1";

    // ── Process (org-scoped, keyed by process template id) ────────────────────
    // 🔴 This is the family where the version is load-bearing. Read the class remarks
    // before touching any constant below.

    /// <summary>
    /// <c>_apis/work/processes/{processId}/workItemTypes/{ref}/rules</c> — the authored
    /// and inherited rules on a type.
    /// <para>
    /// Pinned <c>7.1</c>, which is what the shipped <c>twig process rules</c> path has
    /// always called and what its behaviour is verified against.
    /// </para>
    /// <para>
    /// 🔴 <b>The move to <c>7.1-preview.2</c> that AB#236 deferred turned out to be
    /// unnecessary — re-probed live 2026-08-12 and the two versions return BYTE-IDENTICAL
    /// bodies.</b> The endpoint survey (0001) recorded <c>customizationType</c> as a preview.2
    /// attribute, which read as "GA does not carry it"; it does. Both versions return 54 rules
    /// for <c>Niflheim.Epic</c> with the keys
    /// <c>actions, conditions, customizationType, id, isDisabled, name, url</c>, and the same
    /// 53-system / 1-custom split. So AB#238 carries the customization tag the ruling requires
    /// WITHOUT a version change, and the shipped <c>twig process rules</c> output is untouched.
    /// Do not "align" this constant with its preview.2 neighbours on the strength of the survey
    /// note — there is nothing to buy, and a version change here is a behaviour change to a
    /// shipped command.
    /// </para>
    /// </summary>
    internal const string ProcessRules = "7.1";

    /// <summary>
    /// <c>_apis/work/processes/{processId}/workItemTypes/{ref}/layout</c> — the
    /// server-defined form layout (pages, sections, groups, controls). Pinned <c>7.1</c>,
    /// the version the shipped <c>twig process layout</c> output is verified against.
    /// </summary>
    internal const string ProcessLayout = "7.1";

    // ── Process description routes ────────────────────────────────────────────
    // 🔴 Pinned here, called by later work (#234 onward). They are declared now, with
    // their evidence, because their versions were established by live probing in 0001 and
    // that finding is exactly what would be lost if each caller picked a version later.
    // These are NOT interchangeable with the GA constants above.

    /// <summary>
    /// <c>_apis/work/processes/{processId}/fields</c> — the process-wide field list.
    /// <para>
    /// 🔴 <b><c>7.1-preview.1</c> is mandatory: this route 404s at plain <c>7.1</c></b> —
    /// that version is not valid on it, and the 404 body is count-shaped, so calling it
    /// at GA looks like thin data rather than a failure. At preview.1 it returns 93
    /// fields (13 <c>Custom.*</c>) on the inherited process probed, byte-identical to what
    /// <c>7.2-preview.1</c> and the older <c>*-preview.1</c> versions return; preview.2
    /// and preview.3 also 404.
    /// </para>
    /// </summary>
    internal const string ProcessFields = "7.1-preview.1";

    /// <summary>
    /// <c>_apis/work/processes/{processId}/workItemTypes</c> — the process-scoped type
    /// list.
    /// <para>
    /// 🔴 <b><c>7.1-preview.2</c> buys <c>referenceName</c> and <c>customization</c></b> —
    /// stable identity and authored-vs-inherited. At <c>7.1-preview.1</c> the same URL
    /// returns <c>id</c> and <c>class</c> instead, and carries neither. Display names lie
    /// (one process observed using reference names from a differently-named process), so
    /// preview.1 cannot support matching two processes to each other.
    /// </para>
    /// </summary>
    internal const string ProcessWorkItemTypes = "7.1-preview.2";

    /// <summary>
    /// <c>_apis/work/processes/{processId}/workItemTypes/{ref}/fields</c> — the
    /// <b>type-scoped</b> field list.
    /// <para>
    /// 🔴 <b><c>7.1-preview.2</c> buys <c>required</c>, <c>defaultValue</c> and
    /// <c>customization</c>.</b> The same URL at <c>7.1-preview.1</c> returns a disjoint
    /// attribute set with none of the three — identical counts, so the difference is
    /// invisible unless you look at the keys. A survey at preview.1 reported <c>required</c>
    /// on 0 of 628 field rows; the identical survey at preview.2 reported 59.
    /// </para>
    /// <para>
    /// 🔴 Even at preview.2, <c>required</c> reports only <b>unconditional</b>
    /// requiredness. Conditional requiredness (a <c>makeRequired</c> action gated on a
    /// <c>when</c> condition) lives in <see cref="ProcessRules"/> and is invisible here.
    /// Reporting requiredness from this route alone is wrong in the silent direction.
    /// This route also carries no <c>allowedValues</c> and no picklist reference at any
    /// version, with or without <c>$expand=all</c>.
    /// </para>
    /// </summary>
    internal const string ProcessWorkItemTypeFields = "7.1-preview.2";

    /// <summary>
    /// <c>_apis/work/processes/{processId}/workItemTypes/{ref}/states</c> — the
    /// <b>type-scoped</b> state list.
    /// <para>
    /// GA <c>7.1</c>, and unusually for this family that is the RIGHT choice rather than a
    /// drift: probed live 2026-08-11, <c>7.1</c> and <c>7.1-preview.1</c> return the same
    /// body (<c>name</c>, <c>stateCategory</c>, <c>order</c>, <c>color</c>,
    /// <c>customizationType</c>, <c>hidden</c>), while <c>7.1-preview.2</c> is rejected
    /// outright with <c>VssVersionOutOfRangeException</c> — "outside the valid version
    /// range for this route". So preview.2, the richer version everywhere else in this
    /// family, is NOT valid here. Do not "align" this constant with its neighbours.
    /// </para>
    /// <para>
    /// <c>customizationType</c> is what this buys over the project-scoped state list: it
    /// distinguishes a state authored on this process from one inherited from the parent.
    /// </para>
    /// </summary>
    internal const string ProcessWorkItemTypeStates = "7.1";

    /// <summary>
    /// <c>{project}/_apis/wit/workitemtypes?$expand=all</c> — the ONLY source of state
    /// TRANSITIONS. GA <c>7.1</c>.
    /// <para>
    /// 🔴 <b>This is a deliberate reach outside the modern process API, and it is forced.</b>
    /// Probed live 2026-08-11: the process-scoped
    /// <c>…/workItemTypes/{ref}/transitions</c> and <c>…/stateTransitions</c> routes return
    /// an <b>HTML 404 (no such controller)</b> at <c>7.1</c>, <c>7.1-preview.1</c> and
    /// <c>7.1-preview.2</c> alike, and the process type list carries no transitions under
    /// any <c>$expand</c> value. The modern API simply does not serve them.
    /// </para>
    /// <para>
    /// 🔴 <b>Do not "simplify" this by deriving transitions from the state list.</b> The
    /// obvious shortcut — assume every state reaches every other — is WRONG on live data:
    /// of 20 types probed in this org, 4 are not fully connected, and one declares a state
    /// no transition reaches. Deriving would report transitions that do not exist.
    /// </para>
    /// <para>
    /// This route is project-scoped rather than process-scoped, which is a real narrowing:
    /// it can only describe the process the CONFIGURED project runs on. It does return
    /// <c>referenceName</c>, so type identity stays reference-name-keyed and the
    /// display-names-lie hazard is not reintroduced. It also returns MORE types than the
    /// process reports (it includes system helper types such as Code Review Request), so
    /// callers must intersect against the process's own type list rather than trusting it
    /// as the roster.
    /// </para>
    /// </summary>
    internal const string ProjectWorkItemTypesExpanded = "7.1";

    /// <summary>
    /// <c>_apis/work/processes/lists</c> and <c>_apis/work/processes/lists/{listId}</c> —
    /// picklists. <c>7.1-preview.1</c> is the version these were probed working at.
    /// <para>
    /// 🔴 The list-all call returns metadata only — every entry has <c>items: []</c> —
    /// so the item values cost one extra call per list. Picklists are org-wide, not
    /// per-process. Nothing on any PROCESS route names which picklist backs which field;
    /// that association is read FIELD-FIRST off <c>_apis/wit/fields</c>, whose
    /// <c>isPicklist</c> / <c>picklistId</c> pair carries it — see
    /// <c>AdoProcessDescriptionSource.GetFieldValueConstraintsAsync</c> (AB#237). It was
    /// recorded as an open capability gap until that ticket found the field-first route.
    /// </para>
    /// </summary>
    internal const string ProcessLists = "7.1-preview.1";

    /// <summary>
    /// <c>_apis/work/processes/{processId}/workItemTypesBehaviors/{ref}/behaviors</c> —
    /// which backlog levels ONE type belongs to. GA <c>7.1</c>.
    /// <para>
    /// 🔴 <b>The route segment is <c>workItemTypesBehaviors</c>, NOT
    /// <c>workItemTypes/{ref}/behaviors</c>.</b> The obvious route returns an HTML 404 ("the
    /// controller for path … was not found or does not implement IController") for every type
    /// on both an inherited and a stock process — probed live 2026-08-11 and re-probed
    /// 2026-08-12. Note the shape: an HTML page, not the count-shaped JSON envelope the rest of
    /// this family returns for a 404.
    /// </para>
    /// <para>
    /// <c>7.1</c> and <c>7.1-preview.1</c> return byte-identical bodies (verified 2026-08-12),
    /// so GA is chosen for the usual reason: it is the version least likely to move under us.
    /// The row is a REFERENCE only — <c>{"behavior":{"id":…},"isDefault":…}</c> — so naming
    /// the level costs one further call, see <see cref="ProcessBehaviors"/>.
    /// </para>
    /// </summary>
    internal const string ProcessTypeBehaviors = "7.1";

    /// <summary>
    /// <c>_apis/work/processes/{processId}/behaviors</c> — the process's behaviour
    /// CATALOGUE: every backlog level it defines, with name and rank. <c>7.1-preview.2</c>.
    /// <para>
    /// PROCESS-scoped, so it is ONE call per description run regardless of how many types are
    /// described — never per type. It exists in the description's fetch path solely to turn the
    /// membership route's bare reference into a readable name: a custom backlog level's
    /// reference name is a GUID (<c>Custom.3daa3b35-…</c>), so a document carrying the edge
    /// alone would be true and unreadable, and worthless in a diff between two processes whose
    /// levels have different ids and the same name.
    /// </para>
    /// <para>
    /// preview.2 is the version 0001 probed this route working at; it returns
    /// <c>referenceName</c>, <c>name</c>, <c>rank</c>, <c>customization</c> and
    /// <c>inherits</c>. 🔴 <c>referenceName</c> here is the same identity the membership route
    /// keys <c>id</c> — the join between the two names the level.
    /// </para>
    /// </summary>
    internal const string ProcessBehaviors = "7.1-preview.2";
}
