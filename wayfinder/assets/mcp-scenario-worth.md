# Memo — which ADO work-management scenarios are worth an MCP tool

**Status:** research finding, for Daniel's decision. Not a charter, not a ticket.
**Date:** 2026-07-31. **Branch:** `docs/wayfinder-1-0-map`.
**Question:** which ADO software-work-management scenarios, as twig performs them, are worth
exposing as MCP tools — judged by what a script CLI *structurally cannot* do (bar 1), and what
an LLM *demonstrably* handles better as a tool than as a command (bar 2)? If none, say none.

---

## The finding, up front

**No scenario clears bar 1. No *new* tool clears bar 2.**

Bar 1 — structural impossibility — comes back **empty**, and that is the substantive result of
this research. The one candidate the record has carried since 0002 §b (**reach**: answering about
data twig never cached) does not survive execution-level inspection. It is a **policy boundary
twig chose**, not a capability a script CLI lacks. Details in §2.

Bar 2 — measurably better as a tool — has exactly **two members, and twig already ships both**:
`twig_batch` and `twig_find_or_create`. 0012 already identified these as "the accidental
exceptions and the shape to generalise." This memo's contribution is the negative half: the
generalisation does **not** produce more tools, because the scenarios that would have justified
them have since been solved elsewhere in the codebase.

Everything else in the 41-tool surface is a 1:1 command proxy that clears neither bar.

🔴 **Correction to 0012, on evidence.** 0012 named two scenarios as most needing composite tools.
**One of them is no longer a case at all.** "Publish a staged seed chain — ordering + partial
failure" was fixed in the **shared domain layer** (`SeedPublishOrchestrator`), not in MCP, and
both surfaces inherit the fix. It is not an argument for an MCP tool any more. Detail in §3.1.

---

## 0. What this memo is grounded in, and what it is not

**Verified by execution this session** (repo at `docs/wayfinder-1-0-map`):

| Claim | How verified |
|---|---|
| **41 MCP tools** | distinct `Name = "twig_*"` values `sort -u` → 41; `McpToolCatalog.AllToolNames` agrees. `grep -c McpServerTool` returns 52 only because it substring-matches the class-level `[McpServerToolType]` on 11 classes (52 − 11 = 41). 0012's 41 was right. |
| **11 advertised by default** | `McpToolCatalog.CompactToolNames`, `src/Twig.Mcp/Services/McpToolCatalog.cs:71-84`. |
| **17 read-only annotated** | `ReadOnlyToolNames`, same file, lines 89-108. |
| **~82 CLI entry points** | `public async Task<int>` methods on `src/Twig/Program.cs`. |
| **`src/Twig.Mcp/Tools/` = 3,062 lines**, 12 files | `wc -l`. |
| Reach lives in **shared domain code** | `Twig.Domain/Services/Sync/WorkItemFetcher.cs` — used by MCP only, but registered for every surface. |
| Publish partial-failure recovery is **shared** | `Twig.Domain/Services/Seed/SeedPublishOrchestrator.cs`, called by both `SeedTools.cs` (MCP) and `SeedPublishCommand.cs` (CLI). |

**Not available, stated rather than worked around:** 0012's underlying research files are Windows
paths from another host (`%TEMP%\twig-review\research-*.md`). I confirmed they are **unreachable
here** (`/tmp/twig-review` does not exist; filesystem search found neither file). So the **18
scenarios exist to me only at the six-cluster granularity 0012 summarises**, plus its two
individually-named hardest cases. §3 is therefore written per cluster and per named case, not per
individual scenario. Where a cluster verdict would change if the individual rows differed, I say so.

---

## 1. The bars, as applied

- **Bar 1 (structural, primary).** Something the script CLI *cannot* do — not "does not currently
  do." The distinction does all the work in this memo. A capability absent from the CLI but
  present in shared domain code is **an unexposed command, not a structural gap**.
- **Bar 2 (empirical, primary).** Something an LLM demonstrably handles better as a tool than as a
  documented command. **Cannot be settled by reading.** No independent benchmark exists; vendor
  numbers give direction only. Every bar-2 verdict below is labelled **ARGUED** and carries a
  falsifier.
- **Bar 3 (lived annoyance).** Admitted only as individual carveouts as they become obvious, never
  as a category. **This memo raises none.** I did not go looking, and nothing surfaced from the
  code that I could honestly attribute to Daniel's daily use rather than my own inference. If
  carveouts exist, they will come from him, not from this document.

---

## 2. Bar 1: the reach argument does not survive

This is the memo's most consequential finding, so it gets shown rather than asserted.

**The claim in the record** (0002 §b, repeated in 0012's "why freeze rather than cut"): MCP has
*reach* — it can answer about data twig never cached, which a script CLI cannot decide to do on
the user's behalf.

**What the code actually shows.**

Reach is implemented in `WorkItemFetcher` (`Twig.Domain/Services/Sync/WorkItemFetcher.cs`):
cache-first, ADO fallback, best-effort cache warm. Its own remarks record that it was extracted
from `Twig.Mcp.Services.WorkspaceContext` when that mirror was deleted (0016), explicitly so that
"**every surface can resolve it from the shared registration**."

It lives in `Twig.Domain`. It is registered for all surfaces. Today only MCP calls it
(`ReadTools`, `NavigationTools`, `TrackingTools`). The CLI's `show` instead does a cache-only
lookup and, on a miss, prints:

> `error: Work item #N not found in local cache. Run 'twig set N' to fetch it.`
> — `src/Twig/Commands/ShowCommand.cs:103`

That is **a deliberate contract, not an incapacity.** `SyncResult.cs` states the rule directly:
staleness and non-caching are *outcomes* each surface interprets, and "the script CLI gets a
network-free contract, MCP may treat this exactly as it treats `NotCached` and reach on its own
judgement" (`Twig.Domain/Services/Sync/SyncResult.cs:26-44`). `StaleHint.cs` says the same about
why machine formats render nothing: to keep "a stable, network-free contract."

Further, the CLI **already has explicit reach** where it wants it: `--refresh` on `show`, `tree`,
`workspace`/`ws`; plus `twig refresh`, `twig sync`, and `twig set N` (which fetches on miss —
`SetCommand.cs:66` handles `FetchedFromAdo`).

**Therefore:** the gap between CLI and MCP on reach is **one flag wide**. A `--reach` /
`--fetch-missing` option on the script CLI's read commands would close it entirely, using code
that already exists and is already registered. Nothing structural stands in the way.

🔴 **This weakens the strongest stated argument against cutting the MCP.** 0012's "against
cutting" rested on two things: daily use, and reach. Reach is now the weaker of the two. Daily
use is untouched by this finding and remains real.

**What would change this verdict:** a demonstration that the script CLI cannot make the
fetch decision *in the same call* in a way that matters — e.g. an agent workflow where the
round-trip through a second command loses state that only the model holds. I found no such
case in the code. If Daniel has hit one in daily use, that is exactly the kind of bar-3
carveout this memo declines to invent on his behalf.

**Bar 1 members: none.**

---

## 3. Bar 2: the two hardest cases, re-examined

0012 named two scenarios as most needing composite tools. Both need correcting.

### 3.1 Publish a staged seed chain — 🔴 **NO LONGER A CASE**

0012's argument: children need parent IDs that don't exist until creation; the local→remote ID map
lives only in model context; retry after partial failure duplicates everything already created,
with no safe recovery available to the model.

**That hazard has been closed — in `Twig.Domain`, for every surface.**
`SeedPublishOrchestrator.cs` now implements, at lines 187-296:

- **Intent recorded durably *before* the ADO call**, deliberately outside the local transaction so
  it survives the crash it exists to witness (implements 0001 §4).
- **Two-source recovery before creating anything**: the intent ledger's own `PublishedId`, then
  `FindPublishedIntentAsync` against ADO narrowed by an in-flight tag plus title/type/timestamp.
- **A durable `publish_id_map`** keyed on `StagedIdentity` rather than the negative alias, so a
  cache rebuild cannot mis-resolve it (fixes #280).
- **`SeedLinkRepair`** to reconcile stale links and parent refs after a partial publish — exposed
  on **both** surfaces (`twig seed reconcile` at `Program.cs:763`, and `twig_seed_reconcile`).

The comments in that file are an explicit post-mortem of #270 and #280 — the very issues 0012 was
restating as a general API hazard.

**Consequence:** the ID map no longer "lives only in model context," and retry no longer
duplicates. The scenario that most justified a composite MCP tool was instead **solved one layer
down**, which is the better outcome and the one 0012's own freeze policy was designed to produce
("new agent capability goes through the script CLI first"). Freezing worked.

**Clears no bar.** Not because it does not matter, but because it is done.

### 3.2 Triage a batch — **CLEARS BAR 2 (ARGUED), and is already built**

The failure modes are real and are properties of the *transport*, not the domain: N+1 call
explosion (0012: 60 items ≈ 180 calls), context-window truncation silently reported as success,
non-atomic re-runs double-commenting.

`twig_batch` addresses these directly (`BatchTools.cs`): a JSON graph of `sequence` / `parallel` /
`step` nodes, max 50 operations, max 3 nesting levels, no recursion, per-batch timeout capped at
300 s, and `onError: continue` for partial-failure recovery inside a sequence.

Note the CLI's `twig batch` is a **different and narrower thing** — state transition + field
updates + a note, applied to N items in one PATCH per item (`BatchCommand.cs`). It is not a
heterogeneous tool graph. They share a name and nothing else. Worth knowing before anyone
concludes the CLI already covers this.

**Why this is bar 2 and not bar 1:** a shell script sequences and parallelises natively. The
argument for the tool is **token and round-trip economy plus a single reported outcome**, which is
an efficiency claim about the model — exactly the kind of claim that cannot be settled by reading.

> **ARGUED, not proven. Falsifier:** run the same 60-item triage twice against the same fixture —
> once via `twig_batch`, once via the model driving the script CLI with a skill document — and
> measure tokens-to-completion, wall time, and correctness (double-comments, silent truncations).
> If the CLI path is within noise on tokens **and** no worse on correctness, the Playwright
> "CLI + skills beat MCP" finding applies to twig and `twig_batch` loses its justification. This
> is a genuinely runnable experiment and is the single highest-value thing anyone could do next.

### 3.3 Idempotent creation — **CLEARS BAR 2 (ARGUED), and is already built**

`twig_find_or_create` performs a mandatory dedup check on title+type under a parent
(`CreationTools.cs:128-141`). Verified: **the CLI's `new` has no dedup path at all** —
`skipDuplicateCheck` appears only in `Twig.Mcp` and its batch dispatcher.

The argument is that models retry, and a retry of a create is a duplicate unless the tool absorbs
the check. GitHub's `issue_write` declares `idempotentHint: false` for exactly this reason.

> **ARGUED, not proven. Falsifier:** measure duplicate-creation rate across a repeated
> create-heavy agent workflow, with and without the dedup tool. If a documented
> "query-then-create" CLI recipe produces the same duplicate rate, the tool is not earning its
> place — the model was capable of the two-step all along.

**This one has a cheap alternative worth naming:** a `--dedup` flag on `twig new` would put the
same behaviour on the script CLI, and would then need the falsifier above to decide between them.

---

## 4. The 18 scenarios, by cluster

At the granularity available (see §0). **No cluster produces a new tool.**

| Cluster (0012) | Scenarios | Bar cleared | Why |
|---|---|---|---|
| **A. Navigation / context loading** | 3 | **None** | Fully served by `show`, `tree`, `workspace`, `set`, `query` on both surfaces. The only MCP-side difference is reach — §2 shows that is a flag, not a gap. |
| **B. Triage & classification** | 3 | **Bar 2 (ARGUED)** — via the *existing* `twig_batch` | §3.2. Produces no new tool. |
| **C. Planning & decomposition** | 4 | **None**, except idempotent creation → **Bar 2 (ARGUED)** via existing `twig_find_or_create` | §3.3. `seed new` / `seed chain` / `seed link` all exist on both surfaces. |
| **D. Status reporting & narrative** | 3 | **None** | Pure read + summarise. `query`, `show`, `history`, `sprint`, `workspace` all emit machine formats (`json-full`, `json-compact`). This is precisely the class the Playwright finding says CLI + skills serves as well or better. |
| **E. Linking & code-context join** | 3 | **None** | `link-parent`, `link-artifact`, `link-reparent`, `seed link`, and `seed publish --link-branch` all exist on the CLI. |
| **F. Staging & publication** | 2 | **None** — was the strongest case, now closed | §3.1. Solved in `SeedPublishOrchestrator`; both surfaces inherit it. |

**Where this verdict is soft:** clusters B and C are the two where an individual scenario row —
which I could not read — might carry a specific composite-intent case that the cluster summary
flattens. If Daniel wants that gap closed, the research files need to be recovered from the
Windows host. I would not expect it to change the recommendation, because both clusters already
route to tools that exist.

---

## 5. Scenarios and tools that clear no bar — named plainly

This is a result, not a gap.

**Clearing no bar: 39 of the 41 tools.** Every 1:1 command proxy — `twig_show`, `twig_tree`,
`twig_query`, `twig_new`, `twig_set`, `twig_state`, `twig_update`, `twig_patch`, `twig_note`,
`twig_link*`, `twig_delete`, `twig_discard`, `twig_sync`, `twig_refresh`, `twig_history`,
`twig_sprint`, `twig_area`, `twig_config`, `twig_process`, `twig_track`, `twig_untrack`,
`twig_workspace`, and the nine `twig_seed_*` tools — has a script CLI counterpart reaching the
same domain service. None clears bar 1. None has a bar-2 argument beyond "it is one call instead
of one process spawn," which is a latency claim, not a capability or an accuracy one.

**A small honest sub-case.** Six tools have **no** CLI counterpart today: `twig_cache_status`,
`twig_tracking_status`, `twig_list_workspaces`, `twig_children`, `twig_parent`,
`twig_verify_descendants`. Absence from the CLI is **not** bar 1 — each is a thin read over shared
repository code and could be a command tomorrow. `twig_verify_descendants` is the only one with
non-trivial logic behind it (`DescendantVerificationService`), and that logic also sits in
`Twig.Domain`. **If the MCP is ever cut, these six are the things that would actually be lost**,
and each is a small CLI command, not a research question. That is worth writing down now while
it is cheap to know.

**Clearing bar 2 (ARGUED), already built, no work implied:** `twig_batch`, `twig_find_or_create`.

**One non-tool improvement the evidence does support:** 0012's cache guidance recommended every
read payload carry an `as_of` field so a model can report staleness honestly. Verified: MCP read
payloads **do not** carry one, and machine-format CLI output deliberately does not either. This is
a **field on existing responses**, not a tool, and it is the MCP-side expression of 0001 §5's
user-owned sync boundary. Noted for whoever picks up the surface next; it does not need a map.

---

## 5a. 🔴 Added after review — the implied-context defect, and the cut it justifies

**Raised by Daniel, 2026-07-31, after reading the draft above.** This did not come out of the
scenario research; it is a defect in the surface the research did not look for. It is recorded
here because it changes the recommendation in §6.

### The defect

The active work item is stored in the **shared SQLite context store** — one row, one key
(`active_work_item_id`, `SqliteContextStore.cs:12`). It is **not** per-connection and not
per-session. Both the CLI and the MCP read and write that same pointer.

Five MCP **mutations** fall back to it when `id` is omitted — every one of them a write:

| Tool | Site |
|---|---|
| `twig_state` | `MutationTools.cs:33` |
| `twig_update` | `MutationTools.cs:160` |
| `twig_patch` | `MutationTools.cs:235` |
| `twig_note` | `MutationTools.cs:318` |
| `twig_discard` | `MutationTools.cs:411` |

**The failure mode:** the user runs `twig set 4102` in a shell; a model mid-task calls
`twig_note` with no id and comments on 4102 instead of the item it believed it was on. Neither
side is warned. This is a **silent cross-surface write to the wrong work item**, and no test can
catch it because both surfaces are behaving as specified.

Aggravating detail, verified: MCP writes **shell prompt state** from four sites —
`ContextTools.cs:91` and `MutationTools.cs:62, 201, 504`. So a model call can change **what the
user's terminal prompt displays**.

### The decision (Daniel, 2026-07-31): MCP becomes explicit-context only

Every MCP tool takes its target explicitly. No tool infers a target from the shared pointer.

**Signature changes (5):** the five mutations above — `id` becomes required. Not a redesign; the
parameter already exists and is merely optional.

**Needs a decision, not a deletion (1):** `twig_sync` resolves the active item to decide what to
pull (`MutationTools.cs:476`). It needs an explicit target or a defined default.

**Left alone deliberately (1):** `twig_workspace` reads the active item as one field of a
dashboard (`ReadTools.cs:56`). Reporting *"here is what the workspace currently points at"* is
honest read-only observation, not an implied write target. **Open question for Daniel:** if
"explicit context only" is absolute, this is a ninth affected tool and the response loses a field.

### The cut this justifies: 41 → 38

Three tools are cut **as consequences of the explicit-context rule**, not as a quantity trim.
The distinction matters for the record: these are cut because the rule makes them illegal or
pointless, which is a reason that survives review — "we had too many" is not.

1. **`twig_set` — ILLEGAL under the rule.** Its entire job is writing the shared pointer. It also
   warms a working set around the target (parent chain, two levels of children, links) and
   rewrites prompt state (`ContextTools.cs:78-91`). Delete, do not deprecate.
2. **`twig_parent` — POINTLESS under the rule** (`NavigationTools.cs:171`).
3. **`twig_children` — POINTLESS under the rule** (`NavigationTools.cs:157`).

Both crumbs exist so a model with implied context could feel its way one hop at a time. With
explicit ids the model already holds the id, and `twig_tree` returns the hierarchy in one call.
Cutting them **reduces round-trips**, which is the same N+1 argument that justifies `twig_batch`
— so this cut is consistent with §3.2's evidence rather than in tension with it.

**Tested and NOT cut**, so the boundary is visible: the four remaining no-CLI-counterpart tools
(`twig_cache_status`, `twig_tracking_status`, `twig_list_workspaces`, `twig_verify_descendants`)
all take explicit arguments or none. The rule does not touch them and deleting them would be
genuine capability loss — `twig_verify_descendants` has real logic behind it
(`DescendantVerificationService`).

### 🔴 The caution that could reverse this

`twig_set` is in the **advertised default eleven**. It is therefore likely something Daniel or
Copilot reaches for routinely. Cutting it means a model can no longer say "let's work on 4102"
and have that stick across calls — every subsequent call must carry the id.

**That is the intended behaviour change, and it is also the one that will be felt.** If the
stickiness turns out to be load-bearing in daily use, that is a legitimate **bar-3 carveout** and
it reverses this specific cut. This memo does not claim to know; only Daniel's usage can say.

### Relationship to the freeze

This is **not** tool growth and does not conflict with 0012. The count goes **down**. But it **is
a breaking change** to the MCP surface: prompts and habits relying on "operate on whatever is
active" will start erroring. That is the point — they should error rather than guess.

---

## 6. Recommendation

The research question has a clean answer: **nothing new should be built.** What remains is a
decision about the surface that exists, and that decision is Daniel's.

### The options

**A. Single ticket: keep the surface, close the question. — RECOMMENDED**
Record this memo's finding as the answer to "what should MCP expose," which is the question a map
would have been chartered to answer. It is now answered: *nothing new*. Convert the freeze from a
holding position into a settled scope — the 41 tools are the surface, permanently, unless a
falsifier in §3 comes back the other way. Optionally add the two cheap items named above: an
`as_of` field on read payloads, and a `--reach` flag on the script CLI's reads that closes §2's
one-flag gap. Both are small, both are independently useful, neither needs a map.
*Cost: one ticket. Risk: none — it ratifies the status quo with evidence behind it.*

**B. Cut the MCP.**
Now genuinely live: bar 1 is empty, and the strongest argument against cutting (reach) has been
shown to be a flag. But it is **not** what the evidence recommends. Against cutting: it is
~3,062 lines of tool code with a passing suite, it is in daily use under Copilot CLI, it costs
nothing to keep frozen, and the six no-CLI-counterpart tools in §5 would need writing as commands
first. Bar 1 being empty argues against *growing* the surface; it does not argue for destroying
working code that someone uses every day. **Cut if the §3.2 falsifier comes back showing the CLI
path is equal or better** — that would remove the last empirical justification. Not before.

**C. Trim to the advertised 11 and delete the other 30.**
The middle option, and the one industry practice actually points at (Sentry: ~48 catalog, 9
advertised, "never exceed 25"; Anthropic: 30+ → "switch to search + execute"). Twig already gets
most of this benefit for free — only 11 are advertised by default, so the selection-degradation
cost the vendor numbers describe is **already avoided**. Deleting the hidden 30 buys maintenance
reduction, not model accuracy. Worth doing only if that maintenance is actually hurting.
*Rank: third. Real, but it solves a problem the compact profile already solved.*

**D. Charter a wayfinder map.**
**Do not.** A map earns its existence when there is more work than one session can hold and the
route is foggy. Neither holds: the research question resolved in one session, and it resolved to
"build nothing." Chartering a map to design a surface that should not grow would be ceremony.
`wayfinder/map.md`'s Lineage note calling the MCP map "not yet chartered" should be updated to
point at this memo instead — a one-line edit, and only if Daniel picks A.

### Ranked recommendation — SUPERSEDED, see below

**A** was the right answer to the *research question* and remains so. It is now **incomplete**,
because §5a identified a real defect in the surface that the scenario research did not look for.
Preserved rather than deleted, per the reporting bar.

---

### 🔴 Current recommendation (2026-07-31, after Daniel's review): TWO tickets

The research finding and the surface defect are separate calls and belong in separate tickets.

**Ticket 1 — Close the scenario question.** Record this memo's finding: no scenario clears bar 1;
the only two bar-2 members are already built; nothing new should be built. Convert 0012's freeze
from a holding position into settled scope, with the §3 falsifiers named as the only things that
could reopen it. *This is option A, unchanged.*

**Ticket 2 — Make MCP explicit-context only.** Per §5a: five mutation signatures take a required
`id`; `twig_sync` gets an explicit target or a defined default; `twig_set`, `twig_parent`, and
`twig_children` are deleted (41 → 38). Breaking change, deliberately. Carries the §5a caution
about `twig_set`'s daily use, and the open question about whether `twig_workspace` may still
*read* the pointer.

**Still not a map.** Both tickets are one session's work each and neither route is foggy.
`wayfinder/map.md`'s Lineage note calling the MCP map "not yet chartered" should point at this
memo instead — a one-line edit.

**Order matters:** ticket 1 first. It ratifies "build nothing new," which is the frame that makes
ticket 2 legible as *removing a hazard* rather than as reshaping a surface someone might then be
tempted to grow.

**Two optional add-ons, unchanged and independently justified:** an `as_of` field on read payloads
(§5), and a `--reach` flag on the script CLI's reads closing §2's one-flag gap.

The one thing that would genuinely move any of this: **run the §3.2 falsifier.** It is still the
only experiment on the table that could change a verdict rather than confirm one.

---

## 7. Corrections to the record made by this memo

Per the reporting bar — arguments that turned out wrong are corrected here rather than dropped.

1. **0002 §b's "reach" as a structural MCP advantage — WEAKENED.** It is a chosen contract
   boundary (0004 §3) plus one missing CLI flag, not a capability the script CLI lacks. §2.
2. **0012's hardest case #1 (staged seed publish) — NO LONGER A CASE.** Closed in
   `SeedPublishOrchestrator` for all surfaces. §3.1. This is the freeze policy working as
   designed, and should be read as a success of 0012, not a failure of it.
3. **0012's "the surface may be redundant with its own script CLI" (the uncomfortable finding) —
   SUPPORTED, more strongly than 0012 put it.** Verified at the code level across all six
   clusters, not inferred from one vendor README.
4. **This memo's own §6 recommendation — SUPERSEDED mid-session.** It read "one ticket, ratify the
   status quo," which was right for the research question and wrong as a whole-surface
   recommendation once §5a's implied-context defect was raised. Now two tickets. The original is
   preserved above rather than quietly rewritten.
5. **The research question's scope was too narrow to find §5a.** Asking "which scenarios deserve a
   tool" cannot surface "the tools we have share mutable state with another surface." Worth
   remembering: a worth-of-scenarios lens is blind to correctness defects in the existing surface,
   and the defect found here was larger than anything the scenario lens turned up.
