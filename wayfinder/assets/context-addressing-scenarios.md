# Context Addressing — Scenario × Mechanism Analysis

Ticket: `wayfinder/tickets/0023-context-addressing.md`. Ruling not reopened: `wayfinder/tickets/0022-bench-and-context.md`.
Every claim tagged **[measured]** (ran a command / read the file), **[cited]** (from a ticket or external doc), or **[asserted]** (my reasoning).

## 0. Grounding

| Fact | Tag | Evidence |
|---|---|---|
| Active work item is one row, key `active_work_item_id`, in a shared kv table; not per-Connection, not per-session | [measured] | `src/Twig.Infrastructure/Persistence/SqliteContextStore.cs:12`, `SELECT value FROM context WHERE key=@key` :57 |
| 28 files under `src/` (excl. `obj/`) reference `IContextStore` | [measured] | `grep -rl IContextStore src --include=*.cs \| grep -v obj \| wc -l` → 28 |
| Only `TWIG_MCP_TOOL_PROFILE` exists as a behaviour-selecting env var; `TWIG_PAT` is a credential; `TWIG_PROMPT`/`TWIG_TYPE_*`/`TWIG_STATE_CATEGORY` are **twig→shell outputs** for oh-my-posh, not inputs | [measured] | `src/Twig.Mcp/Services/McpToolCatalog.cs:20`, `src/Twig/Program.cs:288`, `src/Twig/Commands/OhMyPoshCommands.cs:12-163` |
| 0021 patched the slot at the MCP surface only; the slot survives | [cited] | 0022 §4 |
| STANDING (`tree`, `nav`, untargeted `set`) need a Context; TARGETED need a Connection and nothing else; a targeted read must not mutate a Bench/Context as a side effect | [cited] | 0022 §3 |
| #271 class = silently destroying unpushed work; never recommend discard | [cited] | 0023 §3, task brief |
| The oh-my-posh integration already exports twig state into the shell env per prompt — the shell is *already* a state-carrying surface in this tool | [measured] | `OhMyPoshCommands.cs:136-163` |

Prior art cited for mechanisms:

| Tool | Mechanism | Note |
|---|---|---|
| `git worktree` | E (explicit add/remove handle) + prune with `gc.worktreePruneExpire` default **3 months** | [cited] git docs; the reclaim model Q1 borrows |
| `kubectl` | D (one default context per config) + `--context` flag (B) | [cited] |
| `ssh-agent` / `SSH_AUTH_SOCK`, `docker context`+`DOCKER_HOST`, `tmux`+`TMUX` | A (env var names the ambient session) | [cited] |
| `direnv`, `git` repo discovery | C (derive from cwd/ancestry) | [cited] |
| `screen`/`tmux` attach | E handle, reclaimed by explicit kill only | [cited] |

## 1. The matrix

Mechanisms: **A** env var names the Context · **B** explicit flag every invocation · **C** derive from process ancestry / terminal session · **D** one default Context per Connection · **E** explicit open/close returning a handle · **F** sticky-by-default (today generalised).

### Scenario 1 — Human at a terminal, several commands
Sequence: `twig nav 4821` → `twig tree` → `twig set state=Active` → `twig children`.

| Mech | What concretely happens | Failure mode when the caller forgets |
|---|---|---|
| A | Shell has `TWIG_CONTEXT=ctx-7`; all four land in ctx-7. Needs `eval $(twig context open)` or a shell hook once per terminal. | Env var unset → every command starts clean; `tree` after `nav` shows nothing. Worse: var *stale* from a closed Context → commands fail or silently reopen a dead id. |
| B | `twig nav --context ctx-7 4821`, repeated on all four. Works, verbose. | Forgotten flag → falls back to *something*; whatever that fallback is, it is the real design. Flag alone cannot be the answer. |
| C | Context keyed by tty/session id (or PPID chain); all four inherit automatically. Zero ceremony — best human ergonomics. | New tmux pane / `ssh` re-login = new key = lost place, with no error, just an empty `tree`. Ancestry is invisible, so the user cannot see why. |
| D | One default per Connection; all four land there. Identical to today for a single human. | Two terminals on the same Connection silently share — this is the #271-adjacent defect 0022 exists to delete, reappearing at the human surface. |
| E | `h=$(twig context open)` then `--context $h` (or export it) then `twig context close $h`. | Human forgets `close` → Contexts accumulate (see Q1). Human forgets `open` → command errors "no Context"; loud, recoverable. |
| F | Works exactly like today. Best ergonomics, zero ceremony. | Any *other* caller (script, agent, MCP) on the same machine moves the human's place under them. This is the defect class, not a scenario cost. |

### Scenario 2 — Script runs twig N times in one job
Sequence: `for id in $(twig query ...); do twig nav $id; twig tree; done` inside `deploy.sh`.

| Mech | Concretely | Failure mode |
|---|---|---|
| A | Script sets `TWIG_CONTEXT=$(twig context open)` at top, exports; all N calls share it; trap on EXIT closes. Cheap, one line. | No export → each of N calls clean; loop degenerates silently to N independent no-op `tree`s. |
| B | Every line carries `--context $h`. Explicit and greppable. | One forgotten flag in an N-line loop → that one command targets the fallback; result looks plausible, is wrong. |
| C | All N inherit the script's own process/session → correct by construction, no code change to the script. | Script that backgrounds work (`&`), uses `xargs -P`, or spawns via `ssh`/`sudo` breaks the ancestry chain; subset of N goes elsewhere. |
| D | All N share the Connection default. Works *if* nothing else runs. | Script run twice back-to-back inherits run 1's leftovers — the exact scenario 0023 §"narrow question" names. |
| E | Handle opened once, closed in `trap`. Same as A but the handle is a value, not ambient. | Script exits on error before `close` → leak. Needs the Q1 reaper. |
| F | Run 2 inherits run 1's active item. | **This is the named defect** [cited 0023]: "run two silently inherits run one's state… the class of defect 0022 exists to remove." |

### Scenario 3 — Two concurrent scripts, one Connection
Sequence: `./a.sh & ./b.sh &`, both against `org/proj`, both `nav` then `set`.

| Mech | Concretely | Failure mode |
|---|---|---|
| A | Each script exports its own `TWIG_CONTEXT`; isolated. Correct. | If both inherit the *parent's* exported var (launched from one terminal that already had one), they collide and interleave — silent cross-talk, hardest bug in this table. |
| B | Each passes its own handle; isolated, correct, and auditable. | Either forgetting the flag collapses both onto the fallback. |
| C | Distinct PIDs → distinct Contexts if keyed on PID; **same tty** → collision if keyed on session. Key choice decides correctness. | Session-keyed: both scripts share one Context and race on the active item. Interleaved `nav`/`set` → `set` applied to the other script's item. Pending set is per-Connection so the *edit* survives (good), but lands on the wrong work item (bad, and it is a real ADO write). |
| D | Both share the single Connection default. Guaranteed collision. | Same as C-session, unconditionally. D is disqualified by this row alone. |
| E | Two handles, two Contexts, no shared mutable slot. Correct by construction. | Leak on crash only. |
| F | Guaranteed collision, plus each run poisons the next. | Worst cell in the matrix. |

### Scenario 4 — Agent A does task 1, then agent B does a different task
Sequence: agent A `nav 4821; set …`; agent B (fresh process, maybe MCP) `tree`.

| Mech | Concretely | Failure mode |
|---|---|---|
| A | B has no `TWIG_CONTEXT` (separate process tree) → B starts clean. Correct. | If the agent host exports one env for all subprocesses, A and B share — the multi-agent version of scenario 3's collision. |
| B | B must name a Context or a target; MCP already does this [cited 0021/0022 §3]. Correct. | Agent forgets → fallback decides; an agent inheriting a human's place is exactly the 0021 defect. |
| C | Both agents are children of the same supervisor → same ancestry key → **B inherits A's Context**. | Silent inheritance across independent tasks. Ancestry does not model "task", it models "process tree", and agents break that correspondence. |
| D | B inherits A's Context, always. | Same defect, unconditional. |
| E | A opens/closes; B opens its own. Clean by construction; matches how 0021 already forced MCP to behave. | A crashes without close → leaked Context, but B is unaffected because B never joins by default. Leak is bounded, not contagious. |
| F | B inherits A. | The literal defect 0021 patched at one surface [cited 0022 §4]. |

### Scenario 5 — CI, no prior state
Sequence: fresh container, `twig auth …; twig show 4821 --json`, maybe `twig tree 4821`.

| Mech | Concretely | Failure mode |
|---|---|---|
| A | No var set → clean. If a STANDING command is used, it must either error or auto-open. That choice is the whole design. | Silent auto-open + no close → each CI job leaks a Context into a store; harmless in an ephemeral container, cumulative on a self-hosted runner. |
| B | Explicit flags; CI is the one caller that never minds verbosity. Correct. | Forgetting is loud in CI because there is no state to fall back onto — CI is the cheapest place to detect a missing-flag bug. |
| C | No tty in CI; ancestry key is the job process. Works accidentally; degenerates to "one Context per job". | If key derivation needs a tty and there is none, fallback path is untested and CI is where it first runs. |
| D | Connection default created on first use; works, one Context per job. | On a persistent self-hosted runner, job 2 inherits job 1. |
| E | `open` at job start, `close` in `always()` step. Explicit and CI-idiomatic. | Job cancelled → no close → leak; reaper required (Q1). |
| F | Container is fresh, so nothing to inherit — F looks *perfect here and only here*. | Persistent runner → F becomes scenario 2's defect. CI is the scenario that flatters F; do not generalise from it. [asserted] |

### Scenario 6 — TUI, one long-lived session, many commands
Sequence: launch TUI once, then dozens of navigations/edits in-process.

| Mech | Concretely | Failure mode |
|---|---|---|
| A | TUI opens a Context at startup, holds the id in memory; env var irrelevant in-process. Env only matters if the TUI shells out. | TUI shells out to `twig` without exporting → the child starts clean, and the user sees the sub-command not know where they are. |
| B | In-process calls pass the handle explicitly. Trivial for the TUI; it holds a variable. | None material — the TUI is code, not a human, and cannot "forget" once wired. |
| C | Single process, single tty → stable key for the whole session. Works. | User opens a second TUI in the same tty/session key → two TUIs fight over one Context. |
| D | Works while exactly one TUI runs. | Concurrent CLI in another terminal moves the TUI's place mid-session; the TUI must invalidate on every read or show stale state. |
| E | Open at start, close at exit. Cleanest lifetime story in the whole table — the TUI's process lifetime *is* the Context lifetime. | Hard kill (SIGKILL, window close) → no close → leak. Reaper required. |
| F | Works today. | Same cross-surface bleed as D. |

### Scenario 7 — TARGETED command (names its own work item)
Sequence: `twig show 4821`, `twig set 4821 state=Active`, any MCP tool post-0021.
Per 0022 §3 [cited]: needs a **Connection and nothing else**, and **must not create or mutate a Context or Bench as a side effect**.

| Mech | Concretely | Failure mode |
|---|---|---|
| A | Env var present but **ignored** for targeted commands. Correct only if the code path never reads the Context. | Implementation reads `TWIG_CONTEXT` "for convenience" → targeted read becomes Context-mutating → violates 0022 §3. This is a *code* failure, not a caller failure. |
| B | `--context` is rejected/ignored on targeted commands. Correct; the split is visible in the CLI surface. | Accepting the flag invites callers to believe targeted commands are Context-scoped. |
| C | Ancestry key resolved eagerly at startup for all commands → **auto-creates a Context for a targeted command**. Direct violation. | Eager resolution is the natural implementation of C; C must special-case targeted commands, i.e. reintroduce a per-command rule. Counts against C. |
| D | "Default Context per Connection" means touching the Connection materialises a Context. Targeted commands touch the Connection. → violation by construction. | D cannot satisfy row 7 without a carve-out. Disqualifying. |
| E | No handle passed → no Context opened, nothing created. Satisfies row 7 **by construction**, no carve-out. | None. Best cell in this row. |
| F | Sticky means the targeted command updates the sticky slot (today's `set` does exactly this — `SetActiveWorkItemIdAsync` [measured]) → targeted read moves the user's place. | Exactly the "scripts silently move the user's view" defect 0022 §3 forbids. |

### Row-7 scorecard (the ruling's hard constraint)

| Mech | Satisfies "targeted needs no Context, creates none"? |
|---|---|
| A | Yes, if code is disciplined [asserted] |
| B | Yes |
| C | No without a carve-out |
| D | No — violation by construction |
| E | Yes, by construction |
| F | No — violates it today |

## 2. Q1 — What reclaims a Context a caller never closed?

Every mechanism except D leaks Contexts, because every real caller can die without closing. So a reaper is **mandatory, not optional** [asserted].

Prior art: `git worktree` never asks you to clean up correctly. `git worktree add` writes an admin dir; if the working tree disappears, the record becomes *prunable*, and `git worktree prune` — run by `git gc` — deletes it after `gc.worktreePruneExpire`, default **3 months** [cited, git docs]. Three properties worth copying:

1. **Reclaim is a garbage-collect, not a refcount.** No liveness protocol, no PID heartbeats.
2. **Reclaim is generous.** Months, not minutes — a stale record costs almost nothing, a wrongly-reclaimed one costs a user's place.
3. **Reclaim is idempotent and inspectable** (`worktree list`, `prune --dry-run`).

Recommended shape for twig [asserted]:

| Property | Value | Why |
|---|---|---|
| Trigger | opportunistic, on any `context open` and on an explicit `twig context prune` | no daemon; twig is a local single-user tool [cited 0001 via 0023 §4] |
| Criterion | `last_touched` older than expiry **AND** owning process not alive (if a pid was recorded) | pid check reclaims fast in the common case; time bound covers pid reuse and cross-host stores |
| Default expiry | 14 days for Contexts with no pending edits attributable to them | a Context is *disposable* [cited 0022 §1] — far cheaper than a worktree, so 3 months is over-generous |
| Contexts implicated in pending edits | **never** time-reclaimed silently; listed by `twig context list` as `stale (has pending)` | #271 class [cited] |
| Visibility | `twig context list`, `twig context prune --dry-run` | copies `worktree list`/`prune -n` |

Because a Context holds **only the active item and derivations** [cited 0022 §1], reclaiming one destroys nothing durable — the pending set lives on the Connection in `pending.db`, which is never dropped [cited 0022 "What was NOT the blocker"]. That is what makes an aggressive-ish 14 days safe [asserted].

## 3. Q2 — Closing a Context that holds unpushed edits

**Answer: do not refuse. The edits stay pending against the Connection, and `close` reports them.** [asserted, grounded in cited invariants]

Reasoning:

| Consideration | Evidence |
|---|---|
| The pending set is owned by the **Connection**, not the Context — the Context holds only the active item and its derivations | [cited] 0022 §1 |
| Therefore a Context close cannot, structurally, discard an edit; the edit was never stored in the Context | [asserted] |
| Seeds and unpushed edits must remain visible even when no current view selects them | [cited] 0022 §7 mandatory guard |
| Refusing close would make crash-cleanup and the Q1 reaper unable to run on exactly the Contexts most likely to be abandoned mid-edit — pressuring users into `--force`, which becomes the silent-discard path | [asserted] |

Contract:

- `twig context close <h>` with pending edits attributable to it → **succeeds**, exits 0, and prints `N pending edit(s) remain against <org>/<project>; run 'twig pending' or 'twig push'` on stderr.
- No `--force`/`--discard` flag on `close`. Discarding is a separate, explicit, named verb operating on the **pending set** (`twig pending drop <id>`), never a side effect of a lifetime operation. This is the #271 firewall [cited].
- The reaper (Q1) inherits the same rule: it may reclaim the Context record, never the pending rows.

Rejected alternative — refuse-with-`--force`: it converts "close" into a routine `--force` habit, and habitual `--force` is how #271 happens twice [asserted].

## 4. Q3 — Migration for scripts written against today's implicit shared slot

Today's caller shape [measured]: `twig nav 4821` writes `active_work_item_id`; a later `twig tree` reads it. Nothing names a Context. 28 files touch `IContextStore`.

Constraint: twig is single-user and local, so there is **no version skew to manage** [cited 0023 §4] — but silently changing what an existing script targets *is* the 0021 defect in a new hat [cited 0023 §4]. So migration must be **loud or compatible, never silently different**.

| Caller kind today | Post-change behaviour | Rationale |
|---|---|---|
| Targeted (`twig show 4821`, `twig set 4821 …`, all MCP) | **Unchanged.** Needs a Connection only. | 0022 §3 [cited]; MCP already migrated by 0021 |
| STANDING commands in an interactive shell | Auto-open a Context on first standing command, keyed to the shell session, and **say so once**: `opened context ctx-7 (auto)`. Subsequent commands in that shell join it. | Preserves the human's place without a per-surface special case: the *rule* is "standing command with no Context opens one"; the shell just happens to be where the handle persists [asserted] |
| STANDING commands in a script | Same rule → each `twig` invocation opens its **own** Context unless the script exports/passes one. Run N inherits nothing from run N−1. | This is the ruling's required outcome [cited 0022 §6 model B: "a script run twice inherits nothing"] |
| Scripts that *relied* on stickiness across invocations (`twig nav X` then a later separate `twig tree`) | **Break loudly**, not quietly: `tree` with a freshly-auto-opened empty Context prints `no active work item in this context — did you mean 'twig tree <id>'?` and exits non-zero. | A non-zero exit with a fix in the message is the only migration that is not the 0021 defect [asserted] |

Migration aids [asserted]:
- `twig context list` shows what exists, so "where did my place go" is answerable in one command.
- One release of a `TWIG_CONTEXT_COMPAT=sticky` escape hatch reproducing today's shared slot, documented as removal-dated. It is the second behaviour-selecting env var in the codebase after `TWIG_MCP_TOOL_PROFILE` [measured] — acceptable as a temporary, not as the design.
- Do **not** ship a silent compatibility mode. If compat is on, every standing command prints a one-line deprecation to stderr.

## 5. Recommendation (ranked, one pick)

**Pick: E as the model, A as the transport, B as the override, and clean-by-default for anything that is not a shell.**

Concretely:
1. `twig context open` returns a handle; `close` releases it (E).
2. A shell carries it in `TWIG_CONTEXT`, set by an `eval $(twig context open --export)` or the existing oh-my-posh-style shell hook — the shell is already a twig-state-carrying surface [measured, `OhMyPoshCommands.cs:136-163`], so this adds a channel that already exists rather than a new concept.
3. `--context <h>` overrides on any invocation (B), and is **rejected** on targeted commands.
4. No `TWIG_CONTEXT` and a standing command → open a fresh Context, announce it. Never join someone else's.
5. Targeted commands never read, create, or mutate a Context (row 7, by construction).
6. Reaper per §2; close-never-discards per §3.

Ranking of the rest: **B** (safe, unusable alone — the fallback is the real design) > **A alone** (good transport, no lifetime story) > **C** (best ergonomics, worst failure mode: agent supervisors and tmux panes make ancestry lie about task identity, and it needs a carve-out to satisfy row 7) > **F** (only survives scenario 5, which is the one scenario with no state to inherit) > **D** (disqualified by rows 3 and 7 independently).

### Named falsifier

**This recommendation is wrong if, in practice, the *human at a terminal* (scenario 1) ends up needing a shell hook that no other caller needs.** Operationally: if after implementation the shell integration requires per-shell special-casing inside twig's command dispatch — i.e. code that reads "am I attached to a tty?" to decide whether to join or open — then E+A has reintroduced the per-surface rule 0022 deleted [cited 0023 §"clean by default"], and C (ancestry, uniformly applied, with an explicit carve-out for targeted commands) becomes the better trade.

Test that decides it: implement `context open --export` plus the auto-open rule, then grep the dispatch path for tty/interactivity checks. **Zero tty checks → recommendation holds. Any tty check that changes Context *joining* behaviour → falsified.**

Secondary falsifier: if `twig context list` on a real week of use shows leaked Contexts growing faster than the reaper clears them *and* users report confusion about which handle they are in, the handle is too visible and D's ergonomics were worth its correctness cost — reopen the ranking. [asserted]
