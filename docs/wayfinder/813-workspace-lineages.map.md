# MAP: Workspace lineages — how many workspaces a chain of workers gets

> **Authoritative source.** This file is the map's source of truth, on branch `wayfinder/workspace-lineages`. ADO Map **#813** mirrors it.

## Destination

The rules governing how **worktrees, workspaces and tabs are allocated across a chain of workers** are settled, and land as a rewritten "Session transport (Herdr)" section in `AGENTS.md` plus whichever workflow skills read it.

This map decides **prose rules that agents obey**. It builds nothing, adds no field, and introduces no new artifact. Reaching the end of it means someone can rewrite that section and the affected skills without another decision outstanding.

## Notes

**Domain.** Agent session transport. The deliverable lands in `~/repos/hyperbright-workflow-skills` and `~/.omp/agent/AGENTS.md`, not in twig's source.

**Vocabulary — three things with three different lifetimes, routinely conflated:**

- **Tab** — the herdr tab a session runs in. Ended by `herdr tab close`, which is what `closeout/scripts/close-my-tab.sh` calls.
- **Workspace** — the herdr container holding tabs. Collapses when its last tab closes. `close-my-tab.sh:93` guards exactly this.
- **Worktree** — the git checkout on disk plus the branch it pins. Removed only by `herdr worktree remove`, which **nothing in the skills ever calls**.
- **Lineage** — a chain of workers that successively occupy one workspace, **one at a time**. A lineage is a chain of *workers*, not of tickets and not of shared outputs.

**Skills every session should consult:** `skill://wayfinder`, `skill://grilling`, `skill://domain-modeling`, `skill://twig-cli`, `skill://ado-publish`, `skill://closeout`, `skill://do-work`.

**Settled while charting** (2026-08-27, with the human; these are premises, not open questions):

- The destination is an **amended contract**, not a new artifact and not a built feature.
- A **lineage is a chain of workers**, one at a time. *Examples corrected by 0001 (2026-08-28):* #729's children are `740→742, 741→742, 742→743` — a join, not a serial chain — and #727's children contain `732→734→735`, a chain of the same family. Neither Spec carries a whole-Spec verdict. The principle stands; the original examples did not.
- The **fan-out shape is in scope** as a *second* allocation rule under the same contract. Silence on fan-outs is not neutral — it re-endorses today's per-item default, which is the behaviour that produced four workspaces feeding one branch.
- Lineage membership is **derived from the blocking graph**, not authored in advance. The failure in both evidence cases was not a missing plan; the graph already said what the shape was and nobody read it.
- A lineage shares a **worktree and a workspace**, with a **fresh tab per worker**. Self-close therefore needs no change in the common case: `close-my-tab.sh:93` already keeps the workspace alive while a sibling tab exists.
- The hard invariant is **worktree exclusivity *between* lineages**. *Corrected by 0002 (2026-08-28):* the charting premise that "a lineage may hold more than one worktree" is **dead** — a lineage holds exactly one branch, and git refuses to check one branch out in two worktrees (`fatal: '<branch>' is already used by worktree at …`, exit 128, verified first-hand). One lineage, one worktree. Two lineages may never share one, now trivially true.
- Within a lineage there is **no split trigger**. *Superseded by 0002 (2026-08-28):* the charting premise that a lineage "splits on a branch or pull-request boundary" does not survive, because the branch, the pull request and the lineage are the **same unit**. One lineage = one branch = one PR. Work needing a second PR is by definition a second lineage.

**Standing preference.** Charting is planning. Resolve one ticket per session (research tickets excepted), and do not carry execution into the map.

**Known tracker defect.** twig 0.91.5 predates AB#748 — plan readback compares HTML literally while ADO canonicalizes it, so any `batch` op staging an HTML field reports `Indeterminate` even when the mutation lands. Reconcile with a refreshed read rather than retrying.

## Decisions so far

- **0007 - What does herdr actually support for worktree and workspace lifecycle?** (#820): Four verbs, not three. A lineage *can* share one workspace across several worktrees via `tab create --cwd`, but herdr does not record that as membership, so it is invisible to `worktree list` and to any reaper. `worktree remove` has **no liveness guard** — it deleted a checkout and closed a live workspace with two running panes without `--force`. Closing the last tab leaves the checkout on disk **by design**. `is_prunable` is useless as an orphan signal. `--base` defaults to the source checkout's `HEAD` and is ignored outright when the branch already exists. Detail: `docs/research/0007-herdr-worktree-workspace-lifecycle.md`.
- **0009 - Current-state audit** (#822): Quoted, line-referenced inventory of the six contract surfaces, with the one-worker-per-workspace assumptions listed separately. `cross-lane-handoff` is server selection only and allocates nothing — **it is not a surface this map has to change**, which narrows 0008. Corrected a premise: the "zero forbidden-term mentions" claim holds for `skills/` only; AGENTS.md line 224 contains a `lineage` match. Detail: `docs/research/0009-current-contract-audit.md`.
- **0010 - Orphan census** (#823): **118 worktrees across 19 repositories**, not the 34 the ticket assumed — twig alone has 33. Classified: 19 live, 31 safely reclaimable, **45 holding unpushed work**, 23 unknown. The 45 is the governing number for 0006: a reaping rule that does not check for unpushed commits destroys real work at scale, not in edge cases. Measured without fetching, so merge status is against the existing local `origin/main` and may be stale. Detail: `docs/research/0010-worktree-orphan-census.md`.
- **0001 - How does a spawning agent derive lineage membership from the blocking graph?** (#814): **Inheritance requires a 1:1 blocking edge into a live workspace.** X continues P's lineage iff X has exactly one predecessor P, P has exactly one successor X, and P's workspace is still live; everything else starts a new lineage. The predicate is asked *per spawn*, never per Spec, so finding a chain inside a fan-out is a correct output. A lineage ends at the first of: no 1:1 successor edge, or its workspace is gone. Decidable in at most two refreshed reads, with no tie-break and no lock. Fails closed to a new lineage while recording `lineage: new (graph unread)` — necessary because `twig sync` leaves `work_item_links` empty and an unrefreshed read is byte-identical to a genuinely isolated item. Killed two of this map's own premises: #729 is a join, not a serial chain, and #727 contains a chain of the same family, so **"one lineage for #729" was never reachable from the graph**.
- **0002 - Where does the branch or pull-request boundary come from at spawn time?** (#815): **From nowhere new — the boundary *is* the lineage boundary.** A branch is a function of the **lineage**, not the claimed item: the first worker cuts it, successors inherit it, and the id in the branch name is the lineage head's. One lineage = one branch = one PR, definitionally, so there is **no intra-lineage split trigger** and no new readable input — the trigger is 0001's own 1:1 predicate, already required at every spawn. Inheriting the branch: the baton's existing `destinationBranch` supplies the key in O(1) (preserving 0001's two-read bound) and `herdr worktree list` holds a **veto** — absent, dead, or disagreeing fails closed to a new lineage. Consequence, enforced by git rather than by agreement: **a lineage holds exactly one worktree**. Rejected and recorded: scanning `worktree list` by id, which herdr cannot key (no id field; head-named branches hide mid-chain predecessors; #728 carries three branches against 0001's ratified no-tie-break). Verified that no PR-shaped input exists at spawn in any case — no type declares a branch or PR field, no item carries a Branch/PullRequest artifact link, `origin` is GitHub while twig's PR client targets ADO Repos, and nothing in `skills/` ever creates a PR.

## Not yet specified

- ~~**Adopt the second worktree, or accept the second workspace?**~~ **Cleared by 0002 (2026-08-28).** The question presupposed that a lineage can hold a second worktree. It cannot: one lineage holds one branch, and git refuses to check one branch out in two worktrees. There is no second worktree to adopt, so `tab create --cwd <other-worktree>` adoption is not needed for intra-lineage sharing and its invisibility to reaping is not this map's problem.
- **Whether twig's primary-scope attachment model survives a shared worktree.** MAP #726 states that "a managed Git worktree has one explicit primary scope attachment." A worktree shared across a lineage attaches to several work items, which that model does not admit. Settled in Spec #728, already Done — a collision with a closed decision, not a blockable edge, and it needs revisiting on #726's side rather than resolving here.
- **What protects a live worktree from being reaped.** 0007 established there is no liveness guard anywhere in herdr: `worktree remove` destroys running panes without `--force`, and it takes `--workspace`, not a path — so it cannot even address an orphan, because an orphan has no workspace. Reaping an orphan therefore means plain `git worktree remove`, entirely outside herdr's knowledge. Whatever safety exists has to be built in the contract; nothing underneath provides it. Feeds 0006, but may be larger than one ticket.

## Out of scope

- **PR groups as blocking units / PR-group-as-first-class-node.** Shelved deliberately. It collides with the standing rule that PR groups must not map 1:1 to the ADO hierarchy, so making them blocking units would require promoting a PR group to a first-class node — a genuinely separate design question. This map may *name* a pull-request boundary as a trigger for splitting a worktree, but it never decides how PR groups are formed.

## Named inputs to open tickets

- **To 0011 - Does the derivation need an override seam, and what carries it? (#828).** PR-group intent *is* authored and readable today — 61 `*.plan.md` files under `docs/projects/`, many carrying `PG-1`/`PG-2` rows. 0002's contract cannot honour a plan that wants `PG-1={A,B}` and `PG-2={C,D}` across a genuine 1:1 chain: one lineage means one PR, so the plan's grouping is forced flat. That is exactly the case an override seam would decide, and it is #828's to weigh — not chartered here, and it does not reopen the out-of-scope PG-formation question.

## Motivating evidence

All first-hand, 2026-08-27:

1. **Four workspaces for one output.** A session working Spec #727 created four worktrees — #727 coordination plus #732/#733/#734 implementers — all feeding one branch, `work/727-reference-process`. Three were torn down at closeout. Nothing planned that shape; it emerged from one agent's mid-session judgement.
2. **Four workspaces for one dependency graph.** Spec #729's children ran #740 → #741 → #742 → #743, and #742 and #743 each took a fresh workspace. *Corrected by 0001 (2026-08-28):* that run order was one serialization of a partial order, not a serial chain. The real edges are `740→742, 741→742, 742→743`, so `740` and `741` are unordered and can never share a workspace. One-at-a-time happened by accident of scheduling, not by design and not by the graph.
3. **Worktree base staleness.** `herdr worktree create` defaults `--base` to the repo's current HEAD, not `origin/main`. A worktree so created was 72 commits stale, and 4,922 tests passed green against a baseline that no longer existed. Allocation-time decisions have correctness consequences, not merely tidiness ones.
4. **Self-close topology.** A session spawned into the main checkout via the shared-checkout fallback — on a Spec later found to produce 1,844 lines of code — could not self-close, because its tab was the sole tab of a non-disposable workspace. Commit `329528e` added `is_linked_worktree` to distinguish disposable from not.
5. **Worktrees are never reaped.** Confirmed and enlarged by 0010: **118 worktrees across 19 repositories**, 45 of them holding unpushed work. `work/729-change-recipe-proposal` and `work/743-proposal-review-authorization` have no `open_workspace_id` — their workspaces are gone and their checkouts are still on disk pinning branches.
