---
id: 0012
title: MCP toolkit scenarios and tool granularity
type: grilling
status: closed
claimed_by: starbright-baseline (session 2026-07-26)
blocked_by: [0002]
---

## Question

Ticket 0002 established that MCP should be an **LLM toolkit rather than a CLI proxy**, but
"things an LLM might reasonably want to do" was a phrase, not a list. This ticket turns it
into one, and decides the tool granularity that follows.

Twig's MCP surface today: **41 tools**, ~39 of them 1:1 command proxies, 11 advertised by
default (`CompactToolNames`, `McpToolCatalog.cs:70`), rest behind `--tool-profile full`.
Two accidental exceptions already exist and are the shape to generalise: `twig_batch` (a
sequence/parallel/step graph) and `twig_find_or_create` (encodes idempotent-creation
intent).

**Decide:** which scenarios the toolkit serves; how many tools that implies; what the
default-advertised set is and by what *criterion*; and what happens to the ~39 proxies.

## Evidence (research, 2026-07-26)

Full findings: `%TEMP%\twig-review\research-mcp-tool-design.md` (~36KB, claims tagged
OFFICIAL / OBSERVED / VENDOR-BLOG / UNVERIFIED) and
`research-llm-worktracking-scenarios.md` (18 scenarios, ~31KB, cited).

### The industry is actively leaving 1:1 proxying

Not a preference — a documented migration:

- **GitHub** (90 tools) publishes an alias table collapsing **25 one-per-endpoint tools
  into 7** (`get_issue` → `issue_read`; `get_workflow` + 6 others → `actions_get`) via a
  `method` enum. Consolidates along the **resource** axis, *never across read/write*.
  Growth handled by 18 toggleable `--toolsets` (5 on by default).
- **Sentry**: ~48 catalog tools, **9 advertised**. Documented policy: *"Target ~20 publicly
  visible tools. Never exceed 25."*
- **Notion** is *sunsetting* its OpenAPI-generated 22-tool proxy for a ~19-tool
  hand-designed intent surface.
- **Anthropic's official `mcp-server-dev` skill**: 1–15 tools "sweet spot", 15–30 "audit
  for merges", **30+ "switch to search + execute, promote top 3–5"**.

**Twig's 41 tools sit in the 30+ band.**

### Tool count measurably degrades selection

Vendor-run measurements, direction corroborated by the behaviour of GitHub/Sentry/Notion:

- Claude MCP eval accuracy, Tool Search off→on: **Opus 4: 49% → 74%**;
  **Opus 4.5: 79.5% → 88.1%**.
- Anthropic docs: selection *"degrades once you exceed 30–50 tools."*
- Context cost: 58 tools across 5 servers ≈ **55K tokens before the first turn**.
- Programmatic tool calling: 43.6K → 27.3K tokens (**37% less**) *and* accuracy up
  (GAIA 46.5 → 51.2).

No independent benchmark exists — magnitudes are vendor-reported. Treat direction as
reliable, exact numbers as indicative.

### Candidate scenarios — 18, in six clusters (~11–13 tools)

| Cluster | Scenarios | Tools |
|---|---|---|
| A. Navigation / context loading | 3 | 2 |
| B. Triage & classification | 3 | 2 |
| C. Planning & decomposition | 4 | 2–3 |
| D. Status reporting & narrative | 3 | 1–2 |
| E. Linking & code-context join | 3 | 2 |
| F. Staging & publication (twig-specific) | 2 | 2 |

Sources actually read: Azure DevOps MCP README/EXAMPLES/TOOLSET/HOWTO, Linear MCP "Common
use cases" (6 worked prompts), mcp-atlassian Common Workflows, GitHub MCP README + raw
tool JSON schemas. For scale: ADO's work-item domain alone is 30+ primitives;
mcp-atlassian ships 98 tools.

### The two scenarios that most need composite tools

1. **Publish a staged seed chain** — ordering + partial failure. Children need parent IDs
   that don't exist until creation, and the local→remote ID map lives only in model
   context. GitHub declares `issue_write` `idempotentHint: false`; **retry after partial
   failure duplicates everything already created**, with no safe recovery available to the
   model. This is #270 and #280 restated as a general API hazard, and it directly confirms
   0001 §4 (record intent before the call).
2. **Triage a batch** — N+1 explosion (60 items ≈ 180 calls), silent context-window
   truncation *reported as success*, non-atomic re-runs double-comment.

### Three transferable principles

1. **The command list is not the tool list.** Consolidate by resource with a `method` enum;
   keep read and write split (Anthropic's Directory *rejects* tools doing both); alias old
   names rather than breaking them.
2. **Keep the escape hatch, typed not raw.** Sentry's `search_*_tools` (returns
   inputSchema) + `execute_*_tool` (schema-validated) is now the spec's own
   catalog→inspect→execute pattern. **`twig_batch` is already this idea** — keep its name
   and schema stable for prompt caching.
3. **Each tool should absorb work the LLM does badly.** The `find_or_create` case
   generalised: bundle chains, prefer plural signatures, semantic IDs over UUIDs,
   `concise`/`detailed` modes (~⅓ the tokens), truncate loudly, and make errors carry
   recovery hints.

### Cache guidance from the research

Timeline/activity digests (immutable revisions), dedup search, hygiene sweeps and
working-set reads can serve from SQLite. **Publish and any write-apply path must refresh
first** to avoid lost updates. Recommendation: every read payload carries an `as_of` field
so the model can report staleness honestly — which is the MCP-side expression of 0001 §5's
user-owned sync boundary.

### The uncomfortable finding

Microsoft's own Playwright README now argues **CLI + skills beat MCP for coding agents** on
token efficiency. Twig already ships a CLI with machine-readable output (experience 2). If
that argument holds, part of twig's MCP surface may be *redundant with its own script CLI*,
and the toolkit should be scoped to what genuinely needs to be an MCP tool rather than a
documented command. This should be tested, not assumed — it argues against building 13 new
tools reflexively.

## Owner's position (2026-07-26) — FREEZE, don't cut

Owner, on reading the research: *"I'd be willing to 'cut' the MCP in favor of a scriptable
CLI for now"* — then, on being offered the narrower version, *"freeze is more what I
meant."*

**The decision: freeze the MCP surface. Do not delete it.**

- **No new MCP tools.** The 41-tool catalogue stops growing.
- **No CLI↔MCP parity work.** Parity was already established as partly the wrong goal
  (§d above); freezing makes it moot rather than merely questionable.
- **Any new agent capability is tried through the script CLI (experience 2) FIRST.** Only
  when a scenario provably cannot be scripted does it become a candidate MCP tool — and
  that failure is then the *evidence* for the tool, rather than an assumption.
- **Existing tools keep working.** ~1,300 lines with 1,308 passing tests, in daily use
  (the owner runs twig's MCP server under Copilot CLI). Nothing is deleted.

### Why freeze rather than cut

Cutting was considered and rejected as too strong. Against cutting: MCP is live code the
owner uses daily, and it has **reach** (0002 §b) — it can answer about data twig never
cached, which a script CLI cannot decide to do on the user's behalf. The Playwright
finding is one vendor's README about coding agents specifically, which is a datapoint and
not a verdict.

For freezing: it captures the whole benefit of the evidence — *don't build 13 new tools* —
at zero risk, and it converts an assumption into an experiment. If the script CLI turns
out to serve the 18 scenarios, that is real usage data for #282's 1.0 map to make the
final call on. If it doesn't, the gaps are named and specific.

### What this resolves elsewhere

- **This ticket's main question is deferred, not answered.** Scenario enumeration and tool
  granularity are no longer urgent, because nothing is being built. The evidence above
  stands as the input for whenever the freeze lifts.
- **0002 (four experiences, one seam?) shrinks.** With MCP frozen, the live seam question
  is CLI-vs-TUI — and the owner has already said those serve the same person. That is a
  materially smaller question than the original four-way one.
- **0007 (single composition root) gets cheaper to reason about but NOT easier to do.**
  MCP's root still exists and still hand-mirrors CLI DI (`WorkspaceContextFactory.cs:30-33`).
  Freezing stops the divergence growing; it does not close the gap that produced #269,
  #270 and #279. Registration completeness tests (0008) therefore remain worth doing.
- **0009 (MCP hints contract)** should be parked while the freeze holds — specifying a
  hint contract for a surface under a build-freeze is premature.

### Exit condition

The freeze lifts when a concrete scenario is shown to be unserveable by the script CLI, or
when #282's 1.0 map revisits the surface question with usage data. Whoever lifts it should
start from the evidence section above rather than re-deriving it.

## Answer

**FROZEN (2026-07-26).** MCP surface frozen at 41 tools: no new tools, no parity work, new
agent capability goes through the script CLI first. Existing tools keep working and are not
deleted. Scenario enumeration and granularity are deferred with the evidence preserved
above; the freeze lifts on a demonstrated script-CLI gap or at the 1.0 map.
