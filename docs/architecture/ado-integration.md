# ADO Integration Layer

How twig communicates with Azure DevOps: REST client, authentication,
work-item operations, field enrichment, conflict resolution, and link management.

---

## 1. ADO REST Client

### AdoRestClient

`Twig.Infrastructure/Ado/AdoRestClient.cs` — sealed internal class implementing
`IAdoWorkItemService`. All ADO HTTP traffic flows through this single class.

**API version:** `7.1` (comments use `7.1-preview.4`).

**Org URL normalisation:** bare org names (e.g. `my-org`) are expanded to
`https://dev.azure.com/my-org`; absolute URLs pass through unchanged.

### HTTP plumbing

The shared `HttpClient` is built in `NetworkServiceModule.CreateHttpClient()`:

| Setting | Value |
|---------|-------|
| HTTP version | 2.0 with 1.1 fallback (`RequestVersionOrLower`) |
| Decompression | GZip + Brotli via `SocketsHttpHandler` |
| Auth header | Applied per-request by `AdoErrorHandler.ApplyAuthHeader()` |
| Patch content type | `application/json-patch+json` |

Every outbound call goes through `SendAsync()`, which:

1. Builds an `HttpRequestMessage` with method, URL and optional content.
2. Attaches the auth header (PAT → `Basic`, az CLI → `Bearer`).
3. Sets `If-Match` when an expected revision is supplied (optimistic concurrency).
4. Catches `HttpRequestException` / timeout → wraps as `AdoOfflineException`.
5. Delegates response validation to `AdoErrorHandler.ThrowOnErrorAsync()`.

### Batch processing

ADO caps batch fetches at **200 items**. `FetchBatchAsync` splits larger lists
into chunks of 200, fetches sequentially, and concatenates the results.

---

## 2. Authentication

Two providers implement `IAuthenticationProvider` (domain interface, single
method `GetAccessTokenAsync`). The active provider is selected at DI
registration time based on `TwigConfiguration.Auth.Method`.

```
if method == "pat"  →  PatAuthProvider
else                →  AzCliAuthProvider   (default)
```

### Azure CLI provider (`AzCliAuthProvider`)

The default flow uses the Azure CLI as a token broker:

```
az account get-access-token --resource 499b84ac-...798 --query accessToken -o tsv
```

**Three-tier cache:**

| Tier | Location | TTL |
|------|----------|-----|
| In-memory | Instance field | 50 min |
| Cross-process file | `~/.twig/.token-cache` | Until expiry tick |
| Azure CLI subprocess | `az account get-access-token` | On cache miss |

The file cache uses atomic write (tmp + rename) and `0600` permissions on
Unix. Stdout and stderr are read concurrently to avoid pipe-buffer deadlocks.

**Platform resolution:** On Windows the provider searches `PATH` for `az.cmd`;
on Unix it shells to `az` directly.

**Subprocess timeout:** 10 seconds.

### PAT provider (`PatAuthProvider`)

Token resolution order:

1. `$TWIG_PAT` environment variable.
2. `.twig/config` field `auth.pat`.

The token is formatted as HTTP Basic auth: `Basic base64(":PAT")`.

---

## 3. Work Item Operations

### IAdoWorkItemService interface

```csharp
public interface IAdoWorkItemService
{
    // Fetch
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<(WorkItem, IReadOnlyList<WorkItemLink>)> FetchWithLinksAsync(int id, …);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, …);
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, …);

    // Mutate
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, …);
    Task<int> CreateAsync(WorkItem seed, …);
    Task AddCommentAsync(int id, string text, …);

    // Query
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, …);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, …);

    // Links
    Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, …);
    Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, …);
}
```

### Fetch operations

| Method | Description |
|--------|-------------|
| `FetchAsync` | Single item with `$expand=relations` |
| `FetchWithLinksAsync` | Single item + mapped non-hierarchy links |
| `FetchChildrenAsync` | WIQL for children → batch fetch |
| `FetchBatchAsync` | POST to `_apis/wit/workitemsbatch`, chunked at 200 |

### Patch / Create

`PatchAsync` sends a JSON Patch document to
`_apis/wit/workitems/{id}?api-version=7.1`. An `If-Match` header carries the
expected revision for optimistic concurrency. The returned revision int is
used to update the local cache.

`CreateAsync` POSTs to `_apis/wit/workitems/$...` and captures the server-
assigned ID from the response.

### WIQL queries

The client serialises an `AdoWiqlRequest { Query }` and POSTs to
`_apis/wit/wiql`. The response (`AdoWiqlResponse`) returns a flat list of
work-item ID references. An optional `$top` parameter limits result count.

### DTOs

All wire types live in `Twig.Infrastructure/Ado/Dtos/` and use
`[JsonPropertyName]` attributes with source-generated serialisation
(`TwigJsonContext`):

- `AdoWorkItemResponse` — id, rev, fields dict, relations list
- `AdoPatchOperation` — op, path, value, from
- `AdoWiqlResponse` — queryType, workItems list
- `AdoBatchWorkItemResponse` — wraps `List<AdoWorkItemResponse>`

---

## 4. Field Enrichment

### Field definitions

`IIterationService.GetFieldDefinitionsAsync()` calls
`GET /{project}/_apis/wit/fields?api-version=7.1` and maps each entry to a
domain `FieldDefinition(ReferenceName, DisplayName, DataType, IsReadOnly)`.
Results are cached as a lazy-initialised task and stored in
`IFieldDefinitionStore` (SQLite) for offline access.

### Field import filter

`FieldImportFilter.ShouldImport(refName, fieldDef)` decides which ADO fields
are imported into a `WorkItem.Fields` dictionary:

| Rule | Action |
|------|--------|
| Core field (System.Id, Title, State, …) | Exclude — stored as first-class properties |
| No definition available | Import (safe fallback) |
| Display-worthy read-only (CreatedDate, Tags, CommentCount, …) | Import |
| Other read-only | Exclude |
| Importable data type (string, int, double, dateTime, html, plainText) | Import |
| Boolean | Exclude — cannot round-trip faithfully as string |

`System.CommentCount` is on the display-worthy list for a specific reason (AB#618): it is
read-only, so the read-only rule would drop it, and without it no machine-readable surface
carries any evidence that a work item has comments at all — meaning a `twig note` write
could not be verified through twig. Every such surface projects it as a top-level
`commentCount`, always emitted, via `WorkItemExtensions.ReadCommentCount`.

### Process configuration sync

`AdoIterationService.GetProcessConfigurationAsync()` fetches the project's
process configuration from
`GET /{project}/_apis/work/processconfiguration?api-version=7.1`, returning
backlog category mappings (task, requirement, portfolio, bug) with their
associated work-item types.

### Work-item type metadata

`GetWorkItemTypesWithStatesAsync()` fetches every work-item type and its
state definitions, including state category (`Proposed`, `InProgress`,
`Resolved`, `Completed`, `Removed`) and colour.

State ordering uses a stable sort: `OrderBy(CategoryRank).ThenBy(OriginalIndex)`.

### Process description routes

`AdoProcessDescriptionSource` serves `twig process description` — and, since AB#241,
the `twig_process_description` MCP tool, which goes through the SAME assembler and the
SAME projection so both surfaces emit a byte-identical document. See
`docs/architecture/mcp-server.md` § "The process description surface". It is a
**separate class from `AdoIterationService`, deliberately**: that service memoizes
every route it calls, and the description must not cache anything — a stale
description is a wrong description, and the artifact is a truth claim about a
process at a moment in time.

🔴 **On this route family the api-version selects the response *schema*, not just
the route version.** The same URL at two neighbouring preview versions returns
disjoint attributes with identical row counts, so a version slip is invisible in
the count and shows up only as silently blank data. Every version is named from a
constant in `AdoApiVersions`; never inline a literal.

| Route | Pinned version | What that version buys |
|-------|----------------|------------------------|
| `_apis/projects/{project}?includeCapabilities=true` | `7.1` | `processTemplate.templateTypeId` — the id every process route is keyed by |
| `_apis/work/processes/{id}/workItemTypes` | `7.1-preview.2` | `referenceName` + `customization`; preview.1 returns `id` + `class` instead |
| `.../workItemTypes/{ref}/fields` | `7.1-preview.2` | `required`, `defaultValue`, `customization` — absent at preview.1 |
| `.../workItemTypes/{ref}/states` | `7.1` | `customizationType`, `order`, `stateCategory`. **preview.2 is rejected on this route** |
| `.../workItemTypes/{ref}/rules` | `7.1` | `makeRequired` actions and their conditions — the **second source of requiredness** |
| `{project}/_apis/wit/workitemtypes?$expand=all` | `7.1` | The **only** source of state transitions (see below) |
| `_apis/wit/fields` | `7.1` | `isPicklist` / `picklistId` — the **only** source of the picklist association (AB#237) |
| `_apis/work/processes/lists/{listId}` | `7.1-preview.1` | A picklist's `items`; the list-all route returns metadata only |
| `.../workItemTypesBehaviors/{ref}/behaviors` | `7.1` | Per-type **backlog-level membership** (AB#238). 🔴 Note the segment — see below |
| `_apis/work/processes/{id}/behaviors` | `7.1-preview.2` | The behaviour **catalogue**: `referenceName`, `name`, `rank`. One call per run |
| `.../workItemTypes/{ref}/layout` | `7.1` | The form layout — pages, sections, groups, controls, each with its `order` |

🔴 **Requiredness is merged from two routes, and reading either alone lies (AB#236).**
The per-type `fields` route reports **unconditional** requiredness only. A field made
mandatory by a rule — *when State = Done → makeRequired* — reads as not-required
there. Verified live: `Custom.WayfinderAnswer` is `required: null` on `fields` while
`/rules` carries a `makeRequired` action for it. A whole-process survey found 59
unconditionally-required fields while every conditionally-required one was invisible
to `fields`. So the document carries `requiredness` (`always` | `conditional` |
`never`) with the conditions attached, never a bare boolean — a boolean cannot
express the conditional case, and gets it wrong in the silent direction.

The rules route stays pinned at **`7.1`, not preview.2** — and as of AB#238 that is
settled rather than deferred. AB#236 recorded a note that preview.2 "additionally
carries `customizationType`", derived from the 0001 endpoint survey, and deferred the
move to the ticket that would need the tag. **Re-probed live 2026-08-12: the two
versions return BYTE-IDENTICAL bodies and GA already carries `customizationType`.**
Both return 54 rules for `Niflheim.Epic` with the keys
`actions, conditions, customizationType, id, isDisabled, name, url` and the same
53-system / 1-custom split. So the tag AB#238 requires costs no version change, and
the shipped `twig process rules` output is untouched. 🔴 Do not "align" this constant
with its preview.2 neighbours on the strength of the survey note — there is nothing
to buy, and a version change here is a behaviour change to a shipped command.

🔴 **Value constraints come from a THIRD source, and guessing them is banned (AB#237).**
No process route carries `allowedValues` or any picklist reference, at any api-version,
with or without `$expand=all`. The association is readable **field-first** off the
org-scoped `_apis/wit/fields`, which reports `isPicklist` on *every* row — and that
explicit negative is what makes the ban on name-matching costless rather than a
sacrifice: the document states "not list-constrained" as a **server fact**, never as an
inference from a field being called `Status` or typed `string`.

So the document carries `valueConstraint` (`list` | `suggested` | `unconstrained` | `unknown`)
with the resolved `allowedValues` attached. Four values and not a boolean, because there are two
distinct ways to overstate and one to understate:

- 🔴 `unknown` (the picklist call failed, or the source contradicted itself) must not collapse
  into `unconstrained`, which would tell a caller the server accepts anything when nobody
  successfully asked — the most dangerous wrong answer, because acting on it fails at the server.
  A field in this state puts `picklists` in its type's `unfetched` list, and that label is derived
  from the RESOLVED answers rather than from whether the call came back, so a *partial* failure is
  labelled too.
- 🔴 `suggested` (ADO's `isPicklistSuggested`) must not collapse into `list`. A suggested picklist
  offers its values in the web editor while the server enforces nothing, so calling it a
  constraint tells a caller its write must come from the list when it need not. The values are
  still carried — they are true — but the claim attached to them is weaker. Both the field row's
  `isPicklistSuggested` and the list's own `isSuggested` are read, and on disagreement the weaker
  claim wins.

Three separate ways of not knowing — an absent `isPicklist` key, `isPicklist: true` with no
`picklistId`, and a list that would not resolve — all land on `unknown` rather than on the
positive negative. `isPicklist` and `isPicklistSuggested` are modelled as **nullable** bools for
that reason: a non-nullable `bool` deserializes an absent key to `false`, and `false` is consumed
as a stated server fact, so a version drift would manufacture the explicit negative out of nothing.

`picklistId` is a **conditional key**: the server omits it entirely rather than sending
`null` when `isPicklist` is false. Reading only `picklistId` is how the original endpoint
survey concluded this route did not carry the association at all. Values cost one call per
**distinct** list — the list-all route returns metadata only, with every entry carrying
`items: []`.

The join to the type-scoped field list is `OrdinalIgnoreCase`, like every other
reference-name match in this layer. An exact join would drop a real constraint over a
casing difference and report a list-backed field as unconstrained — byte-identical to a
field that genuinely is, and carrying no `unfetched` label to catch it.

> ⚠️ **The org's picklist state has changed since the research this ticket rests on.**
> Finding 0005 (2026-08-11) recorded 199 fields with **zero** picklist-backed, and seven
> orphan lists backing nothing. Probed live during AB#237's implementation: 200 fields with
> **seven** picklist-backed, every one of the seven lists now bound, and the server's own
> validator rejecting a junk value into `Custom.WayfinderExecutionMode` — the exact probe
> that was *accepted* in 0005. The orphan-picklist defect 0005 flagged has been fixed in the
> Niflheim process. This changes the ticket's *illustration*, not its design: the honesty
> constraint and the ban on name-matching hold either way, and the implementation reads the
> server rather than assuming either state.

🔴 **Behaviour membership needs TWO routes, and the obvious one does not exist (AB#238).**
Per-type membership lives at `.../workItemTypesBehaviors/{ref}/behaviors` — note the
segment. The natural `.../workItemTypes/{ref}/behaviors` returns an **HTML 404** ("the
controller for path … was not found") for every type on both an inherited and a stock
process, probed live 2026-08-11 and re-probed 2026-08-12. Note the shape too: an HTML
page, not this family's usual count-shaped JSON envelope.

That route returns a **reference only** — `{"behavior":{"id":"Custom.3daa…"},"isDefault":true}`
— and a custom backlog level's reference name is a GUID. A document carrying the edge
alone would report that a type belongs to `Custom.3daa3b35-2574-4c94-b260-0d15fe6db82f`:
true, unreadable, and worthless in a diff between two processes whose levels have
different ids and the same name. So the **process-scoped catalogue** supplies the name
and rank, fetched once per run (it is the same answer for every type) and joined
`OrdinalIgnoreCase`.

An unresolved membership keeps its reference name and loses only its NAME, with
`behaviourCatalogue` added to the type's `unfetched` list. Dropping the membership
entirely would let a real difference — this type is on a backlog level, that one is not —
diff clean. 🔴 A type with **no** memberships gets no label even when the catalogue failed:
it lost nothing, and a false reservation is as much a lie as a missing one.

🔴 **The form layout is the one collection whose ORDER IS ITS CONTENT (AB#238).** Every
other collection in the document is sorted alphabetically on an ordinal key; sorting the
layout that way would be deterministic and **wrong**, because "Description sits above
Acceptance Criteria" is precisely the fact a reader asked for. It is sorted instead on the
server's own explicit `order` key at each level, with the element id as a total tiebreak —
faithful to the form *and* provable. Trusting the array's order was rejected: the server
does not promise it stable, and an order taken from an array is not something a test can
assert. Sections are ordered by **id** because the server gives them no order key; their
ids (`Section1`…`Section4`) *are* the arrangement.

🔴 **The layout DTO needs a deliberate count-shaped-body guard, and this is the AB#237
defect class in its new home.** A count-shaped 404 carries none of the layout DTO's keys
and `System.Text.Json` ignores unmapped members, so unlike the sibling LIST fetches it does
**not** throw — those survive it only by accident, because their `value` is a `List<T>` and
the error body puts an object where the array belongs. The layout response is a bare object,
falls outside that accidental defence, and untreated would report the type's form as
**empty**: a positive claim built on a call that failed. The guard is the presence of
`pages`, keyed on presence rather than emptiness so a layout that genuinely serves no pages
stays distinguishable from one that could not be read.

🔴 **Rules are carried WHOLE, inherited ones included (AB#238), and that reverses the
obvious call.** `Niflheim.Epic` and `Niflheim.Issue` carry 54 rules each of which 53 are
system plumbing and 1 was authored; `Niflheim.Grilling` carries 2, both authored. So a
verbatim carry is ~95% noise on exactly the types a caller most often asks about, and
filtering the inherited ones out is tempting and wrong: **a difference that exists only in
the omitted part diffs clean.** A reader who wants only the authored rules can filter a
complete document; a reader handed a filtered one cannot recover what was dropped or tell
that anything was. Every rule carries its `customizationType`, which is what makes that
filtering available *to the reader* — the intended way to pay the noise cost. An absent tag
is `unknown` and never `system`: mislabelling an authored rule as inherited plumbing invites
the reader's own filter to discard it, undoing the ruling from the far end.

🔴 **The layout is carried WHOLE, including the parts it is easy to reach and then drop.**
Every level's `visible`, `inherited`, `isContribution` and `order` reaches a cell, and so do
the `systemControls` the same response returns alongside `pages`. Independent review caught an
earlier draft carrying the page flags only for pages with *no* controls, the group flags
nowhere at all, `order` at no level, and `systemControls` deserialized and then discarded — so
a process that hid a group, hid a populated page, or marked one inherited-vs-authored
differently produced a **byte-identical document**. That is the "a difference that exists only
in the omitted part diffs clean" failure this feature exists to prevent, living in the renderer
rather than the assembler, and it is the reason there is now a command-layer test asserting the
new rows reach the file: the domain tests cannot see the render tree.

🔴 **One reservation survives the completeness audit: `ruleIdentity`.** The rule's
server-assigned `id` is reachable and deliberately not carried — it is a per-process GUID, so
two processes defining the same rule carry different ids and including it would make every rule
diff dirty and bury the real differences. That reasoning is a build **judgement**, not a
ruling, and S3 says carry everything and *mark* rather than filter — so it is declared in
`KnownGaps` where a reader sees it, not left in a doc comment where only a maintainer would.
An empty list would have claimed the document omits nothing reachable, which would have been
false.

🔴 **A LOCKED system type answers the layout route with 400, not 404.** `TestCase`,
`TestPlan` and `TestSuite` are locked in this process, and the layout route returns
**`400 VS403115`** — *"you cannot modify form layout information for work item types … as
these work item types are locked"* — where every other failure on this family is a 404 or a
count-shaped body. An unhandled `AdoBadRequestException` there propagated out of
`GetTypeDetailAsync` and **killed the entire description**: 14 types lost to one type's
answer, with a green 7,879-test suite behind it. Found by running the command live, because
the seam tests drive a scripted source that never returns a 400.

It is swallowed rather than re-raised because it **is an answer**: the process will not serve
a layout for that type, ever, which is a fact about the type rather than a transport failure.
Reported as `formLayout` unfetched — the honest weaker claim, since this layer cannot
distinguish "locked" from "call failed" and an empty layout would assert the form has no
pages. 🔴 Verified live that the hazard is **layout-only**: the same locked type serves 55
rules, 3 states, 49 fields and 0 behaviours normally, so a blanket 400-swallow across the
fetch layer would be wider than the evidence supports.

Three findings that constrain this layer, all probed live:

- **The modern process API serves no transitions route.** `.../transitions` and
  `.../stateTransitions` return an HTML 404 at every version, and no `$expand` on
  the process type list carries them. Transitions come from the classic
  project-scoped `wit` route, which narrows the description to the process the
  *configured project* runs on. Deriving them from the state list is not safe —
  4 of 20 types probed are not fully connected.
- **The two routes disagree on a derived type's name.** A derived type is
  `Niflheim.Epic` on the process routes and `Microsoft.VSTS.WorkItemTypes.Epic` on
  the transitions route. Matching on the process name alone reports zero
  transitions while exiting 0. The parent reference name is threaded through as a
  fallback, guarded so a type that genuinely customised its workflow reports its
  transitions as unfetched rather than borrowing the parent's.
- **`$expand=all` is a trap** on the process type list: it returns *fewer* keys
  than `$expand=states`, silently dropping states and behaviors. Use named expands.

🔴 A 404 from these routes arrives with a **count-shaped body**
(`{"count":1,"value":{"Message":…}}`) — exactly the shape of a thin success. Every
fetch returns `null` on failure rather than an empty collection, and partial
failures are named in the document's `unfetched` list, so "could not ask" is never
laundered into "this has nothing".

🔴 **The whole-process path fans out CONCURRENTLY and the fan-out is BOUNDED
(AB#238, guarded by AB#239).** No type argument means every type, one document —
Implementation Decision 3, whose decisive argument is not convenience: *a per-type
document cannot express a type's ABSENCE*, and a type present in one process and
missing from another is exactly the difference the comparison case exists to find.

Parallelising the independent per-type fetches is the **ruled** latency mitigation,
not an optimisation, so it cannot be removed. But "parallelise harder" is the obvious
wrong reading. Each type's detail call issues **five** concurrent GETs (fields,
states, rules, behaviours, layout), so an ungated projection over this process's
14 types is ~70 in-flight requests plus the picklist fan-out alongside — a 429
generator. Throttling degrades exactly the answers the document exists to make
trustworthy: a throttled call comes back as an `unfetched` label, which is honest
*but* is a worse document. The bound is
`ProcessDescriptionAssembler.MaxConcurrentTypeFetches` (currently 4), declared once
and referenced by the test rather than copied into it — a test carrying its own
literal passes happily while the two drift apart, which is how a gate quietly stops
being the gate. 🔴 The picklist gate in this class is declared **independently** and
chosen to match; the two can drift, so change them together.

**Measured, not estimated.** The ruling *accepted* ~20 s as a serial ceiling — a limit
the build must not exceed, never a target. Measured live on 2026-08-12,
`twig process description -o json --out <file>` against the Niflheim process:
**14 types, 508,793 bytes, ~2.6-3.4 s wall** including process startup. So the ceiling
is no longer a live constraint and there is no latency argument left for raising the
bound. 🔴 That byte count supersedes the spec's "~1 MB across the 14 types" estimate
in Decision 8 — that figure predates the build and was an estimate; this is the
emitted file. The spec's *reason* for wanting a short human rendering stands
regardless, since half a megabyte is no more readable than one.

🔴 **Concurrency must not reach the ORDERING, and that is a property of where the sort
sits.** `Task.WhenAll` preserves *input* order in its result array regardless of which
task finished first, and the assembler re-sorts after the gather rather than appending
results as they arrive. That is what makes the reverse-completion assertion meaningful
rather than accidental — and it is why `IProcessDescriptionSource` exposes per-type
detail as one awaitable call per type: a test can drive **completion order** explicitly
through gates and assert byte-identity, instead of asserting on wall-clock timing,
which the spec forbids as flaky theatre. 🔴 That assertion is made on a roster **larger
than the bound** as well as a small one: under a roster that fits inside the gate the
semaphore never makes a scheduling decision, so the small-roster test proves
byte-identity for an *ungated* fan-out only.

### Template detection

Two-phase approach:

1. **API:** `GET /_apis/projects/{project}?includeCapabilities=true` → extract
   `ProcessTemplate.TemplateName`.
2. **Heuristic fallback:** Inspect fetched work-item type names:
   - "User Story" → Agile
   - "Product Backlog Item" → Scrum
   - "Requirement" → CMMI
   - Default → Basic

---

## 5. Conflict Resolution

### Error handler

`AdoErrorHandler.ThrowOnErrorAsync()` maps HTTP status codes to typed
exceptions:

| Status | Exception | Notes |
|--------|-----------|-------|
| 400 | `AdoBadRequestException` | Reads error message from body |
| 401 | `AdoAuthenticationException` | |
| 404 | `AdoNotFoundException` | Extracts work-item ID via regex |
| **412** | **`AdoConflictException`** | Parses server revision from body |
| 429 | `AdoRateLimitException` | Reads `Retry-After` header (default 10 s) |
| 5xx | `AdoServerException` | |

All inherit from `AdoException`. Network failures and timeouts become
`AdoOfflineException`.

### ConflictRetryHelper

`PatchWithRetryAsync(adoService, id, changes, expectedRevision, ct)`:

1. **Attempt 1:** Patch with expected revision.
2. **On 412:** Fetch the latest item to get the server's current revision.
3. **Attempt 2:** Patch again with the fresh revision.
4. **On second 412:** Exception propagates — no further retries.

This is a single-retry strategy; it handles the common case where another
actor (e.g. an ADO rule) bumped the revision between our fetch and patch.

### Revision parsing

`AdoConflictException` exposes `ServerRevision` (int), parsed from the error
body via regex `revision[:\s]+(\d+)` (case-insensitive). Returns 0 if
parsing fails.

---

## 6. Link Management

### Domain model

```csharp
public readonly record struct WorkItemLink(int SourceId, int TargetId, string LinkType);

public static class LinkTypes
{
    public const string Related     = "Related";
    public const string Predecessor = "Predecessor";
    public const string Successor   = "Successor";
}
```

`IWorkItemLinkRepository` provides `GetLinksAsync` / `SaveLinksAsync` backed
by SQLite.

### ADO relation type mapping

| ADO `rel` value | Domain `LinkType` |
|-----------------|-------------------|
| `System.LinkTypes.Hierarchy-Reverse` | Parent (stored as `WorkItem.ParentId`) |
| `System.LinkTypes.Related` | `Related` |
| `System.LinkTypes.Dependency-Forward` | `Successor` |
| `System.LinkTypes.Dependency-Reverse` | `Predecessor` |

Parent links are extracted by `AdoResponseMapper.ExtractParentId(relations)`,
which finds the hierarchy-reverse relation and parses the ID from the URL
suffix. Non-hierarchy links are extracted by `ExtractNonHierarchyLinks()`.

### Add link

`AddLinkAsync` sends a JSON Patch with `op: "add"`, `path: "/relations/-"`,
and a value containing the rel type and target URL:

```json
{
  "rel": "System.LinkTypes.Related",
  "url": "https://dev.azure.com/{org}/_apis/wit/workitems/{targetId}"
}
```

### Remove link

`RemoveLinkAsync` is a two-step operation:

1. **GET** the source item with `$expand=relations` to find the relation
   index and current revision.
2. **PATCH** with `op: "remove"`, `path: "/relations/{index}"`,
   and `If-Match: {rev}`.

The operation is idempotent — if the link doesn't exist, it returns silently.

---

## Iteration Service

`AdoIterationService : IIterationService` provides additional ADO metadata
queries beyond work items:

| Method | Endpoint |
|--------|----------|
| `GetCurrentIterationAsync` | `/{project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current` |
| `GetTeamAreaPathsAsync` | `/{project}/{team}/_apis/work/teamsettings/teamfieldvalues` |
| `GetAuthenticatedUserDisplayNameAsync` | `https://app.vssps.visualstudio.com/_apis/profile/profiles/me` |
| `GetWorkItemTypesWithStatesAsync` | `/{project}/_apis/wit/workitemtypes` |
| `GetFieldDefinitionsAsync` | `/{project}/_apis/wit/fields` |
| `GetProcessConfigurationAsync` | `/{project}/_apis/work/processconfiguration` |

Results are lazily cached as tasks for the lifetime of the service instance
(singleton scope). Failures degrade gracefully — most methods return null or
empty lists rather than throwing.
