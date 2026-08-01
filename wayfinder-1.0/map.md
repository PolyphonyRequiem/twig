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
  ships. **Gated on #359** (Windows AOT verification, owner-run: cross-OS native
  compilation is not supported, so no Linux box can answer it). Fallback if #359 is red:
  keep the split, and rewrite the csproj comment to the TRUE reason — deliberate risk
  isolation — rather than leaving a disproven one in the tree.

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

## Not yet specified
- **What the TUI actually does.** The scope call is banked (committed to 1.0, needs "a lot
  of real work", its ~774 lines across 3 files are a starting point not a deliverable) but
  no one has said what a finished 1.0 TUI *is*. This is the largest unspecified area on the
  map and almost certainly graduates into several tickets — design, UX mockup, execute —
  once someone can phrase the first question sharply. Deliberately not pre-sliced.
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
