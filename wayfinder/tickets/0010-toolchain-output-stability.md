---
id: 0010
title: Toolchain output stability
type: grilling
status: closed
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

**`jsonc` is a mistake, not a format and not a flag. Delete the advertisement. Then pin the
one thing that is actually a contract: the accept-list — not the payload.**

### First, the count. 79 and 80 are both right, and neither is what #281 implies.

`grep -o` counts 80 occurrences of `jsonc` across `src/**/*.cs`; `grep -n` counts **79 lines**
(`BatchCommand.cs:260` contains it twice). #281's "79" is a line count. But the unit that
matters is neither. Broken down by what each line *is*:

| Kind | Count | Where |
|---|---|---|
| **User-visible advertisement** (`-o` help text) | **70** | `Program.cs` — 70 identical `/// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>` lines |
| Internal param doc on a non-entrypoint | 2 | `BatchCommand.cs:67`, `PatchCommand.cs:52` |
| Prose code comments | 5 | `BatchCommand.cs:39,260,303`; `WorkspaceCommand.cs:28,651` |
| **Actual dispatch** | **2** | `NoteCommand.cs:140,159` |

**The real number is 70 — and it is 70 out of 70.** Every single `-o` doc comment in
`Program.cs` advertises `jsonc` (`grep -c 'param name="output"' src/Twig/Program.cs` = 70;
of those, 70 mention `jsonc`). The advertisement is not partial drift. It is total, and it is
one copy-pasted string.

**And the "one implementation" is not one either.** `NoteCommand.cs:140,159` do not *handle*
`jsonc` — they list it inside an or-pattern that already contains `json`, and route it to the
same `BuildNoteAddedRecord` / `BuildNoteCancelledRecord`. There is no branch anywhere in the
repo where `jsonc` produces output that `json` would not. **`jsonc` has never had an
implementation. It has an alias, in one file, to the format it was supposed to be an
alternative to.**

### It is not a gap, because the format it was meant to be already exists and is already a no-op.

`jsonc` reads as an abbreviation of `json-compact`. `json-compact` *is* routed —
`RendererFactory.cs:44,68` and `OutputFormatterFactory.cs:27`. But look at what it routes to:

```csharp
"json"         => new JsonRenderer(Console.Out, indented: true),
"json-full"    => new JsonRenderer(Console.Out, indented: true),
"json-compact" => new JsonRenderer(Console.Out, indented: true),
```
— `RendererFactory.cs:44-46`

Three names, one renderer, identical arguments. `RendererFactory`'s own doc comment concedes it
(`:33-38`: "*`JsonRenderer` currently emits indented (pretty) JSON for all JSON aliases*").
So implementing `jsonc` as "compact JSON" would not be filling a gap — **`json-compact`,
the format that name is short for, does not emit compact JSON either.** Building `jsonc`
means first building a thing nobody has asked for through the name that already promises it.

### The code does not merely omit `jsonc` — two files assert opposite falsehoods about it.

- `WorkspaceCommand.cs:28` says "*`json`, `jsonc`, `minimal`, and `ids` output formats now
  project through the seam*" and `:651` repeats it. **False.** That file's own
  `IsMachineFormat` at `:489` is `"json" or "json-full" or "json-compact" or "minimal" or
  "ids"` — `jsonc` is absent, so it takes the human path.
- `BatchCommand.cs:39-40` says the opposite and calls it deliberate: "*to preserve the
  documented "jsonc uses human-readable output" quirk*", echoed at `:260` and `:303`
  ("*jsonc/minimal intentionally use human-readable output*").

Both cannot be true. Neither is: `RendererFactory.GetRenderer` has no `jsonc` case, so every
command including both of these falls through `_ => new SpectreNodeRenderer(...)`
(`RendererFactory.cs:48`). `BatchCommand` did not preserve a designed quirk; it wrote a
rationalisation of the default branch into a doc comment, and `WorkspaceCommand` wrote the
opposite rationalisation into another. **An option that two files describe incompatibly, that
no renderer resolves, and that no test exercises is not a design gap. It is a string that
propagated.** (`grep -rc jsonc tests/` → one file, `SetCommand_SlimTests.cs:18`, and only in a
comment.)

**Resolution: delete `jsonc` from all 77 non-dispatch sites, and delete the two `NoteCommand`
or-pattern arms.** Also correct the eight docs files that advertise it
(`docs/specs/mutation-commands.spec.md` ×6, `docs/specs/context-commands.spec.md` ×3,
`.github/skills/twig-command-dev/SKILL.md` ×5, two plan files) — `.github/skills/…:23`
documents it as "*Compact JSON (json-compact). Reduced schema.*", which is how the string
keeps getting regenerated into new commands.

### Q1 — What is the contract, and who may rely on it?

**Per 0001, twig is single-user and local: there is no third party and no published API.** The
only consumers are the user's own scripts and their agent, both of which run *the binary the
user installed*. That kills the version-skew problem that motivates published wire contracts —
a consumer cannot be pinned to an older twig than the one it invokes.

So the contract is not the payload. **The contract is the invocation: if twig accepts an `-o`
value, the output belongs to the family that value names.** `-o <machine-format>` yields
parseable output on stdout or a non-zero exit. It never yields prose.

That is the guarantee #281 actually caught being broken. `twig show 1234 -o jsonc | jq` does
not fail because a field was renamed; it fails because the CLI **accepted a flag it does not
implement and answered in a different language**, silently, exit 0.

**Field-level shape stays unpinned, deliberately.** No `schemaVersion`, no JSON schema files,
no golden tests over command payloads — the ticket is right that there are zero today, and
zero is the correct number. Adding them would buy stability against a skew that cannot occur,
while making every future field addition a docs-and-fixtures ceremony for an audience of one.
The projector-level pin that exists (`JsonRendererTests.cs`) is the right altitude and should
stay there.

### Q3 — What counts as breaking?

Given no published payload contract, the breaking-change unit is **the accept-list**, not the
field set:

1. **Breaking:** removing or renaming a value `-o` currently accepts.
2. **Breaking:** changing which *family* (human vs machine) an accepted value resolves to.
3. **Breaking, and shipped today:** accepting a value and emitting the other family.
4. **Not breaking:** adding a field to a payload, reordering fields, adding a new `-o` value.

By (1), deleting `jsonc` is nominally breaking — and it is still right, because (3) means
`jsonc` never worked for the purpose anyone would have scripted against. A script doing
`-o jsonc | jq` is already failing. A script doing `-o jsonc` and parsing prose is relying on
the fallback, not on `jsonc`, and `-o human` says that honestly.

### The one pin worth paying for: reject unknown `-o` values.

Both factories end in a catch-all that silently means "human":

```csharp
_ => new SpectreNodeRenderer(CreateAnsiConsole(Console.Out)),   // RendererFactory.cs:48
_ => human,                                                     // OutputFormatterFactory.cs:30
```

**That catch-all is the actual defect.** `jsonc` is one instance; so is `-o jsno`, `-o JSON5`,
and every future typo. It converts a user error into malformed output with exit 0 — the worst
failure mode available to a tool whose output is piped.

Validate `-o` once at the entrypoint against a **single accept-list**, and exit non-zero with
the accepted values on a miss. This is the smallest possible pin: **one list and one test**,
not 56 golden files or a schema per command. It is also what makes deleting `jsonc` safe —
the user finds out at the prompt instead of downstream in `jq`.

It also subsumes the drift the ticket inventories. The 33 copy-pasted `IsMachineFormat`
predicates have already diverged three ways — `RefreshCommand.cs:72` omits `minimal`;
`ShowCommand.cs:716` omits `json-full` and `jsonc` (its own doc comment at `:707-712` lists
only four); `WorkspaceCommand.cs:489,492,495` holds three different variants in one file — but
**collapsing those 33 sites is 0002's capability-seam work, not a stability regime.** What 0010
owns is that they must all read from one accept-list, so no command can accept a value the
list does not contain.

### Two corrections to this ticket's own framing.

- **"`PlainOutputFormatter` fails the deletion test — absorb."** It does not.
  `PlainOutputFormatter.cs:29` is the ANSI-stripping wrapper the machine formats get from
  `OutputFormatterFactory.cs:20,25-29`, and per that factory's doc comment (`:9-14`) it exists
  because Linux CI sets `TERM=xterm-256color`, so without it incidental stderr messages carry
  escape codes into `jq` pipelines. Deleting it re-breaks the surface this ticket is about. It
  is load-bearing.
- **`docs/architecture/commands.md:120-128`** — confirmed stale, as the ticket says. Its
  formatter table names `JsonOutputFormatter`, `JsonCompactOutputFormatter` and
  `MinimalOutputFormatter`, all retired per `IOutputFormatter.cs:3-11`. The rows are wrong,
  not just the class names: `json-full` no longer resolves to a distinct formatter.

### Scope held

No MCP work (0012 freeze). No new module, no `schemaVersion`, no golden-test regime. The
follow-on task is one deletion sweep plus one validation site — implementation, not design.

