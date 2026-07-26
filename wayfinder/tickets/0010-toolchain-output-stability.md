---
id: 0010
title: Toolchain output stability
type: grilling
status: open
blocked_by: [0002]
---

## Question

What stability guarantee does the toolchain surface offer, and what pins it? Full inventory in `%TEMP%\twig-review\candidates-toolchain.md`.

**Confirmed: it is a parameter, not a module.** Machine-readable output is a `string output` parameter on **50 of 56 command files**. The predicate deciding whether a format is machine-readable — `lower is "json" or "json-full" or "json-compact" or "minimal" or "ids"` — is **copy-pasted across 33 command files** (`DeleteCommand.cs:182`, `NewCommand.cs:215`, `StateCommand.cs:227`, …) and has already drifted: `RefreshCommand.cs:72` omits `minimal`, `ShowCommand.cs:716` omits `json-full` and `jsonc`, `WorkspaceCommand.cs:489,492,495` has three variants in one file.

**Nothing pins the shape. Answered plainly: no.** 0 golden/snapshot tests over command output, 0 JSON schema files, 0 `schemaVersion` fields. `JsonRendererTests.cs` pins the *projector*, not any command's payload — rename a field in `NewCommand.BuildCreatedRecord` and all 326 lines still pass. The ~15 test files using `JsonDocument.Parse` assert incidental fields. The only written contract (`docs/architecture/commands.md:126-128,231-233`) describes three classes deleted per `IOutputFormatter.cs:4-11`.

**A break has already shipped:** issue #281 — `jsonc` is advertised 79 times in `src/` but handled only in `NoteCommand.cs:140,159`. Every other command falls through to `HumanOutputFormatter`, so `twig show -o jsonc | jq` receives human ANSI text. Unknown formats degrade silently to human output instead of erroring.

**The seam cannot currently name this surface.** `RenderAudience.cs:15-24` is `All | HumanOnly | MachineOnly` — it counts to two, not three. The toolchain surface is literally unnameable in the rendering seam's own vocabulary, which is why MCP sits outside it. `PlainOutputFormatter` fails the deletion test — absorb.

Decide: is the toolchain output versioned? What counts as a breaking change? What test or schema pins the shape so a refactor cannot quietly break a consumer's script?

## Answer

<!-- empty until resolved -->
