# Issue tracker: Azure DevOps (via the `twig` CLI)

Work for this repo is tracked on the **Azure DevOps board `PolyphonyRequiem/Twig`**, driven
through the **`twig` CLI**. It is not tracked on GitHub.

> 🔴 The git remote is `github.com/PolyphonyRequiem/twig`, so tooling that infers a tracker
> from `git remote -v` will guess GitHub and guess wrong. GitHub is the **public record**
> surface, not the work tracker.

## The three-way split

`AGENTS.md` § *Where work is tracked* is the authority:

| What | Lives in | Why |
| --- | --- | --- |
| **Work** — defects, tasks, anything schedulable | **ADO** (`PolyphonyRequiem/Twig`) | One board. Source of truth for status and scheduling. |
| **Decisions** — wayfinder rulings, specs | this repo (`wayfinder/`, `wayfinder-1.0/`, `docs/specs/`) | Reviewed with the code they govern; carry evidence a work item cannot hold. |
| **Public record** — issues from outside | **GitHub** | Contributors have no ADO access. |

Consequences when deciding where to write:

- Anything schedulable → an ADO work item. This is the default.
- A ruling, a spec, a decision with evidence → a markdown file in this repo. Not a work item.
- An issue reported by an outside contributor → it stays open on GitHub, and its **tracking**
  moves to ADO. Do not close the GitHub issue; closing it hides a live defect from the public.

## Conventions

The `twig` binary is on `PATH`. The workspace is already initialised against
`PolyphonyRequiem/Twig`. `twig` resolves its workspace from the **current directory**.

- **Create**: `twig new --type <Type> --title "..."`, or stage a seed first (below).
- **Read**: `twig show <id>` — `--tree` for hierarchy, `--refresh` to sync first,
  `--output json` for machine-readable.
- **Search**: `twig query [text]` — filters by text, type, state, assignee.
- **Set the active item**: `twig set <id|pattern>`. `note`, `state` and `update` act on the
  active item when given no id.
- **Comment**: `twig note "..."`
- **State**: `twig state <name>`
- **Fields**: `twig update <field> <value>`, `twig patch --json '<json>'` for several
  atomically, `twig batch` to combine state + fields + notes.
- **Link**: `twig link parent <id>`, `link reparent <id>`, `link predecessor <id>` (active item
  is blocked by `<id>`), `link successor <id>` (active item blocks `<id>`).
- **Push**: `twig sync` flushes pending changes then refreshes from ADO.

Run `twig <command> --help` for the full option set — this list is a starting point, not a
substitute for the binary's own help.

### 🔴 Pitfalls

- **Changes are staged locally until `twig sync`.** A `PendingChangeRecord` or `PendingNote`
  lives only in the local SQLite cache. Do not report a change as landed on the board before a
  successful sync.
- **Writing close-gate fields does not move the State.** To close: `twig set <id>`, then
  `twig state Done`, then re-read with `twig show <id> --refresh`.
- **`twig state` fails in a fresh git worktree** with *"Process configuration not available.
  The process_types table is empty"*. The error blames auth — that is a red herring. Run
  `twig sync` in that worktree first.
- **A read can miss a just-created item** — the CLI keeps a local mirror, and `--refresh` does
  not always rescue it. If a read fails with a cache message, `twig set <id>` first.

### Seeds — drafting before publishing

A **seed** is not a type. It is `WorkItem.IsSeed` plus a negative id: a local-only draft work
item never pushed to ADO (`CONTEXT.md` §3). Prefer seeds when drafting a set of related items,
so the whole chain can be reviewed and linked before anything reaches the board:

- `twig seed new <title>` (`--parent <id>`, `--no-parent --type <Type>`, `--editor`)
- `twig seed chain` — a chain of linked seeds
- `twig seed link <s> <t>` — a virtual link, including seed → real ADO item
- `twig seed validate [id]` — check publish rules **before** publishing
- `twig seed publish <id>` / `--all` — publish in dependency order
- `twig seed reconcile` — repair stale links after a partial publish

Publishing is the commit point. Validate first.

## Bidirectional linking

A tracker split without links just moves the problem. Every link is asserted in **both**
directions:

- An ADO item implementing a wayfinder ruling **names that ruling** in its description.
- A scheduled ruling declares its board items in frontmatter: `tracked_in: [139]`.
- A GitHub issue migrated to ADO gets a comment naming the ADO item, and the ADO item's
  description opens with the GitHub URL. **The issue stays open.**
- A commit references its work item with `AB#nnnn` in the message.

```bash
tools/check-tracking.sh              # verify every declared link
tools/check-tracking.sh 1007         # one ticket
tools/check-tracking.sh --selftest   # prove the checker can fail AND pass
```

It asserts both directions: the work item must **resolve**, and its description must **name the
ticket back**. A ticket with no `tracked_in` is **not** an error — most rulings were never
scheduled.

## What the skills' vocabulary means here

- **"Publish to the issue tracker"** → create an ADO work item with `twig new` (or publish a
  seed). Not `gh issue create`. If the thing is a *decision* rather than schedulable work, it is
  not a work item at all — write it under `wayfinder/` or `docs/specs/`.
- **"Fetch the relevant ticket"** → `twig show <id> --refresh`; `--tree` for the subtree.

## 🔴 Routing is a decision, not execution

Choosing which project, area path or parent a work item lands under is a **routing decision**.
Wrong-project routing is the recurring defect here.

- **Do not create a work item to make a statement true.** If asked something conditional —
  "if there's a card for this, leave it" — and no card exists, **say so**.

## Wayfinding operations

Used by the `wayfinder` skill. This repo does **not** model the map as a tracker item. The
**map is a markdown file** (`wayfinder/map.md`, `wayfinder-1.0/map.md`) and its **tickets are
markdown files** under `<map>/tickets/`. That is the "Decisions" row of the split above.

- **Map**: `<map-dir>/map.md` — Destination, Notes, Decisions so far, Not yet specified.
- **Ticket**: `<map-dir>/tickets/NNNN-slug.md` with frontmatter. A question to answer, not work
  to schedule.
- **Resolve**: answer in the ticket, then add a one-line entry to the map's *Decisions so far*.
- **Scheduling a ruling**: create the ADO item(s), add `tracked_in: [<ids>]` to the ticket
  frontmatter, name the ticket in each item's description, verify with `tools/check-tracking.sh`.

## Work item types

The board runs a custom process. Enumerate its types and their states from the tool rather than
from a list in this file, which would drift:

```bash
twig process                 # all types with state counts
twig process <type>          # states, fields and transitions for one type
twig process layout <type>   # the form layout
```

Beyond the stock set (`Bug`, `Task`, `Epic`, `Feature`, `Issue`, and the test-management types)
the process defines: `Map`, `Wayfinder Task`, `Research`, `Prototype`, `Grilling`, `Decision`,
`Spec`, `Idea`.

🔴 **How these relate to the repo's `wayfinder/` markdown is UNDECIDED.** The names line up
suggestively, but a suggestive name is not a mapping. Ask; do not infer it from the type list.

🔴 **Four types are HIDDEN system machinery — they are not spare vocabulary.**
`Code Review Request`/`Response` and `Feedback Request`/`Response` all sit in
`Microsoft.HiddenCategory`, which Microsoft defines as *"the set of WITs that you do not want
users to create manually"*. They are the back ends of two Microsoft tool features (the legacy
TFVC code review handshake, and the *Request Feedback* flow) and are created by tooling, never
by hand — `Feedback Request` cannot even be customised, as ADO rejects the attempt as already
in use. Do not route work into them, and do not treat their names as taken when choosing names
for new types: they are not part of the visible vocabulary at all.

🔴 **Category membership does NOT follow the type's name.** Measured on this board:
`Microsoft.BugCategory` contains **`Issue`**, not `Bug`; `Bug` lives in
`Microsoft.RequirementCategory`; and `Issue` is itself hidden. Never infer a category from a
name.

`twig process` **omits hidden types by default** (AB#657) and marks them when you ask for them,
so the default listing is the vocabulary you can actually use — 12 types here, not 21:

```bash
twig process                    # usable types only
twig process --include-hidden   # all types, hidden ones marked [hidden — ADO tooling type]
twig process --output json      # carries isHidden and categories per type
```

Naming a hidden type still describes it (`twig process "Code Review Request"`): the type is
omitted from the *list*, never made unreachable.

⚠️ **Three different routes return three different type lists.** The process roster
(`_apis/work/processes/{id}/workItemTypes`) is the process's own set; the project-scoped
`_apis/wit/workitemtypes` reports **more**, because it includes the hidden system helpers; and
categories come from `_apis/wit/workitemtypecategories`. Do not assume the three agree — see the
red note in `src/Twig.Infrastructure/Ado/Dtos/AdoProcessDescriptionDtos.cs`.
