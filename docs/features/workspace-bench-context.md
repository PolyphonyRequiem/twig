# Connection, Bench, and Context

Twig has three nouns for *where you are working*. They stack:

1. **Connection** — one `{org}/{project}` Azure DevOps endpoint, with its
   cache, credentials, and pending set.
2. **Bench** — a named, durable, saved backlog of work items you return to.
3. **Context** — a disposable place to stand, holding only the active work
   item and what derives from it.

Everything else — the workspace view, `twig set`, `twig show` without an
argument, the workspace tree, `bench switch` — is a consequence of that
stack. This page explains the model, the rules that hold it together, and
how a human at a terminal and a script or agent each address it.

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
  [`workspace exclude`](../commands/workspace/workspace-exclude.md) group
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

### Context — a disposable place to stand

A **Context** is a place you open, work in, and close (ticket 0022 §1).
It holds only where you are: the active work item, plus what derives from
it (parent chain, children, navigation history). It is *not* a record of
interest. Reading a work item does not add it to a Bench and does not
retroactively join any Context. Being on a Bench means only that you *can*
stand on it — the Bench does not know who is standing on it.

Concurrency lives at the Context level, not the Bench level: **Contexts
are concurrent, Benches are switchable** (`CONTEXT.md` §4). Several
Contexts can be open at once against one Connection, each naming which
Bench it stands on; only one Bench at a time is the "current" one for a
given Context.

There is **one default Context per Connection**, and it is the only
Context twig creates on its own — so it is never reaped (ticket 0023
"RULING"). Everything else is opened deliberately by a caller.

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

`set` is how you *point* the current Context at a work item. If you want
to claim work you own on the ADO board, transition the item and update
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

## Standing commands vs targeted commands

Commands split cleanly (ticket 0022 §3):

- **Standing commands** need a Context. Examples: `tree`, `nav`, `set`
  with no target named. These read *where you are* — an active item, its
  neighbours, the current Bench view.
- **Targeted commands** name their own work item. They need a Connection
  and nothing else. Reading `twig show 1234`, staging a change against
  `#1234`, or opening it in the browser must not create or mutate a
  Context as a side effect.

The rich CLI lives mostly in the first kind; the script CLI and MCP live
mostly in the second, and MCP tools already require every call to name
its target (see ticket 0021 and
[`docs/architecture/mcp-server.md`](../architecture/mcp-server.md)).

Design consequence, stated so it is not re-litigated:

- **Reading one work item does not need a Bench and does not join one.**
- **A targeted read must not mutate a Bench as a side effect**, or scripts
  silently move the user's view.

---

## Addressing a Context — human simple, machine strict

Ticket 0023 settled how a caller names its Context. The rule holds across
every surface:

| Caller           | Standing command, no Context named           | Non-default Context |
|------------------|----------------------------------------------|---------------------|
| human format     | the default Context for the current Connection | must name it       |
| machine format   | **hard error**                               | must name it        |
| any              | targeted command                             | no Context involved |

Why the split:

- **A human can never drift.** Silence lands you in the default Context
  for the current Connection, every time. There is no state where doing
  nothing puts you somewhere unexpected — the kubectl failure mode, in
  one sentence.
- **A machine inherits nothing, ever.** A script has no memory between
  runs and no prompt to glance at, so requiring the name — even for the
  default — costs one flag and removes the whole class of quiet-target
  bugs. This is ticket 0021's rule ("every MCP tool names its work item")
  one level up.
- **Format is a declaration, not an inference.** Twig must not sniff for
  a tty. The output flag is *declared* by the caller, so a command means
  the same thing in a pipe as at a prompt (see the `-o|--output` flag on
  every [`bench`](../commands/bench/README.md) subcommand and
  [`context`](../commands/context/README.md) command).

**An unknown or expired Context is a hard error.** Not a fallback, not a
warning, not a silent fresh Context. Prior art splits by failure family:
a *handle* must resolve, so a stale one fails loud (docker, ssh-agent); a
*name in a shared file* always resolves, so a stale one acts on the wrong
target silently (kubectl, terraform, gh). Twig is deliberately in the
first family (ticket 0023 "RULING — twig takes the handle family"). Being
wrong loudly is the feature.

---

## Durable Bench selectors, disposable Context

Two rules of thumb summarise the persistence model:

- **Benches are durable.** Their selectors live in the pending store
  alongside the pending set and survive cache rebuilds
  (`src/Twig.Domain/Aggregates/Bench.cs:5-11`, `docs/specs/bench.spec.md`
  §"What is NOT the problem"). Naming an arrangement is a promise that
  it will still be there tomorrow.
- **Contexts are disposable.** A caller opens one, works, and closes.
  Only the default Context per Connection is created and kept by twig
  itself (ticket 0023 "Consequence for lifetime"). Everything else was
  named by someone who can be told it is gone.

When you switch Bench, selectors on the previous Bench are untouched;
switching does not migrate pins
(`docs/commands/bench/switch.md`). When you close a non-default Context,
no work is lost — because unpushed work belongs to the Connection, not to
the Context (see below).

---

## Pending work is Connection-owned

Every unpushed field change, note, and seed lives on the **Connection**,
not the Bench and not the Context (ticket 0023 "Corollary — close never
refuses"). Two structural consequences:

1. **Switching Bench never changes what twig owes ADO.** A Bench is a view
   and never a sync unit. Pending edits stay pending; seeds stay visible
   even if no selector on the new Bench matches them (ticket 0022 §7 —
   the "mandatory guard").
2. **Closing a Context cannot discard work.** Close exits `0` and reports
   what remains pending against the Connection. There is deliberately no
   `--force` on close: a force flag that is habitually needed is how the
   silent-loss class recurs (ticket 0023 "Corollary").

Discard is a separate, explicit verb on the pending set. Reconciliation
against ADO happens at the Connection level; look at the pending state
and reconcile through the plan/proposal path, not through a Bench switch.

For the on-disk shape — where the cache lives, how the pending set is
stored, and how sync flushes it — see
[`docs/architecture/data-layer.md`](../architecture/data-layer.md).

---

## Putting it together — a worked walk

A human at a terminal, working in one Connection with the default Bench:

```
$ twig set 4211                 # local pointer flip; no ADO write
Set active item: #4211 Wire retry telemetry [Doing]

$ twig show                     # standing read; uses the default Context
#4211 Wire retry telemetry — Doing — You

$ twig workspace                # view rendered on the current Bench
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

A script running in CI names its Context, always, including when it wants
the default — machine callers inherit nothing (see ticket 0023 "RULING"
and the `-o|--output` flag on every command page).

---

## See also

**Commands**

- [`bench`](../commands/bench/README.md) — create, list, switch, delete Benches.
- [`context`](../commands/context/README.md) — `set`, `show`, `show-batch`, `query`, `web`, `history`.
- [`workspace`](../commands/workspace/README.md) — the view rendered on the current Bench.
- [`workspace track`](../commands/workspace/workspace-track.md) /
  [`workspace exclusions`](../commands/workspace/workspace-exclusions.md) — pin and hide items.
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
