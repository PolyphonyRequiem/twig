---
id: 0019
title: One accept-list for output formats
type: task
status: closed
blocked_by: [0002]
---

## Question

Implement the one pin 0010 ruled worth paying for: **`-o` is validated once, against a single
accept-list, and an unknown value exits non-zero.**

0010 answered the stability question by *removing* a contract rather than adding one — field
shape stays deliberately unpinned (no `schemaVersion`, no JSON schemas, no golden tests over
payloads), because 0001 makes twig single-user and local, so a consumer cannot be pinned to an
older twig than the one it invokes. What 0010 did NOT dismiss is the accept-list. This ticket
owns it.

## Why this exists

Both format factories end in a catch-all that silently means "human":

```csharp
_ => new SpectreNodeRenderer(CreateAnsiConsole(Console.Out)),   // RendererFactory.cs:48
_ => human,                                                     // OutputFormatterFactory.cs:30
```

That catch-all converts a user typo into **malformed output with exit 0** — the worst failure
mode available to a tool whose output is piped. `-o jsno`, `-o JSON5`, `-o json5` all produce
ANSI prose that `jq` chokes on, and the CLI reports success.

`jsonc` was one instance of this class. #281 deleted the advertisement (PR #300) and 0010 ruled
it a mistake rather than a gap — but **deleting `jsonc` did not fix the door it came through.**
The next typo, and the next command that invents a format name in a doc comment, land in exactly
the same place.

## Scope

1. **One accept-list, in one place.** The set of values `-o` accepts, as a single literal.
   Current membership from `RendererFactory.cs:43-48`: `human`, `json`, `json-full`,
   `json-compact`, `minimal`, `ids`.
2. **Validate once at the entrypoint.** Unknown value → non-zero exit, with the accepted values
   in the message. Not per-command.
3. **One test.** That the accept-list and the renderer switch cannot diverge — i.e. every
   accepted value resolves to a renderer, and no renderer case is unreachable from the list.
4. **The 33 sites must READ the list, not restate it.** This is the part that makes the pin
   real rather than decorative (see below).

## Why `blocked_by: [0002]`, and the escape hatch

**The pin is not independent of the seam work.** Validation at the door does not fix drift in
the room. The machine-format predicate is copy-pasted across **33 command files** in
`src/Twig/` with **5 distinct variants** (verified at `ef5be4d3`):

```
is "json" or "json-full" or "json-compact" or "minimal" or "ids"
is "json" or "json-full" or "json-compact" or "ids"
is "json" or "json-compact" or "minimal" or "ids"
is "json" or "json-full"
```

Named instances: `RefreshCommand.cs:72` omits `minimal`; `ShowCommand.cs:716` omits `json-full`
(and its own doc comment at `:707-712` lists only four); `WorkspaceCommand.cs:489,492,495`
carries three different variants **in one file**.

So if validation ships while 33 sites keep private opinions, `-o json-full` passes the gate and
is then dropped by `RefreshCommand`. **That is arguably worse than today** — a validation gate
implies a guarantee it does not deliver. Collapsing those 33 predicates is 0002's capability-seam
work, and the accept-list is the artifact that collapse produces. Ticketing it fully independent
risks two sessions building two lists.

**Escape hatch, and it is deliberate:** if 0002 stalls, a **narrow entrypoint-only** version may
be pulled forward — validate at the door, accept-list as a literal in one file, explicitly marked
as the seed of 0002's collapse. That buys the exit-0 safety now and hands 0002 a starting point
instead of a conflict. A session taking this path must say so in the PR and must NOT touch the
33 sites.

## Constraints inherited

- **Do NOT pin payload shape.** 0010 ruled zero schemas / zero golden tests is the CORRECT
  number, not a defect. The projector-level pin (`JsonRendererTests.cs`) is the right altitude
  and stays there.
- **Do NOT implement `jsonc`.** Ruled a mistake by 0010, deleted by #281 / PR #300.
- **`json-compact` is also a no-op** — `RendererFactory.cs:44-46` maps `json`, `json-full` and
  `json-compact` to the same `JsonRenderer(indented: true)`, as its own doc comment concedes.
  Whether to collapse those three names is a real question this ticket may answer, but it is
  **not** required to; the accept-list works either way.
- **Do NOT touch `PlainOutputFormatter`.** 0010 corrected the claim that it fails the deletion
  test: it is the ANSI stripper keeping escape codes out of `jq` pipelines under
  `TERM=xterm-256color` (`OutputFormatterFactory.cs:9-14`). Load-bearing.
- **MCP is FROZEN (0012)** — no new tools, no CLI↔MCP parity work.

## Breaking-change rules (from 0010, for whoever implements)

1. Breaking: removing or renaming a value `-o` currently accepts.
2. Breaking: changing which family (human vs machine) an accepted value resolves to.
3. Breaking, and shipped until #281: accepting a value and emitting the other family.
4. Not breaking: adding a payload field, reordering fields, adding a new `-o` value.

Rule 4 is why this pin is cheap: it does not make future field additions a docs-and-fixtures
ceremony for an audience of one.

## Answer

**Built: the narrow entrypoint-only version the ticket sanctions as its escape hatch.**
0002 has not landed, so this ships validation at the door and explicitly does NOT touch the
33 copy-pasted machine-format predicates. It is the seed of 0002's collapse, not the collapse.

**One accept-list, one place.** `src/Twig/Formatters/OutputFormats.cs` is the single literal:
`human`, `json`, `json-full`, `json-compact`, `minimal`, `ids` — membership unchanged, so no
breaking-change rule from 0010 is tripped. It exposes `Normalize()` (canonical lower-case, or
`null` when off-list), `IsAccepted()`, `Describe()` (comma list for messages/help), and
`Default`.

**Validated once, at the entrypoint.** `OutputFormatArgumentValidator.Validate(args)` is called
in `Program.cs` immediately after the unknown-command interception and BEFORE `app.Run(args)`.
An unknown value writes `Unknown output format 'X'. Valid formats: human, json, json-full,
json-compact, minimal, ids.` to stderr and exits **2** (`UsageExitCode`, matching the existing
command-level usage-error convention). It handles `-o V`, `--output V`, `-o=V`, `--output=V`,
stops scanning at `--`, and leaves a bare trailing `-o` to the argument parser.

**The two factories now READ the list instead of restating it.** `RendererFactory` (both
overloads) and `OutputFormatterFactory` switch over `OutputFormats.Normalize(...)`, and their
`DefaultFormat` constants forward to `OutputFormats.Default`. Their `_ =>` arms remain, but they
are now only reachable by in-process callers — no CLI input can arrive off-list — so the arm no
longer converts a user typo into exit 0. `QueryCommand`'s `--output` help line reads
`OutputFormats.Describe()`.

**Facts later tickets depend on:**
- The accept-list is `Twig.Formatters.OutputFormats.Accepted`. **0002 should collapse the 33
  predicates onto this type, not create a second list.** A `IsMachineFormat`-style predicate
  belongs on `OutputFormats` when 0002 lands.
- Unknown-format exit code is **2**, distinct from the generic error exit **1**.
- Case handling is unchanged: `ToLowerInvariant` semantics, so `-o JSON` still works.
- `json`, `json-full`, `json-compact` still all map to `JsonRenderer(indented: true)`. This
  ticket deliberately did NOT collapse those three names (the ticket said it may, need not).
- Scope held: no `schemaVersion`, no JSON schemas, no golden payload tests — 0010's ruling
  respected. `PlainOutputFormatter` untouched. MCP untouched.

**Tests** — `tests/Twig.Cli.Tests/Formatters/OutputFormatsAcceptListTests.cs`, 31 cases: typos
(`jsno`, `json5`, `JSON5`, `jsonc`, `yaml`, empty, trailing space) rejected in all four flag
spellings; every accepted format passes validation and resolves to a renderer AND a formatter in
the correct family (human vs machine, 0010 rule 2); case-insensitivity pinned; `Describe()`
proven to list exactly the accept-list; list-vs-switch divergence blocked.

**Pre-fix proof:** a probe using only the e899de46 API surface (asserting a typo does not resolve
to `SpectreNodeRenderer`/`HumanOutputFormatter`) was run in a detached worktree at e899de46 and
failed **5/5** — confirming `-o jsno` silently produced human output there. The worktree has been
removed. End-to-end on the built binary: `-o jsno|json5|jsonc` exit **2** with the message; all
six valid formats plus `JSON` pass the gate.

**Suite:** Cli 2914 / Infra 1355 / Mcp 1313 / Domain 1828 = 7,410, exit 0 on all four
(baseline 7,379 + 31 new).

PROPOSED follow-ons (no IDs assigned, per instruction):
- PROPOSED: Collapse the 33 machine-format predicates onto `OutputFormats` (this is 0002's work
  and is the reason this ticket shipped narrow).
- PROPOSED: Decide whether `json-full` / `json-compact` should collapse into `json`, or acquire
  the distinct behaviour their names advertise.

### Review follow-up (2026-07-27)

Review raised one MAJOR: the ticket's central deliverable — "unknown value exits non-zero" — had
**no committed guard**. `OutputFormatArgumentValidator.Validate` has exactly one production call
site (the `Program.cs` guard block), and every unit case called the validator directly, so deleting
that block left all 31 tests green while `-o jsno` silently returned to human output with exit 0.
The pre-fix probe was real and honestly disclosed, but it was thrown away rather than retained.

Closed by `tests/Twig.Cli.Tests/Formatters/OutputFormatEntrypointTests.cs`: 6 cases that run the
built binary and assert exit 2 plus the stderr message. **Non-vacuity proved by removing the guard
block and re-running: 5 of 6 fail.** The 6th is an in-process positive control that correctly does
not depend on the entrypoint.

Two traps hit while writing it, recorded so the next author does not repeat them:

- **`[Trait("Category", "Interactive")]` would have made the guard vacuous.** `AotSmokeTests` carries
  that trait and the default filter EXCLUDES it, so the first version reported exit 0 having run
  **zero tests**. The trait is deliberately absent, and the file says why.
- **The positive control cannot go through the binary.** `show -o json` reaches a real command and
  therefore pays for 0018's startup side effects — a blocking GitHub companion download — which
  exceeded the 300 s test-run budget and aborted the host. Negative cases never reach it because the
  guard returns first. The accepted-format direction is asserted in-process instead.

**Ordering note:** the 0019 guard sits ABOVE 0018's startup side effects in `Program.cs`. A usage
error must exit without paying for a self-update sweep or a companion download. Both tickets' guards
hold: 0018 asserts its side effects run below the fast-exit block, 0019 asserts its validation runs
before them.

Also addressed from review: `src/Twig/Commands/QueryCommand.cs:274` touches one of the 33 files the
escape hatch said not to touch — benign (it removes a restated list), now called out explicitly
rather than left silent.

PROPOSED follow-ons (not filed as numbered tickets, per the ID-namespace rule):

- **PROPOSED: collapse the duplicate `GetRenderer` overloads** — `RendererFactory.cs:47` and `:71`
  carry identical five-arm switches differing only in sink. Judgement-call Duplicated Code.
- **PROPOSED: assert no renderer arm exists outside `Accepted`** — §Scope item 3 asks for it; only
  the forward direction is tested today.
- **PROPOSED: `RefreshCommand.cs:72` still drops `json-full` downstream** — the entrypoint gate does
  not stop it. Residual hazard the ticket itself raised.
