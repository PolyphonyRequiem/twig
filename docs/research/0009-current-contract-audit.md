# 822 - 0009 - Current-state audit: what do AGENTS.md and the workflow skills specify today?

The audited contract explicitly creates one per-item worktree and its Herdr workspace, then creates a second tab in that workspace when environment variables are required. [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:143-185`]
The closeout contract closes a tab through a preflighted adapter and treats the only-tab case differently for linked-worktree and ordinary workspaces. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:707-719`; `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:1-4,25-38,95-120`]
The workflow skills contain no forbidden-term matches; the broader command including AGENTS.md finds one `lineage` match there. [verified-by-execution: command and output recorded in “Forbidden-term grep”]

## Scope and method

- [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:106-338`] The complete `## Session transport (Herdr)` section was read.
- [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md`, `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md`, `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md`, `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh`, `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md`] These are the audited workflow surfaces. The repository README says the cross-lane skill is no longer shipped by the workflow-skills repository and the dedicated source is the agent skill path. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/README.md:218-220`]

## AGENTS.md — quoted inventory

### Creation and topology

- “One **worktree and tab per work item.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:143`]
- “Create the per-item worktree from the claimed item’s repo first.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:148`]
- “herdr worktree create --cwd <repo-root> --branch work/<id>-<slug> \\” followed by “--base origin/main \\” and “--label \"<id> <short description>\" --no-focus”. [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:151-154`]
- “`--branch` **creates** the branch (from `--base`, else current HEAD). The checkout lands at `~/.herdr/worktrees/<repo-name>/<branch-with-slashes-as-dashes>`. `--label` names the **workspace**, not the tab — the auto-created tab’s label is the number `1`.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:156-158`]
- “`worktree create` opens a **whole new workspace**, not a tab in `$HERDR_WORKSPACE_ID`, and returns `.result.workspace`, `.result.tab`, and `.result.root_pane` — the same triple as `workspace create`, with `root_pane.cwd` already at the worktree.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:169-171`]
- “its auto-created tab cannot carry them: create a second tab in the returned workspace and start the agent there, leaving the auto tab idle.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:173-175`]
- “herdr tab create --workspace <worktree-workspace-id> --cwd <worktree-path> \” with `--label`, `--no-focus`, `--env WORK_ITEM=<id>`, and `--env BATON=<baton-path>`. [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:177-180`]
- “Take the pane from `.result.root_pane`. Never predict IDs.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:181`]
- “`herdr worktree list --cwd <repo-root>` reports `open_workspace_id`, `path`, and `branch` for a worktree that already exists.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:184-185`]
- “Only when the claimed item mutates no source — a plan-to-split `Feature`, a read-only `Research`, a tracker-only `Spec` — MAY the tab use `--workspace "$HERDR_WORKSPACE_ID" --cwd <repo-root>`.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:191-193`]
- “Grant read-only siblings the work genuinely needs (e.g. a design pin in another checkout) with `--add-dir` (repeatable) on omp and copilot.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:208-212`]
- The launch examples name `--add-dir` for omp and copilot: [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:240-242,263-265`]

### Closing and removal

- “`herdr worktree remove --workspace <id> [--force]` tears one down.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:184-185`]
- “Never split a pane inside the tab that is about to close.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:191-194`]
- “/closeout invokes model-invoked `herdr`, then runs its bundled `skill://closeout/scripts/close-my-tab.sh` with `--dry-run` as a non-mutating preflight, and only on success runs it once without `--dry-run`.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:329-331`]
- “The adapter cross-checks the workspace, tab, and pane ids against live Herdr records before its single unpiped `herdr tab close`.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:329-331`]
- “Do **not** invoke `herdr tab close "$HERDR_TAB_ID"` directly.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:333-334`]
- “Add `--allow-sibling-panes` only when closing the tab would take panes this session does not own *and* that loss is explicitly intended. Never close a workspace, tab, or pane this session did not create. Never run `herdr server stop`.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:336-338`]

### Named tokens

- `--base origin/main`: [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:152-153,160-166`]
- `--cwd`: [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:152,178,184,193,209`]
- `--add-dir`: [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:211,241,264`]
- `is_linked_worktree`: no occurrence in the AGENTS session-transport section. [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:106-338`]
- `--allow-workspace-close`: no occurrence in the AGENTS session-transport section. [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:106-338`]

## do-work/SKILL.md — quoted inventory

- The skill says the baton records “where `/next` ran” and that an inheriting consumer is “by definition in the destination worktree, on the destination branch”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md:62-67`]
- It says to “create `<twig-workspace-root>/.twig/ado-plans/<opaque-id>/` if absent” and write `plans.json`. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md:75`]
- It says “destination binding, when present” carries `destinationWorkspaceRoot` and `destinationBranch`, and that this is the worktree path returned by transport when `/next` creates the worktree. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md:173-175`]
- It says “Write the baton to a stable path **outside the git workspace**” so “worktree pruning, and branch swaps cannot destroy it.” [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md:170-171`]
- No `herdr worktree create`, `herdr workspace create`, `herdr tab create`, pane creation, tab close, workspace close, or worktree removal statement occurs in this skill’s transport-related text. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md:1-251`; search inventory]
- `--base`, `--cwd`, `--add-dir`, `is_linked_worktree`, and `--allow-workspace-close` do not occur in this file. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md:1-251`; search inventory]

## next/SKILL.md — quoted inventory

- The baton records “where `/next` ran”; an “inheriting-consumer” is “in a different checkout, on a different branch”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:89-99`]
- “Destination binding (when `/next` creates the worktree)” records `destinationWorkspaceRoot` as “the worktree path returned by the transport’s worktree-create response” and `destinationBranch`; both are omitted on the shared-checkout fallback. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:170-175`]
- “Write the baton to a stable path **outside the git workspace**” so worktree pruning and branch swaps cannot destroy it. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:170-171`]
- `/next` says `ado-session` “holds exactly one item per **session** at a time (Research excepted, read-only) — not one per workspace, because several sessions can share one twig workspace root”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:226-228`]
- No Herdr worktree/workspace/tab/pane creation or teardown command is specified in this skill. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:1-234`; search inventory]
- `--base`, `--cwd`, `--add-dir`, `is_linked_worktree`, and `--allow-workspace-close` do not occur in this file. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:1-234`; search inventory]

## closeout/SKILL.md — quoted inventory

- “`/next` writes a durable baton pointer outside the git workspace and a portable plans file inside the twig workspace”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:82-87`]
- It distinguishes an “origin-consumer” “before that pane closes” from an “inheriting-consumer” “in the destination worktree”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:89-99`]
- It requires a worktree frame check and refers to “a swapped worktree” and “a pruned-and-recreated worktree”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:112-131`]
- The close method is `herdr-tab`: run `close-my-tab.sh` with `--dry-run`, optionally `--allow-sibling-panes`, then run the same command once without dry-run; the adapter performs one unpiped `herdr tab close`. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:707-713`]
- “It also refuses when the caller’s tab is the **only** tab in a workspace that is not a disposable linked worktree — closing that tab would destroy the workspace”. A per-item worktree workspace has `is_linked_worktree: true` and is exempt; `--allow-workspace-close` is named for the remaining case. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:714-719`]
- `--base`, `--cwd`, and `--add-dir` do not occur in this file. `is_linked_worktree` and `--allow-workspace-close` occur at lines 717-719. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:1-732`]

## close-my-tab.sh — quoted inventory

- “Close the Herdr tab containing this caller” and usage includes `--dry-run`, `--allow-sibling-panes`, and `--allow-workspace-close`. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:2-4`]
- It requires Herdr identity variables `HERDR_WORKSPACE_ID`, `HERDR_TAB_ID`, and `HERDR_PANE_ID`, and reads the current pane, caller tab, workspace panes, and workspace list before closing. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:25-38`]
- The script computes `ws_linked` from workspace `worktree.is_linked_worktree`. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:40-60`]
- It refuses a tab with multiple panes unless `--allow-sibling-panes` is supplied. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:91-94`]
- It refuses when the tab is the only tab in a non-linked workspace unless `--allow-workspace-close` is supplied, stating: “closing it would close the workspace”. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:95-97`]
- In dry-run it prints “would close tab”; otherwise it prints “closing tab” and executes `herdr tab close "$TAB"`. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:99-120`]
- The script contains no worktree creation, workspace creation, tab creation, or pane creation. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:1-120`]
- `--base`, `--cwd`, and `--add-dir` do not occur; `is_linked_worktree` occurs at line 58 and `--allow-workspace-close` at lines 3, 15, and 96. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh:1-120`]

## cross-lane-handoff/SKILL.md — quoted inventory

- “Lanes pin **where work executes**. Handoffs decide **who the work is for**.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:16-17`]
- The lane index resolver supplies a lane’s “herdr socket”: `lane_resolve.py socket <lane>`. [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:28-35`]
- The text says a workspace path across lanes is “a dangling pointer into a worktree the receiver cannot see” and forbids a card saying “look at what I did in my workspace.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:76-85`]
- It resolves `LANE` and `SOCK`, asserts the socket, and says to stamp the resolved lane and socket in run metadata. [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:98-108`]
- It says pane IDs are “per-server” and must be qualified with the lane. [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:110-111`]
- Its server-start example runs `HOME="$LOGIN_HOME" herdr --session <lane> server`; it describes socket placement under the login home. [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:113-129`]
- No worktree, workspace, tab, or pane creation/closure/removal command is specified; the skill concerns lane/server/socket selection. [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:1-160`; search inventory]
- `--base`, `--cwd`, `--add-dir`, `is_linked_worktree`, and `--allow-workspace-close` do not occur. [read-from-source: `/home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md:1-160`; search inventory]

## One-worker-per-workspace assumptions (separate inventory)

- “One **worktree and tab per work item.” [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:143`]
- The AGENTS text says an auto-created tab is left idle and a second tab is created in “the returned workspace” to start the agent. This explicitly places two tabs in one workspace during launch, while assigning one spawned worker to the per-item workspace. [read-from-source: `/home/polyphonyrequiem/.omp/agent/AGENTS.md:173-180`; inference from quoted topology]
- `/next` says `ado-session` holds “exactly one item per **session** at a time (Research excepted, read-only) — not one per workspace, because several sessions can share one twig workspace root.” [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md:226-228`]
- `/closeout` calls a per-item worktree workspace’s closure “the designed end of the one-worktree-per-item lifecycle.” [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:714-718`]
- `/closeout` distinguishes two consumers of one baton and states that the inheriting consumer runs “in the destination worktree” while the origin consumer is before its pane closes. [read-from-source: `/home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md:89-99`; inference: this is a worker succession across frames, not a literal one-worker-per-workspace sentence]

## Forbidden-term grep

The exact command including every requested surface plus AGENTS.md was:

```text
grep -RniE 'pull request|PR group|PG-|lineage' /home/polyphonyrequiem/.omp/agent/AGENTS.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh /home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md; printf 'grep_exit=%s\n' "$?"
```

Observed output:

```text
/home/polyphonyrequiem/.omp/agent/AGENTS.md:224:still resolved after its workspace became `wayfinder workspace lineages`. So keep the handle
grep_exit=0
```

[verified-by-execution: command above and observed output] Including AGENTS.md therefore refutes a zero-match claim because that file contains `lineage` at line 224. The exact skills-only command was:

```text
grep -RniE 'pull request|PR group|PG-|lineage' /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/do-work/SKILL.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/next/SKILL.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/SKILL.md /home/polyphonyrequiem/repos/hyperbright-workflow-skills/skills/closeout/scripts/close-my-tab.sh /home/polyphonyrequiem/.omp/agent/skills/cross-lane-handoff/SKILL.md; printf 'grep_exit=%s\n' "$?"
```

Observed output:

```text
grep_exit=1
```

[verified-by-execution: skills-only command above and observed output] The workflow skill surfaces have zero matches for `pull request`, `PR group`, `PG-`, or `lineage`; the command’s `grep_exit=1` is grep’s no-match status. The broader requested surface set is not zero because of the AGENTS.md match shown above.
