# Twig 1.0 — wayfinder map

## Destination
**twig 1.0 shipped**: a single-user local ADO tool, shipped as one binary, with two
surfaces — a CLI serving both interactive and scripted use, and a TUI — whose correctness
twig can defend, and whose docs are true.

Reached when 1.0 is published and installable on all three supported platforms.

**This is a SHIPPED-ARTIFACT map, not a planning map.** Unlike its sibling
[the architecture map](../wayfinder/map.md) — whose destination is decisions-complete and
whose standing rule is "produce decisions, not deliverables" — this map's tickets may and
usually do produce code. Where an architecture ticket decided something and did not build
it, the build belongs here.

## Lineage
Three sibling maps, each with its own destination. None is a child of another.

- **[Architecture map](../wayfinder/map.md)** — destination: decisions-complete. **Closed
  input to this map.** Its `Decisions so far` are consumed as settled, never restated here.
  Three of its rulings are decided-but-unbuilt and graduate into this map's execute phase:
  the capability-seam collapse (its 0002), the reconciliation module (its 0004), and the
  three-argument `Resolve` (its 0006).
- **This map** — destination: 1.0 shipped. Ends at publication.
- **MCP experience map** — *not yet chartered.* Destination roughly: where MCP fits in
  twig's design, its principles, and what it should expose. **MCP is OUT of 1.0** (see
  Decisions), so this map does not block on it and it does not block this map. If it
  charters and resolves early enough, adding MCP back to 1.0 is a scope amendment made
  deliberately — not an assumption baked in now.

Ticket ids here start at **1001** rather than 0001, because the architecture map's tickets
are 0001–0019 and both maps are read in the same sessions. Refer to tickets by name; the
number disambiguates only when two maps are open at once.

## Notes
- Domain vocabulary: `CONTEXT.md` at repo root is authoritative for NAMES. `Connection`
  (one {org}/{project} ADO endpoint) and **Bench** (a named, switchable set of work items);
  `Workspace` is retired; `Sprig` is RESERVED.
- Surfaces are counted by **interaction model, not audience**. The CLI is ONE surface
  serving two experiences — interactive/rich and scripted/machine-readable. Do not treat
  script mode as a separate surface, and do not let a decision serve one experience at the
  other's expense.
- Skills every session should consult: `codebase-design`, `grilling`, `decision-mapping`,
  `improve-codebase-architecture`. `wayfinder` for map operations.
- **SDK on this Linux box:** `global.json` pins `11.0.100-preview.5.26302.115`, which is NOT
  the system dotnet (8.0.129). Export `DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5` and
  prepend it to `PATH`, or every suite fails with a misleading exit 145. `AGENTS.md` says
  the pinned SDK is installed system-wide — that is Windows-host guidance and is false here.
- **Tests:** `tools/run-tests.sh`, grep `TWIG-VERDICT`. **Never grep `Passed!`** — an
  aborted run prints a false-green summary above `Test Run Aborted` and exits non-zero.
  Full suite is ~7,600 tests, ~5–7 minutes; run it in the background.
- One ticket per session.

## Decisions so far
<!-- scope calls banked while chartering, 2026-07-31, owner in the loop -->

- **Destination sentence** — as above. Fixes scope for every ticket on this map. Explicitly
  names the CLI as serving both interactive and scripted use, so that "one surface, two
  experiences" cannot quietly decay into "the rich CLI, plus whatever scripts get."

- **One binary, `twig tui` as a mode** — the TUI folds into the main binary for 1.0.
  The stated blocker was false: `src/Twig.Tui/Twig.Tui.csproj:6-10` claims *"Terminal.Gui v2
  beta does not support AOT"* and *"Terminal.Gui relies on reflection; trimming is
  intentionally not enabled."* A spike disproved the first by execution (19 MB native ELF
  rendering the full TUI, handling input, exiting 0 — branch `origin/spike/tui-aot`
  @ `0a8c185d`, writeup `AOT-SPIKE-FINDINGS.md`) and inverted the second: the failure mode
  is **trimming, not native codegen**, and the fix is `TrimmerRootAssembly(Terminal.Gui)` +
  `TrimMode=partial` rather than avoiding trimming. Measured cost of the split today, from
  the installed v0.85.1: `twig` 18 MB AOT, `twig-mcp` 22 MB AOT, **`twig-tui` 79 MB**
  non-AOT self-contained — the TUI alone is 4.4× the CLI and the largest artifact twig
  ships. **#359 is GREEN — the gate is cleared and 1002 is unblocked** (owner-run on
  Windows 11, 2026-08-06, via
  [docs/handoffs/windows-native-tui-check.md](../docs/handoffs/windows-native-tui-check.md)):
  native publish succeeded at **19.2 MB** against the 79 MB shipped artifact, and the
  binary **drew the full TUI in a real Windows console** — both panes, all field labels,
  rounded borders, em-dash intact — took input, and exited 0. The pre-`Main`
  `Theme is not a ConfigProperty` cctor crash did not occur, so the one genuine unknown
  going in — Windows' direct console-API calls under native compilation — is answered.
  Four non-fatal `IL3051` warnings on `ScopeJsonConverter<T>.Read`. The red fallback (keep
  the split, rewrite the csproj comment to the TRUE reason) is therefore **not taken**.
  Note for whoever builds 1002: `spike/tui-aot` is **evidence, NOT for merge** — it
  suppresses trim warnings rather than fixing them.

- **MCP is OUT of 1.0** — and the decision is independent, not a demotion. The MCP
  experience gets its own wayfinder map: where it fits in twig's design, its principles,
  what it should expose. 1.0 does not wait on that map. If it resolves early, adding MCP to
  1.0 is a deliberate scope amendment. **Consequences carried honestly:** (a) 1.0 ships two
  of the three surfaces the architecture map's vocabulary is built around, and this map says
  so plainly rather than letting the inconsistency sit; (b) `twig-mcp` therefore REMAINS a
  companion binary, so `CompanionFirstRunCheck` and its 60-second startup network budget
  survive 1.0 — the failure class behind the 0011 startup regression and, per its own
  comment at `CompanionFirstRunCheck.cs:34-47`, the #311 vstest abort class is NOT retired
  by the TUI fold. An earlier draft of this decision claimed it was; that was wrong.

- **MCP freeze lifts** — via 0012's own defined hook, not an override. 0012 froze the MCP
  surface with the freeze lifting on *"a demonstrated script-CLI gap or at the 1.0 map"*;
  chartering this map is the second clause. Consequence: the architecture map's 0009 (MCP
  hints contract) **unparks**. It belongs to the MCP map, not this one. Whether the surface
  grows past 41 tools or is reworked at flat/falling count is an MCP-map ticket — 0012's
  external evidence (GitHub 25→7, Sentry advertising 9 of 48, Notion sunsetting its
  generated proxy, measured selection degradation past 30–50 tools) is an INPUT to that
  decision and did not expire with the freeze.

- **Supported platforms: Windows x64, Linux x64, macOS Apple Silicon. Intel Mac is a
  stated non-target for 1.0, not a gap.** osx-x64 fails at NativeAOT link inside a prebuilt
  Microsoft static lib under Xcode 16.4 on the .NET 11 preview toolchain — no twig code in
  the path, osx-arm64 links clean from the same commit, and the matrix leg is commented out
  in `.github/workflows/release.yml:80-87` with that reasoning inline. `install.sh` already
  detects Intel Mac and explains rather than 404ing. Waiting would hand the release date to
  a third party's bug. The revert path stays documented in
  `docs/architecture/build-and-release.md` for post-1.0.

- **Release-process integrity: the one-liner is a 1.0 blocker; the CI matrix is not.**
  Verified rather than taken from #357: `.github/workflows/release.yml:249` declares the
  `nuget` job `needs: verify-ci`, NOT `needs: build` — so packages publish in parallel with
  the platform builds. That is exactly how v0.85.0 burned: packages pushed, two build legs
  failed, and NuGet versions cannot be re-pushed. Shipping 1.0 through that pipeline is
  shipping a known way to destroy the string `1.0.0` permanently, with no recovery. One
  line retires it. The other half of #357 — Linux-only CI hiding Windows/macOS breakage
  until release day — is a real gap whose cost is delay and rework rather than permanent
  loss, and it levies a standing CI-minutes tax on every PR forever. Post-1.0, its own
  decision.

- **The TUI's editor is server-driven** (owner, 2026-08-01, ticket 1003). ADO exposes the
  work item form layout per type — tabs, groups, ordered fields — and the 1.0 editor takes
  its structure from there rather than from a hand-written layout. **In 1.0, not deferred.**
  Two unverified caveats carried in 1003: whether stock (non-inherited) processes return a
  layout at all, and that structure transfers while widgets do not — mapping control kinds
  to terminal presentation is still hand-written work.

- **The TUI is not a place you go — it is what a command does when it is interactive**
  (owner, 2026-08-01, ticket 1003). Many entry points; `edit` becoming interactive by
  default is the owner's example. Consequence for the fold: this removes the *product*
  argument for keeping `twig-tui` a separate binary. It does not decide the packaging
  question — still gated on #359 — but if the split survives it survives as a packaging
  compromise, not a design.

## Not yet specified
- **What a 1.0 TUI session IS.** 1003 banked the interactions (looking, setting field
  values, rapid tree navigation, viewing/navigating query results) and the many-entry-points
  reframe, but **session vs one-shot is unanswered**: once a command opens interactively,
  do you stay until you quit, or finish one item and return to the shell? That is the
  largest remaining cost driver on the TUI — most of the named interactions do not survive
  a surface that exits after one item. Also unprobed: what "multiple modes and views" means,
  whether the TUI is the reconciliation cockpit, whether Bench management is a TUI job, and
  what the TUI is NOT.
- **What a Bench IS, concretely enough to build.** Ticket
  [1006](tickets/1006-what-is-a-bench.md). The noun is agreed (`CONTEXT.md` §4), it does
  NOT scope the sync boundary (0004 §2), and it lives in the durable `pending.db` (0005) —
  but four questions are open across two maps and are entangled: pending set stored
  per-Bench or per-Connection; whether `WorkingSet` survives as a Bench's derived
  projection; whether benches are concurrent in one process or merely switchable; and
  whose job Bench management is. Answered together or not at all — per-Bench storage plus
  concurrent benches is a different data model from per-Connection plus an ambient
  switchable selection. **Note `CONTEXT.md` §4 is stale**: it still lists the sync-boundary
  question as open when 0004 closed it. Likely a 1.0 blocker, since Bench is user-facing
  vocabulary twig has committed to.
  **RESOLVED 2026-08-06 — superseded by `wayfinder/tickets/0022-bench-and-context.md`.**
  Three of the four are answered: `WorkingSet` **is** a Bench (one hard-coded query plus hand
  pins and exclusions, with nowhere to persist the hand edits — promoted, not replaced);
  **benches are switchable and Contexts are concurrent**; and the stale `CONTEXT.md` §4 line
  is corrected. Still open and carried forward: whether the pending set is **stored** per-Bench
  or per-Connection (only the reconciliation boundary is settled), and **whose job Bench
  management is** — 1006 is the only place that question is written down. What 1006 could not
  see: the Bench was never the blocker. The active work item is ONE ROW in a shared store,
  touched at 47 sites across 28 files, so the real unit of concurrency had no name; 0022
  introduces **Context** (disposable, per-caller, opened and closed by its caller), which
  dissolves two of the four rather than answering them on their own terms. 1.0 relevance
  narrows to 0022 **stage 1** (kill the shared slot — a correctness fix); stage 2 (Bench
  create/name/switch/list) can follow, because `WorkingSet` keeps working throughout.
  Addressing — how a caller names its Context — is chartered separately as
  `wayfinder/tickets/0023-context-addressing.md`.
- **BUILD IT: [1007](tickets/1007-build-the-bench.md).** Bench goes FIRST, ahead of 0022's
  Context stage (owner, 2026-08-06), and the evidence supports the reversal rather than
  merely permitting it: **`tracked_items` and `excluded_items` are in the DROPPABLE mirror**
  (`SqliteCacheStore.cs:420`, in `DropAllTables`). Those are the user's hand pins and hand
  exclusions — the one part of the working set ADO **cannot** rebuild — so a `SchemaVersion`
  bump destroys them silently. That is #271's class wearing a quieter coat, and by 0005 §3a's
  own "can ADO rebuild it?" test both tables were always misfiled. Moving them is 0013
  finishing its own sentence. So the Bench is a **data-loss fix that happens to be the
  feature**: it needs no Context work to land, and `WorkingSet` behaves identically
  throughout. Acceptance bar: with one Bench and no user action, twig behaves exactly as it
  does today. 🔴 Unlike 0013, a clean break is NOT available — pins are silent, so losing them
  prompts nobody; the migration must be written or the ticket blocks.
- **Whether twig needs a server, and what a notification would be.** Ticket
  [1005](tickets/1005-does-twig-need-a-server.md), raised off the #359 run: a leftover
  `twig-tui.exe` held the SQLite files open. That symptom is NOT evidence for a server —
  it was Windows refusing to delete an open handle, and `SqliteCacheStore.cs:113-117`
  already sets `journal_mode=WAL` + `busy_timeout=5000`, which is exactly the
  background-agent-reads-while-TUI-is-open case. The real gap it surfaced is that twig
  has no way to tell anyone something changed. Constrained hard by the architecture map's
  0001 §7: **there is no self-servable event source** — personal subscriptions filter by
  field, service hooks need admin, polling is structural. So a server could only
  centralise polling and fan out locally, never subscribe upstream. **Not a 1.0 blocker**
  unless the TUI turns out to need it.
- How much of the ~110 doc-rot corrections "docs are true" actually demands, and whether
  the bar is per-file accuracy or a narrower "nothing user-facing is false".
- Whether the architecture map's decided-but-unbuilt rulings (capability-seam collapse,
  reconciliation module, three-argument `Resolve`) all land before 1.0 or only the subset
  1.0's correctness bar requires. Sharp enough to be near-ticketable; held because the
  answer depends on what the TUI work costs.
- Migration/sequencing: whether the seam collapse lands before or after the TUI fold, and
  how the intermediate state stays shippable.
- What twig's public contract is for external consumers at 1.0, and what a breaking change
  means for it — inherited unresolved from the architecture map, and 1.0 is where a version
  number starts making promises.

## Out of scope
- **MCP** — see Decisions. Its own map, and out of 1.0 by decision rather than by neglect.
- **Cross-platform CI matrix** (the second half of #357). Real gap, deferred by decision.
- **Intel Mac** (osx-x64). Stated non-target; revert path documented for post-1.0.
- **`CompilerPolyfill.cs` retirement** (#333) — gated on dropping `net10.0`, a toolchain
  watch item with no 1.0 consequence.
