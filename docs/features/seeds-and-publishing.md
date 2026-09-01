# Seeds and publishing

Seeds are the local-only draft mechanism for composing Azure DevOps work items —
one, a chain, or an entire dependency graph — before sending anything to ADO.
Every seed carries a negative ID, lives in the workspace SQLite cache, and stays
invisible to `twig sync`, board queries, and every other ADO code path until it
is explicitly published.

This page covers the whole seed lifecycle end-to-end: the local model, the safety
boundary between the workspace cache and ADO writes, ordered publication, and
the recovery paths when a publish batch does not finish cleanly. For per-flag
detail on any command, follow the links to the seed command reference under
[`docs/commands/seeds/`](../commands/seeds/README.md).

## What a seed is

A seed is not a distinct work-item type. It is an ordinary `WorkItem` with two
distinguishing traits:

- `WorkItem.IsSeed = true`
- A negative `Id`, minted from a durable per-workspace identity register

Both live in the shared `work_items` table in the workspace database at
`.twig/{org}/{project}/twig.db`. The negative ID is a sentinel that keeps seeds
addressable everywhere twig accepts an ID (`twig set -42`, `twig show -42`,
`twig seed link -42 5678`) without ever colliding with a real ADO ID, which is
always positive. Supporting tables round out the model:

- `seed_links` — virtual typed links between seeds, or between a seed and a
  published item, that cannot yet exist as ADO relations.
- `publish_id_map` — the durable record of "seed `#-42` became ADO `#7842`",
  written at publish time and consulted by `seed reconcile` when local rows
  reference an ID that has since been published.
- `pending_changes` — staged edits and notes carrying a FK to `work_items(id)`.
  This is why the publish transaction is careful about ordering (see below).

Seeds share the same aggregate as ADO items, so almost every read command works
against them without special-casing: `twig show`, `twig tree`, and the seed
dashboard all render seeds and published items side by side. The one bright
line is mutation. `twig sync` and dirty-item detection deliberately skip
`is_seed = 1` rows — a seed cannot become dirty because it has no upstream to
be behind, and it must not be pushed by a general sync path.

Reference: [architecture overview](../architecture/overview.md),
[data layer](../architecture/data-layer.md), and the terminology entries under
"Seeds" in [`CONTEXT.md`](../../CONTEXT.md).

## The lifecycle at a glance

```
create ─► edit / chain ─► link ─► validate ─► publish ─► (reconcile if needed)
   │                                                              ▲
   └────────── discard (local-only, cascades to descendants) ──────┘
```

The state boundary is `seed publish`. Everything to its left is a workspace
cache mutation with no network traffic. `seed publish` is the only seed command
that talks to Azure DevOps, and it does so through the transactional
orchestrator described below.

## Authoring: create, edit, chain

- **[`seed new`](../commands/seeds/seed-new.md)** creates a single seed. The
  parent defaults to the active work item; pass `--parent <id>` for an explicit
  parent, or `--no-parent` for an orphan (which then requires `--type`). Whether
  the parent was explicit or inferred is recorded — an explicit parent writes an
  extra `parent-child` row in `seed_links` alongside `WorkItem.ParentId`, and
  `seed validate` uses the difference between the two stores to distinguish
  "chosen" from "inferred" later.
- **[`seed edit`](../commands/seeds/seed-edit.md)** opens the seed in the
  external editor, diffs the parsed buffer field-by-field against the on-disk
  row, and re-saves only when at least one field differs.
- **[`seed chain`](../commands/seeds/seed-chain.md)** creates several seeds at
  once and wires a `successor` link between consecutive pairs. Reach for it
  when you know the ordered breakdown of a piece of work up front.
- **[`seed view`](../commands/seeds/seed-view.md)** renders the seed dashboard —
  every local seed grouped under its parent (or in an "Orphan Seeds" section)
  with per-seed completeness ratios and freshness. Use it as the single-screen
  check before validating and publishing.
- **[`seed discard`](../commands/seeds/seed-discard.md)** deletes a seed **and
  its descendant seeds** through the parent-child link table. This cascade is
  deliberate: a partial delete would leave a broken `ParentId` chain that
  `seed reconcile` cannot repair, because reconcile can only remap known
  publishes, not resurrect discarded rows. The prompt shows the full blast
  radius before anything is written.

Both `seed new` and `seed chain` reject unknown field reference names up front,
because ADO silently drops unknown fields at create time and the loss would only
surface after publish.

## Links: virtual edges before ADO exists

Because both endpoints of a link may not exist in ADO yet, seeds carry their own
link table. A **virtual link** is a row in `seed_links` connecting two items by
ID with a typed edge, where at least one endpoint is a seed (negative ID). Two
published items cannot be joined here — that belongs in ADO proper — so
[`seed link`](../commands/seeds/seed-link.md) rejects pairs where both IDs are
positive.

Recognized link types:

| Type | Reverse | Promotes to (ADO relation) |
|---|---|---|
| `parent-child` | — | `System.LinkTypes.Hierarchy-Forward` |
| `blocks` | `blocked-by` | `System.LinkTypes.Dependency-Forward` |
| `blocked-by` | `blocks` | `System.LinkTypes.Dependency-Reverse` |
| `depends-on` | `depended-on-by` | `System.LinkTypes.Dependency-Reverse` |
| `depended-on-by` | `depends-on` | `System.LinkTypes.Dependency-Forward` |
| `related` | — | `System.LinkTypes.Related` |
| `successor` | `predecessor` | `System.LinkTypes.Dependency-Forward` |
| `predecessor` | `successor` | `System.LinkTypes.Dependency-Reverse` |

Link management commands:

- **[`seed link`](../commands/seeds/seed-link.md)** — create a virtual link.
  Dependency types (everything except `related` and `parent-child`) run through
  eager cycle detection: the proposed edge is added to the in-memory graph and
  any cycle is rejected with the offending ID list. `parent-child` reparents
  atomically — the new parent row is written **before** the stale ones are
  removed and `WorkItem.ParentId` is rewritten, so a mid-way failure leaves the
  original correct parent intact rather than a dangling `ParentId`.
- **[`seed unlink`](../commands/seeds/seed-unlink.md)** — remove a virtual link;
  idempotent.
- **[`seed links`](../commands/seeds/seed-links.md)** — list current links,
  optionally filtered by an ID.

Links to positive-ID items that are not in the local cache are allowed but
emit an info warning: the target might be a published ADO item twig has not
seen. They will be resolved at publish time when the ADO item is fetched, or by
`seed reconcile`.

## Validation: the gate before publishing

**[`seed validate`](../commands/seeds/seed-validate.md)** runs `SeedValidator`
against one seed or every seed in the workspace and returns non-zero when any
rule fails, so it is safe to gate scripts on. Rules come from
`.twig/seed-rules.json` if present, otherwise defaults; the ruleset is
intentionally small (required fields, parent requirement) and configurable per
workspace.

Validate covers two categories:

- **Canonical field invariants** (always enforced, even for `seed publish
  --force`). `System.Title` present, sprint-entry policy respected, and any
  other structural invariants the reference profile enforces. Forcing exists to
  bypass repository publish rules, not reference-process invariants.
- **Configurable publish rules** — the ones a repository owner chose to require
  before promoting drafts to real board items.

Validation is a read-only operation and never mutates. Use it liberally.

## The publish flow

**[`seed publish`](../commands/seeds/seed-publish.md)** is the only seed command
that writes to Azure DevOps. It comes in single-seed and batch forms.

### Single-seed publish

The orchestrator (`SeedPublishOrchestrator.PublishAsync`) walks a deliberate
sequence — the ordering matters for retry safety, so it is worth understanding:

1. **Guard.** ID must be negative and the row must be `IsSeed`. Positive IDs are
   short-circuited to `Skipped`.
2. **Parent resolution.** The parent link table is consulted so an
   explicitly-chosen parent overrides any drifted `ParentId`. If the effective
   parent is still a seed (negative ID), publish refuses — parents must be
   published first.
3. **Canonical invariants + optional rules.** Canonical checks always run;
   configurable rules run unless `--force`. Failure returns
   `ValidationFailed` with per-rule detail.
4. **Dry run short-circuit.** With `--dry-run`, the orchestrator returns a plan
   (`Status = DryRun`) and stops before any ADO or SQLite writes.
5. **Record intent, then create in ADO.** Before the ADO create, the intent is
   written to a durable ledger *outside* the local transaction. The create then
   goes to ADO. The intent is written outside the transaction on purpose: if
   the local half rolls back later, an intent that rolled back with it would be
   erased by exactly the failure it exists to survive.
6. **Idempotent retry check.** Before creating, the orchestrator consults the
   intent ledger for a completed row, then falls back to querying ADO by the
   intent's tag + title + type + recorded timestamp. If a prior attempt already
   landed the item, its positive ID is reused instead of creating a duplicate.
7. **Transactional local update.** In one unit of work:
   - Insert the new positive-ID ADO row.
   - Remap `pending_changes.work_item_id` from the old negative ID onto the new
     positive ID (so staged notes and field edits survive publish and flush on
     the next `twig sync`).
   - Rewrite `seed_links` endpoints for the published ID.
   - Rewrite the `ParentId` of child seeds that pointed at this parent.
   - Record `(oldId → newId)` in `publish_id_map`.
   - Delete the old seed row.
   The insert-before / delete-after ordering exists because
   `pending_changes.work_item_id` has a FK to `work_items(id)`. Getting it
   wrong orphans staged edits and, on retry, duplicates the ADO item.
8. **Promote virtual links to ADO relations.** Best-effort. Link failures are
   logged and counted but do not fail the command — the ADO item is already
   live, and links can be added manually.
9. **Optional branch link.** With `--link-branch`, each published item is
   attached to the named git branch as an ADO artifact link. The repo GUID is
   resolved once up front from the ADO project's Git repository list
   (`--repo <name>`, or the workspace-configured default), so a bad branch name
   fails before the publish loop rather than after. Branch-link failures are
   also best-effort — reported in the summary, not fatal.
10. **Active context follow.** If the active work item was one of the published
    seeds, the active pointer is rewritten to the new positive ID so
    `twig show` continues to point at the same conceptual item.

### Batch publish (`--all`)

`PublishAllAsync` extends the single-seed flow across every unpublished seed in
the workspace:

- **Pre-flight validation** walks the entire dependency graph before touching
  ADO, flagging unpublishable seeds up front rather than mid-batch.
- **Topological sort with cycle detection** (Kahn's algorithm) determines the
  publish order — parents before children, predecessors before successors.
- **Deterministic tiebreaker.** When two seeds are equally ready, the older
  `SeedCreatedAt` wins, so batch order is reproducible.
- Seeds involved in a cycle are reported and skipped; the rest publish in
  order.
- Each seed is re-loaded from the repository immediately before its publish so
  the `ParentId` remap performed by an earlier publish in the same batch is
  visible.

The batch returns `SeedPublishBatchResult` — a per-seed result list plus cycle
and pre-flight error lists. `HasErrors` covers cycle errors, pre-flight errors,
and any `Error` / `ValidationFailed` per-seed status.

## Failure recovery: reconcile and retry

Because publish creates real ADO work items step by step, an interrupted batch
can leave the workspace with a mix of published and still-local items and stale
references pointing at IDs that no longer exist as seeds.
**[`seed reconcile`](../commands/seeds/seed-reconcile.md)** exists exactly for
this. It runs entirely against local SQLite — no ADO calls — and walks two
concerns:

- **`seed_links` rows.** Endpoints that no longer resolve are remapped through
  `publish_id_map` when a mapping exists. When one does not (a peer was
  discarded, or was never in the graph), the link is removed.
- **Seed `ParentId` values.** Negative parents that have since been published
  are rewritten to the new positive ID via the same map. Missing parents with
  no mapping surface as warnings — the parent was discarded, and the child
  needs manual attention.

The command always exits `0`, even with warnings, so it is safe to run
opportunistically. A partial repair with warnings is not treated as an error;
the counts and warnings table tell you what still needs a human decision.

### The 7→10 window and the intent ledger

The single most important recovery guarantee is that a crash *between* the ADO
create in step 5–6 and the local transaction in step 7 does not orphan the ADO
item. The intent ledger records that this seed identity is being published
before the ADO call, and records the ADO ID as soon as the create returns —
both writes outside the transaction. On retry:

1. The ledger is consulted first for a completed row with a `PublishedId`. If
   one exists, that ID is reused.
2. Failing that, ADO is queried by the intent's stamped tag combined with title,
   type, and the intent's own `RecordedAt`. A match is reused; if nothing
   matches, a new item is created.

This is what makes `seed publish` safe to re-run after any interruption. It is
also why `seed reconcile` is a repair tool for local state, not for ADO state:
publish itself is idempotent per intent.

## Cross-cutting behavior

- **Navigation.** `twig set`, `twig show`, `twig tree`, `up`/`down`/`next`/
  `prev` all accept negative seed IDs and traverse via `ParentId` and
  `seed_links` (successor / predecessor). `back` / `fore` resolve published IDs
  through `publish_id_map`, so history from before a publish still lands on
  the item under its new positive ID.
- **Mutations.** `twig update` and `twig state` route on `WorkItem.IsSeed`: on
  a seed they mutate the local row directly; on a published item they queue an
  ADO change through the pending-change store. The user experience is
  identical — the same command, the same arguments — but seed mutations never
  reach the network.
- **Sync.** `twig sync` skips seeds. They do not appear in dirty-item
  detection and are not included in ADO push/pull.
- **MCP parity.** Every seed command except `seed edit` has an MCP tool
  counterpart. `seed edit` requires an external editor and has no meaningful
  MCP mapping. `twig_update` and `twig_state` route to seed mutations
  automatically when the active item is a seed, so no separate MCP tools are
  needed for that path.

## Practical workflows

**Draft a small feature offline, then publish it.**

```
$ twig seed new --title "Batch API rework" --type Feature --no-parent
$ twig set -42
$ twig seed chain "design api,build api,cover api" --type Task
$ twig seed link -43 -44 --type blocked-by
$ twig seed validate
$ twig seed publish --all --link-branch feature/batch-api
```

**Compose under an existing published parent.**

```
$ twig set 5678
$ twig seed new --title "Wire audit trail" --type Task
$ twig seed new --title "Cover audit trail" --type Task
$ twig seed link -42 -43 --type successor
$ twig seed view
$ twig seed publish --all
```

**Recover from an interrupted batch publish.**

```
$ twig seed publish --all
# ... network drops after two of five items land ...
$ twig seed publish --all       # safe to retry: intent ledger prevents duplicates
$ twig seed reconcile           # repair any stale local link/parent references
```

**Preview before publishing.**

```
$ twig seed validate                   # gate on rules
$ twig seed publish --all --dry-run    # topological plan without ADO writes
```

## Failure modes and their fixes

| Symptom | Cause | Fix |
|---|---|---|
| `Parent seed <id> must be published first.` | Attempting to publish a child before its seed parent. | Publish the parent (or use `--all`, which topologically orders). |
| `Would create a cycle: [-42, -43, -44]` on `seed link`. | A dependency-typed link would close a cycle in the graph. | Drop or invert one of the cyclic edges. `related` links do not participate in cycle detection. |
| `ValidationFailed` on publish, but `seed validate` passes. | Canonical invariants (e.g. sprint-entry policy) failed. Canonical checks run even with `--force`. | Fix the invariant — e.g. change type or move off the sprint iteration. |
| Publish "created" items that are missing locally after a crash. | The 7→10 window closed mid-way. | Re-run `seed publish` — the intent ledger reuses the existing ADO IDs. Then run `seed reconcile`. |
| `seed view` shows dangling parent chains after a partial publish. | Some children still point at negative IDs whose seeds were published (and deleted) in a prior attempt. | Run `seed reconcile`; it will remap through `publish_id_map`. |
| Branch-link summary shows `failed N`. | ADO artifact-link API rejected the branch link (bad repo name, permissions). | Non-fatal. Fix `--repo` / branch and re-link manually via ADO, or re-run with a corrected `--link-branch`. |
| `Discard seed X and N descendants?` shows more descendants than expected. | `seed discard` cascades through the parent-child link table. | Cancel and re-parent the descendants you want to keep with `seed link ... --type parent-child` first. |

## The safety boundary, restated

Every seed command *except* `seed publish` is guaranteed local-only: no
authentication is used, no ADO endpoint is called, and the workspace database
is the only mutable surface. `seed publish` is where that boundary is
crossed, once, deliberately, through a single orchestrator whose retry model
is designed to survive network and process failures without duplicating remote
state. When the crossing goes wrong, `seed reconcile` restores the workspace
side to consistency.

## See also

- Seed commands: [`docs/commands/seeds/`](../commands/seeds/README.md)
  - Authoring: [`seed new`](../commands/seeds/seed-new.md),
    [`seed edit`](../commands/seeds/seed-edit.md),
    [`seed chain`](../commands/seeds/seed-chain.md),
    [`seed view`](../commands/seeds/seed-view.md),
    [`seed discard`](../commands/seeds/seed-discard.md)
  - Links: [`seed link`](../commands/seeds/seed-link.md),
    [`seed unlink`](../commands/seeds/seed-unlink.md),
    [`seed links`](../commands/seeds/seed-links.md)
  - Publishing: [`seed validate`](../commands/seeds/seed-validate.md),
    [`seed publish`](../commands/seeds/seed-publish.md),
    [`seed reconcile`](../commands/seeds/seed-reconcile.md)
- Architecture: [overview](../architecture/overview.md),
  [data layer](../architecture/data-layer.md),
  [ADO integration](../architecture/ado-integration.md),
  [commands](../architecture/commands.md).
- Terminology: [`CONTEXT.md`](../../CONTEXT.md) — see "Seeds",
  "PublishIdMap", and "PendingChangeRecord".
- Specification: [seed lifecycle spec](../specs/seed-lifecycle.spec.md) —
  full invariant list and command-by-command behavior.
