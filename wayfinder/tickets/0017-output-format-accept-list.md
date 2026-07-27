---
id: 0017
title: One accept-list for output formats
type: task
status: open
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

<!-- empty until resolved -->
