# Connection, Bench, and Context

Twig's domain model uses three nouns for *where work is organized*. The
Connection and Bench concepts are present in the current CLI; the
caller-addressable Context is a settled design record that is not yet exposed
as a CLI resource. They stack conceptually:

1. **Connection** — one `{org}/{project}` Azure DevOps endpoint, with its
   cache, credentials, and pending set.
2. **Bench** — a named, durable, saved backlog of work items you return to.
3. **Context** — the planned disposable place to stand, holding only the
   active item and what derives from it.

This page distinguishes current commands from that design direction, explains
why `twig set` is a local pointer and never a claim, and records the rules
future Context work must preserve.

The naming is settled by wayfinder tickets
[`0022-bench-and-context.md`](../../wayfinder/tickets/0022-bench-and-context.md)
and
[`0023-context-addressing.md`](../../wayfinder/tickets/0023-context-addressing.md),
and mirrored in [`CONTEXT.md`](../../CONTEXT.md) §4.

> **A note on "Workspace".** The word `Workspace` historically named three
> unrelated things in twig — a read model, the `.twig/` directory, and an
> MCP routing key (see `CONTEXT.md` §4). It was retired for the three nouns
> above. The `twig workspace` command survives as the view rendered on top
> of the current Bench; the `.twig/` directory survives as the on-disk
> per-Connection cache. When you read "workspace" in older material, check
> which meaning is intended.

---

## The three levels

### Connection — the ADO endpoint and its local mirror

A Connection is one `{org}/{project}` pair
(`src/Twig.Mcp/Services/Connection.cs:11`). It owns:

- The local SQLite cache at `.twig/{org}/{project}/twig.db`
  (`docs/architecture/data-layer.md`).
- The auth material used to talk to that org (see
  [`auth login`](../commands/system/auth-login.md) and
  [`auth status`](../commands/system/auth-status.md)).
- **The pending set** — every unpushed field change, note, and seed. This
  is the sync unit: reconciliation scopes to the pending set per
  Connection, and *not* per Bench (ticket 0022 §7, `CONTEXT.md` §4).

A single machine can carry several Connections; twig switches between them
based on where it is invoked from. Nothing about a Bench or a Context
crosses a Connection boundary — a Bench in `contoso/apps` is not visible
from `contoso/infra`, and a pending edit in one Connection is never
flushed as a side effect of standing in the other.

### Bench — a named, durable backlog

A **Bench** is an arrangement of work you name once and return to: *my
sprint*, *the bugs I own*, *release blockers*. Several Benches can exist
side by side; you select one, and it is never derived
(`src/Twig.Domain/Aggregates/Bench.cs:22`).

A Bench holds **selectors**, and only selectors
(`src/Twig.Domain/Aggregates/Bench.cs:40`). A **pin** is a selector that
matches one item; a **subtree pin** is a selector that matches an item and
its descendants as they are now; a **query** is a selector that matches a
body of work. They are one mechanism differing only in how many items they
match (`CONTEXT.md` §4). A Bench stores the *rule*, never the results, and
its membership is the order-free **union** of its selectors — two Benches
holding the same selectors show the same items.

Selectors are evaluated against the **local cache**, not against ADO. A
query selector carries an ADO query as a *refresh rule* that describes how
items reach the cache, never as the thing run when somebody looks at their
Bench. This is what keeps seeds and unpushed edits visible on every Bench:
ADO cannot see either, so a server-side answer to "what is on my Bench"
could never include them (`docs/specs/bench.spec.md` §"Selectors are
evaluated against the local cache").

Two rules follow from that shape:

- **Shared view — no private pins.** Everything standing on a Bench sees
  the same Bench. A pin is a change to the Bench, visible to everyone
  standing on it (ticket 0022 §5).
- **Exclusions are out of the Bench entirely** (decided 2026-08-06). There
  is no subtracting selector, and the top-level
  [`workspace exclude`](../commands/workspace/exclude.md) group
  continues to own hiding items from the workspace view.

⚠ **A Bench does not "reconcile" Contexts.** `Reconciliation` is the
staged → published → reconciled → invalidated module against ADO
(ticket 0022 §8). What a Bench does is **merge views for display**: the
same item reached by different routes is shown once.

There is always a **default Bench** per Connection — the one twig creates
on its own, reconstructed from the working set that predates the pivot
(`src/Twig.Domain/Aggregates/Bench.cs:34`, `CONTEXT.md` §4). Manage the
rest through the [`bench`](../commands/bench/README.md) group:

- [`bench create`](../commands/bench/create.md) — name a new arrangement.
- [`bench list`](../commands/bench/list.md) — see what exists, marked with
  the current one.
- [`bench switch`](../commands/bench/switch.md) — put one down and pick
  another up.
- [`bench delete`](../commands/bench/delete.md) — remove one; a Bench that
  holds selectors refuses without re-typing the name into `--confirm`.
- `bench configuration` — inspect or edit Bench-local exact/under areas and
  relative/absolute sprints, without changing workspace-global configuration.
  Saved ownership stays unchanged; pins, seeds and pending work remain additive.
  See [configuration semantics and captured guards](../commands/bench/README.md#configure-the-current-bench).

### Context — planned caller-addressable resource

The Context described by tickets 0022 and 0023 is a place a caller would
open, work in, and close. It would hold only where the caller is: an active
item plus its derived parent chain, children, and navigation history. That
model must not be mistaken for the current CLI's single active-item pointer:
there is no current Context open/close command, handle, or per-caller state.

The design puts concurrency at the Context level and keeps Benches
switchable. It specifies one default Context per Connection, plus explicit
handles for additional Contexts. These are future-surface constraints, not
current options accepted by `twig set`, `twig show`, or `twig workspace`.

---

## `twig set` is a local pointer, never a claim

The [`twig set`](../commands/context/set.md) command chooses which work
item other commands operate on. It is:

- **Purely local.** `set` writes the active work item ID via
  `IContextStore`, records a visit in the navigation history, and updates
  the shell prompt state — nothing more
  (`src/Twig/Commands/SetCommand.cs:115-118`,
  `src/Twig.Domain/Interfaces/IContextStore.cs:14`).
- **Not a claim on ADO.** Nothing is pushed. No assignee is changed, no
  state is transitioned, no lock is taken out. Another person on another
  machine can `set` the same item with no interaction and no conflict.
- **Not a sync.** `set` never loads children, parents, links, or field
  definitions and never runs a working-set sync
  (`docs/commands/context/set.md`).

`set` is how you *point* the current active-item pointer at a work item. If
you want to claim work you own on the ADO board, transition the item and update
its assignee through the mutation flow (see the
[`context`](../commands/context/README.md) group and the plan/proposal
path, not `set`).

Two subtleties fall out:

- **A numeric ID that is not cached is fetched from ADO** so `set` can
  point at it. A title pattern searches cache only. Both writes are still
  local: the fetch fills the cache, the pointer flip is a local write
  (`docs/commands/context/set.md`).
- **`set` does not choose a Bench.** Changing the active work item and
  changing the current Bench are separate acts:
  [`bench switch`](../commands/bench/switch.md) moves the Bench pointer,
  [`set`](../commands/context/set.md) moves the active-item pointer.

---

## Design vocabulary: standing commands vs targeted commands

Ticket 0022 distinguishes intended command shapes:

- **Standing commands** would need a caller Context. Examples include `tree`,
  `nav`, and an untargeted `set`; they read where the caller stands.
- **Targeted commands** name their own work item. They need a Connection and
  nothing else. A targeted read must not create or mutate a Context as a side
  effect.

This distinction is a design constraint on a future Context surface. Current
CLI commands instead use the existing active-item pointer where applicable;
MCP tools already name target work items (see
[`docs/architecture/mcp-server.md`](../architecture/mcp-server.md)).

The design preserves two safety rules: reading one work item does not join a
Bench, and a targeted read must not mutate a Bench as a side effect.

---

## Current CLI behavior

Twig's present CLI has a single active-work-item pointer in `IContextStore`.
`twig set` changes that pointer locally; it does not claim work, write to ADO,
or open a caller-addressable Context (`src/Twig/Commands/SetCommand.cs:115-118`,
`src/Twig.Domain/Interfaces/IContextStore.cs:14-25`). Targeted commands that
name an item act on that target. Do not pass a Context handle or expect
machine-format Context enforcement: the current command surface does not
expose either.

The implemented **Bench** is durable: it saves a named backlog view and its
selectors. Switching Benches changes the view, not pending ADO work. The
current local pointer remains separate from a claim or assignment mechanism.

### Semantic Bench browser contract

`twig workspace --view tree -o json --include-browser` adds a `browser` object
to the existing workspace JSON; without the flag, the existing output is
unchanged. It uses the same native cached hierarchy, process metadata,
working-level ancestor pruning, ordering and configured descendant depth.
Shared ancestors are merged across selector origins and assignees. Ancestor
records contain their actual cached work-item fields, never fabricated
identities or states. Virtual section headings are not selectable work items
and are not emitted as fake nodes.

The version-1 envelope has these required fields:

```typescript
interface BrowserSnapshot {
  version: 1;
  benchId: string; // invariant-culture native storage ID
  benchName: string;
  bindingId: string; // actual admitted native binding
  identityId: string; // actual admitted registered principal
  worktreeRoot: string; // actual attached native checkout
  roots: BrowserNode[];
}
interface BrowserNode {
  key: string; // stable occurrence key, currently item:<id>
  id: number;
  title: string;
  type: string;
  state: string;
  label: string; // native badge/color/tree styling, one terminal-safe row; ANSI allowed
  isSeed: boolean;
  pins: ("single" | "tree")[]; // explicit selectors on this ID only
  owningSubtreeIds: number[]; // ancestor subtree selectors actually matched during evaluation
  membership: "ancestor context" | "bench member" | "seed" | "pending" | "subtree";
  children: BrowserNode[];
}
```

Seeds and pending edits remain visible even when no selector matches them.
`pins` never infers an explicit pin from inherited membership. A subtree root
is explicit; its descendants name that root in `owningSubtreeIds`, and a
nested subtree can name multiple owning ancestors. Native label rendering
reuses canonical type badges and state colors; controls supplied inside work
item content cannot inject terminal commands or additional rows.

Browser selection and folding are local UI state, not `twig set`. Browser pin
actions call `workspace track <id>` or `workspace track-tree <id>` with
`--expect-bench <benchId>`. `workspace untrack <id>` removes **both** explicit
single and subtree selectors; it never removes an ancestor's subtree selector
or prevents a query from continuing to match the item. Seeds cannot be pinned.
Pin changes are durable local Bench selector edits and never write to ADO.

The browser also passes `--expect-binding <bindingId>` and
`--expect-identity <identityId>` on semantic reads, pin changes and
`workspace sync`. These are expected-origin preconditions, not authentication
selectors: native connection admission and registered credentials remain the
only authority path. A stale Bench, binding or identity refuses the operation
before its reads or mutations. A pin operation resolves its current Bench once
and uses that captured storage ID for both removals, even if another caller
switches the Bench pointer while it runs.

`twig workspace sync` is a pull-only, Bench-scoped refresh of its query and
pin selectors plus bounded configured context relationships. It never flushes
pending writes and does not change the behavior of ordinary `twig sync`.
Browsers package a private native companion alongside the UI for semantic
reads, pin changes and bounded refresh; they do not replace the user's normal
Twig executable or derive item identity by scraping terminal output.

---

## Design record: Context addressing

Ticket 0023 records the intended Connection → Bench → Context model for a
future caller-addressable Context surface. It is useful terminology and a
constraint on future work, but the following rules are **not current CLI
behavior**:

| Caller | Intended standing-command default | Intended non-default Context behavior |
|---|---|---|
| human format | default Context for the current Connection | caller names it |
| machine format | hard error without a Context name | caller names it |
| any targeted command | no Context involved | naming one is meaningless |

The design chooses an opaque, resolvable handle over a mutable shared name so
an expired Context fails loudly rather than quietly targeting another one. It
also assigns unpushed work to the Connection, which would let a Context close
without silently discarding it. See ticket 0023 for the settled rationale and
do not treat these design constraints as accepted command-line flags.

---

## Durable Bench selectors and Connection-owned pending work

Benches are durable. Their selectors survive cache rebuilds and describe the
view rather than its materialized results (`src/Twig.Domain/Aggregates/Bench.cs:5-11`,
`docs/specs/bench.spec.md` §"What is NOT the problem"). Switching a Bench
does not migrate selectors or flush pending changes. A Bench is a display
view, never a reconciliation or sync unit.

Pending field changes, notes, and seeds are Connection-scoped in the intended
model. The existing `twig discard` command remains the explicit way to drop
pending work; a Bench switch must not hide work twig still owes ADO. For the
on-disk cache and pending-store implementation, see
[`docs/architecture/data-layer.md`](../architecture/data-layer.md).


---

## Putting it together — a worked walk

A human at a terminal, using the current single active-item pointer and the
selected Bench:

```
$ twig set 4211                 # local pointer flip; no ADO write
Set active item: #4211 Wire retry telemetry [Doing]

$ twig show                     # reads the current active-item pointer
#4211 Wire retry telemetry — Doing — You

$ twig workspace                # view rendered from the selected Bench
Sprint (Iteration \Sprint 42):
  ● #4211  Wire retry telemetry             Doing    You
  …
```

A release goes hot. Save the current arrangement, cut over, and come back
later without losing anything:

```
$ twig bench create "release blockers"
Created Bench 'release blockers'.

$ twig bench switch "release blockers"
Now on Bench 'release blockers' (was 'default').

$ twig workspace track 5090     # pins onto the current Bench
$ twig bench switch default
Now on Bench 'default' (was 'release blockers').
```

The caller-addressable Context contract from ticket 0023 is deliberately not
shown as a runnable CLI example: no current command accepts a Context handle
or changes behavior based on machine output format.

---

## See also

**Commands**

- [`bench`](../commands/bench/README.md) — create, list, switch, delete Benches.
- [`context`](../commands/context/README.md) — `set`, `show`, `show-batch`, `query`, `web`, `history`.
- [`workspace`](../commands/workspace/README.md) — the view rendered on the current Bench.
- [`workspace track`](../commands/workspace/track.md) /
  [`workspace exclusions`](../commands/workspace/exclusions.md) — pin and hide items.
- [`auth login`](../commands/system/auth-login.md) /
  [`auth status`](../commands/system/auth-status.md) — Connection credentials.

**Architecture**

- [`architecture/overview.md`](../architecture/overview.md) — the CLI, MCP, and TUI surfaces.
- [`architecture/data-layer.md`](../architecture/data-layer.md) — SQLite cache, pending set, and sync.
- [`architecture/mcp-server.md`](../architecture/mcp-server.md) — how MCP addresses Connections and targets.

**Design sources**

- [`wayfinder/tickets/0022-bench-and-context.md`](../../wayfinder/tickets/0022-bench-and-context.md) — the three nouns and the staged build order.
- [`wayfinder/tickets/0023-context-addressing.md`](../../wayfinder/tickets/0023-context-addressing.md) — how a caller names its Context.
- [`CONTEXT.md`](../../CONTEXT.md) §4 — the retired `Workspace` term and the vocabulary that replaced it.
- [`docs/specs/bench.spec.md`](../specs/bench.spec.md) — the Bench specification.
