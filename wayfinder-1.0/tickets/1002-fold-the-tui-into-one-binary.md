---
id: 1002
title: Fold the TUI into one binary
type: task
status: open
blocked_by: []
---

## Question

Make the TUI a mode of the main `twig` binary (`twig tui`) rather than a separately
distributed `twig-tui` companion, and AOT it.

**Gated on #359** — Windows NativeAOT verification, which the owner runs on his own
hardware. Cross-OS native compilation is not supported, so no Linux box can answer it. Do
not start the fold before #359 is green; if it comes back red, this ticket's answer is the
fallback instead (see below).

**The verification procedure is written and ready to run:**
[docs/handoffs/windows-native-tui-check.md](../../docs/handoffs/windows-native-tui-check.md).
It is self-contained and copy-pasteable from a Windows machine with no prior context. The
pass bar is the binary **drawing its interface in a real console** — a clean compile is not
a pass, because the naive fix compiles and then crashes before `Main`.

## What is already established

- **The stated blocker is false.** `src/Twig.Tui/Twig.Tui.csproj:6-10` carries two claims:
  `IsAotCompatible=false` because *"Terminal.Gui v2 beta does not support AOT"*, and
  separately *"Terminal.Gui relies on reflection; trimming is intentionally not enabled."*
  The first is disproven; the second is backwards.
- **Disproven by execution, on Linux:** upstream `v2_develop` sets `IsTrimmable=true` and
  `IsAotCompatible=true`, and the spike built a 19 MB native ELF observed rendering the
  full TUI, handling input, and exiting 0. Branch `origin/spike/tui-aot` @ `0a8c185d`,
  writeup `AOT-SPIKE-FINDINGS.md`. **That branch is evidence, NOT for merge** — it
  suppresses trim warnings rather than fixing them.
- **The trap, so it is not re-learned:** the naive fix (suppress IL2026/IL3050) builds
  clean at 12 MB and then **crashes before `Main`** —
  `InvalidOperationException: Theme is not a ConfigProperty`, thrown from
  `ConfigurationManager` in `<Module>..cctor()`. Root cause is **trimming, not native
  codegen**. Fix is `TrimmerRootAssembly(Terminal.Gui)` + `TrimMode=partial`.
  Terminal.Gui's `SourceGenerationContext` is `internal`, so there is no consumer
  registration path — do not go looking for one.
- **Measured cost of the split today** (installed v0.85.1): `twig` 18 MB AOT, `twig-mcp`
  22 MB AOT, **`twig-tui` 79 MB** non-AOT self-contained single-file. The TUI is 4.4× the
  CLI and the largest artifact twig ships; the spike's native build was 19 MB.

## Scope

- `twig tui` becomes a mode of the main binary. `twig-tui` stops being built and published.
- The TUI's own composition root and output stack converge with the CLI's. Per the
  architecture map's 0019, it must land on the single output-format accept-list rather
  than keeping a separate one.
- `.github/workflows/release.yml:123` currently publishes `Twig.Tui.csproj` with
  `/p:PublishTrimmed=false /p:PublishAot=false`. That leg goes away.
- The csproj's two false comments go away with the project, but if any part of this ticket
  stalls, **do not leave a disproven claim in the tree** — replace it with the true reason.

**NOT in scope:** `twig-mcp`. MCP is out of 1.0 by decision and keeps its companion
packaging. `CompanionTools`, `CompanionFirstRunCheck` and `ICompanionInstaller` therefore
SURVIVE this ticket — they lose one of two entries, not their reason to exist. Do not
delete the companion channel here.

## Fallback if #359 is red

Keep the two-binary split, and rewrite `Twig.Tui.csproj:6-10` to state the true reason —
deliberate risk isolation, because Windows AOT is unverified — rather than the disproven
Terminal.Gui claim. Record in the answer that one binary remains the intended destination
and what would reopen it.

## Acceptance

- `twig tui` launches the TUI from the main binary on all three supported platforms.
- The published artifact set no longer contains `twig-tui`.
- The TUI's `-o` handling resolves through the same accept-list as the CLI's.
- Suite green via `tools/run-tests.sh` (`TWIG-VERDICT`, never `Passed!`).

## Answer

<!-- empty until resolved -->
