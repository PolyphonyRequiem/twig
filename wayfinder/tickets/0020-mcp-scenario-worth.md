---
id: 0020
title: What should the MCP expose? — the scenario question, closed
type: research
status: closed
claimed_by: starbright-engineering (session 2026-07-31)
blocked_by: [0012]
---

## Question

0012 froze the MCP surface at 41 tools and **deferred** its own main question — which scenarios
the toolkit serves and what granularity follows — preserving the evidence for whoever lifted the
freeze. The freeze lifted 2026-07-31 via its own defined hook (the 1.0 map).

Daniel reframed the question before it was re-asked. Rather than *how should the MCP surface be
shaped*, he asked:

> "research what kinds of software work management in ADO scenarios based on twig WOULD be
> worthwhile to put into an MCP? If anything"

**Decide:** which ADO software-work-management scenarios, as twig performs them, are worth
exposing as MCP tools — judged by what a script CLI **structurally cannot** do (bar 1), and by
what an LLM **demonstrably** handles better as a tool than as a documented command (bar 2).
Lived annoyance in Daniel's own daily use (bar 3) is admitted only as individual carveouts as
they become obvious, never as a category.

**"None" is a legitimate finding** and was named as such in advance, so that a null result could
not be read as a failure of the research.

## Answer

**NOTHING NEW SHOULD BE BUILT.** Full memo, with per-cluster verdicts and code citations:
[`../assets/mcp-scenario-worth.md`](../assets/mcp-scenario-worth.md).

### Bar 1 (structural) — EMPTY

No scenario clears it. The only candidate the record has carried since 0002 §b — **reach**,
answering about data twig never cached — **does not survive execution-level inspection.**

Reach is implemented in `Twig.Domain/Services/Sync/WorkItemFetcher.cs`, whose own remarks record
that it was moved there by 0016 explicitly so "every surface can resolve it from the shared
registration." It is registered for all surfaces; only MCP calls it. The CLI's cache-only read is
a **deliberate contract** — `SyncResult.cs:26-44` states that staleness and non-caching are
outcomes each surface interprets, and that "the script CLI gets a network-free contract." The CLI
already has explicit reach where it wants it (`--refresh` on `show`/`tree`/`workspace`, plus
`refresh`, `sync`, `set`).

**The CLI↔MCP gap on reach is one flag wide.** That weakens 0012's strongest stated argument
against cutting; daily use remains untouched and is now the stronger of the two.

### Bar 2 (empirical) — two members, both already built

`twig_batch` (N+1 collapse, silent-truncation, non-atomic re-runs) and `twig_find_or_create`
(dedup on retry; verified the CLI's `new` has **no** dedup path at all). Both are **ARGUED, not
proven** — no independent benchmark exists and vendor numbers give direction only. Each carries a
falsifier in the memo (§3.2, §3.3). Neither implies new work.

### Bar 3 — none raised

Not fished for, per the framing. If carveouts exist they come from Daniel's usage, not from this
research.

### 🔴 Correction to 0012, on evidence

**0012's hardest case #1 — "publish a staged seed chain: ordering + partial failure" — is NO
LONGER A CASE.** The hazard was closed in `SeedPublishOrchestrator.cs:187-296`, in the **shared
domain layer**, so both surfaces inherit it: intent recorded durably *before* the ADO call and
deliberately outside the local transaction; two-source recovery (the intent ledger's own
`PublishedId`, then `FindPublishedIntentAsync` narrowed by an in-flight tag plus title/type/
timestamp); `publish_id_map` re-keyed on `StagedIdentity` (#280); `SeedLinkRepair` exposed on both
surfaces. The ID map no longer lives only in model context, and retry no longer duplicates.

**This is 0012's freeze policy working exactly as designed** — capability went through the shared
path instead of becoming an MCP tool. It should be read as a success of 0012, not a failure of it.

### The 18 scenarios, by cluster

No cluster produces a new tool. A: served by existing reads on both surfaces. B: routes to
existing `twig_batch`. C: routes to existing `twig_find_or_create`. D: pure read+summarise, the
class the Playwright finding says CLI+skills serves as well or better. E: all link verbs exist on
the CLI. F: closed by the correction above.

**Stated limit:** 0012's underlying research files are Windows paths from another host
(`%TEMP%\twig-review\research-*.md`) and were verified **unreachable** on this box. The 18
scenarios were therefore worked at the six-cluster granularity 0012 summarises, plus its two
named hardest cases. Clusters B and C are where an unread individual row could soften a verdict;
both already route to tools that exist, so it is not expected to change the finding.

### Consequences

- **0012's freeze becomes settled scope**, not a holding position. 41 tools was the ceiling; it
  is now also the answer. The §3 falsifiers are the only things that reopen it.
- **0012's rejection of CLI↔MCP parity as a goal stands** — it never lifted with the freeze, and
  this research independently supports it.
- **Cutting the MCP entirely was treated as live** and is not recommended: an empty bar 1 argues
  against *growing* the surface, not for destroying working code in daily use.
- **No wayfinder map.** The question resolved in one session, to "build nothing." A map to design
  a surface that should not grow is ceremony. This ticket replaces that map.
- **Six tools have no CLI counterpart** (`twig_cache_status`, `twig_tracking_status`,
  `twig_list_workspaces`, `twig_children`, `twig_parent`, `twig_verify_descendants`). Absence from
  the CLI is **not** bar 1 — each is a thin read over shared code. Recorded because they are what
  a future cut would actually lose. Two of the six are deleted by 0021 for an unrelated reason.
- **One non-tool item the evidence supports:** MCP read payloads carry no `as_of` field, and
  machine-format CLI output deliberately carries none either. That is a field on existing
  responses, not a tool — the MCP-side expression of 0001 §5's user-owned sync boundary.
- **0009 (MCP hints contract)** was parked pending this question. It is now unblocked, and belongs
  to whoever picks up the surface next.

### Partially resolves a fog item

The map's **"Who owns the sync boundary when an LLM triggers a fetch?"** is narrowed but not
closed. §2 establishes that reach is a **policy choice already made per-surface** (0004 §3's
outcome rule), not an unresolved ownership question — the CLI chose network-free, MCP chose reach,
and both call the same shared fetcher. What remains open is whether the CLI should expose an
opt-in `--reach` flag, which is a product call, not an architectural one.
