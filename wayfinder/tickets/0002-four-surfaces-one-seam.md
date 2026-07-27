---
id: 0002
title: Four surfaces, one seam?
type: grilling
status: closed
blocked_by: [0001]
---

## Question

Are human (CLI), AI (MCP), toolchain (JSON/text), and TUI four adapters at ONE seam, or genuinely different products? Evidence for one seam: the CLI already has `Twig.RenderTree` with `IRenderer`, 4 adapters, and a `RenderAudience` concept — but neither `Twig.Mcp.csproj` nor `Twig.Tui.csproj` references it, so both hand-rolled their own output stacks. Four adapters would make this emphatically a real seam rather than a hypothetical one. What differs per surface (hints? interpretation? truncation? stability guarantees? error shape?) and what is genuinely common? Decide the seam's location and its interface before any code moves.

## The four experiences (owner, 2026-07-26)

Definitions given by the owner. These supersede the loose "human/AI/toolchain/TUI"
shorthand used in the audit, which conflated *audience* with *interaction model*.

| # | Experience | Consumer | Wants |
|---|---|---|---|
| 1 | **Rich CLI** | a human at a terminal | rendered text: colour, tables, truncation, hints, interpretation |
| 2 | **Script CLI** | a script, pipe, or CI job | machine-readable **stdio AND fileio** — a stable, parseable, boring contract |
| 3 | **MCP** | an LLM | control the bench / pending set, and **answer questions about local OR remote data** |
| 4 | **TUI** | a human wanting a session | **rich UI sessions launched from the CLI**, with multiple modes and views |

Three things this framing settles or changes:

**a. The TUI is CONCEPTUALLY a CLI thing, but may still be its own product.** Owner,
2026-07-26: *"I think of the TUI as a CLI concept. It can be its own product though."*

This is a **conceptual** placement, not a packaging decision, and the two must not be
conflated (an earlier draft of this ticket did conflate them — corrected). The TUI belongs
to the CLI's world: same user, same terminal, same mental model, launched the same way.
Whether it ships as one binary or two, and whether it keeps its own composition root,
is **undecided and still in scope for this ticket and 0007**.

What that does settle: the TUI is not a *different product for a different audience* the
way MCP is. Experiences 1 and 4 serve the same person. What it does NOT settle: whether
`src/Twig.Tui`'s separate composition root and its own output stack
(`Twig.Tui.csproj` does not reference `Twig.RenderTree`) are justified. That duplication
may be right if the TUI is a separately shipped product, and wrong if it is a mode of one
binary. Both remain open.

**b. Experience 3 has a capability the other three lack: REACH.** *"answer questions about
the local OR remote data."* The other three surfaces read what twig has cached. MCP may be
asked about data twig has never seen, and must decide whether to fetch. That is a
**capability** difference, not a rendering one — and it interacts directly with 0001 §5
(the sync boundary must be explicit and user-owned): if an LLM can trigger a fetch by
asking a question, who owns *that* boundary?

**c. Experience 2 includes FILE output, not just stdout.** *"machine readable stdio and
fileio."* The audit treated the toolchain surface as `--json` on stdout. Files written for
another program to consume are equally a contract, and nothing currently versions or
tests either. Widens 0010 (toolchain output stability).

**d. MCP is an LLM TOOLKIT, not a CLI proxy.** Owner, 2026-07-26:

> *"script these twig things using the scripting interface, or use more high level tools
> targeted at things the MCP might reasonably want to do, and then drive underlying twig
> operations accordingly. I see the MCP as a bit more of an LLM toolkit than just a CLI
> proxy."*

Two offered shapes, and **both already exist in the code as one-offs** — which is the real
finding here:

- **The scripting interface.** `twig_batch` (`src/Twig.Mcp/Tools/BatchTools.cs:17`) already
  accepts a JSON graph of `sequence` / `parallel` / `step` nodes, with fail-fast semantics
  and per-step `onError: continue`. That IS a scripting primitive exposed as a tool.
- **High-level intent tools.** `twig_find_or_create`
  (`src/Twig.Mcp/Tools/CreationTools.cs:120`) is not a CLI proxy: its description says
  *"Always performs a deduplication check — use this instead of twig_new when idempotent
  creation is required."* It encodes an INTENT the LLM would otherwise have to compose
  from a query plus a create, and get wrong.

The other ~39 tools are per-command proxies. So the catalogue currently holds **two
philosophies with no stated intent** — a design decision made twice by accident.

**The mechanism for selective exposure also already exists**, and is not the issue: 41
tools in `AllToolNames`, only 11 advertised by default via `CompactToolNames`
(`McpToolCatalog.cs:70`), the rest reachable with `--tool-profile full`. What is missing is
the *criterion*. The code comment calls the 11 the "high-frequency surface" — chosen by
guessing what an LLM uses often. The owner's rule is different: a tool earns its place
because a real scenario needs it.

**Consequence — "CLI ↔ MCP parity" is partly the WRONG QUESTION.** Parity assumes MCP
should mirror the CLI. If MCP is a toolkit, then some CLI commands should have no MCP tool,
and some MCP tools (composites like `find_or_create`, and `twig_batch` itself) should have
no CLI equivalent. **Divergence would be correct, not drift.** The audit's parity table
needs re-reading with that lens before it is used to justify any alignment work.

Supporting evidence that MCP-side capability has drifted from use:
`McpHintProvider.ApplyHintsAsync` has zero production callers.

**Open:** what are the scenarios? The toolkit cannot be designed until "things an LLM
might reasonably want to do" is an enumerated list rather than a phrase. That list is the
gating artifact for this half of the ticket.

**Sharpest axis so far — interactivity (from 0001 §3d).** 1 and 4 can be *asked a
question*; 2 cannot; 3 can, but only through the LLM as intermediary. Conflict resolution
must therefore branch on this, not on output format. This may be a better seam boundary
than rendering.

**Open:** are 1 and 4 the same surface at different session lengths (both rendered, both
interactive, differing only in whether state persists across commands)? If so there may be
**three** surfaces and two *presentation modes*, not four surfaces.

### The MCP freeze shrinks this ticket (0012, 2026-07-26)

MCP is **frozen** — no new tools, no parity work, script CLI first (see 0012). The seam
question therefore narrows: with one of the four experiences under a build-freeze, the live
question is **rich CLI vs script CLI vs TUI**, and the owner has already placed rich CLI and
TUI as serving the same person. MCP remains a consumer of whatever seam exists; it just
stops being a moving target while the seam is decided.

This does NOT make the ticket trivial — experience 2's contract (stdio *and* fileio, §c)
is still unversioned and untested, and that is where the investment is going.

### Who decides the seam question

The owner has **no strong feeling** on one-seam-versus-many (2026-07-26) and is not
withholding a preference. **Do not wait on him for it** — this is an engineering judgement
to be made from evidence: the deletion test, the number of real adapters, and whether the
per-surface differences (§a–d) are genuinely differences of *shaping* or of *capability*.

What he HAS given, and what the answer must respect:

- Experiences 1 and 4 serve the same person (§a).
- MCP is a selectively exposed projection of capability that exists elsewhere (§d) — which
  is itself an argument for a seam, since a projection needs a source.
- Experience 2's contract includes files, not just stdout (§c).
- Interactivity is the sharpest observed axis, not output format.

The remaining decision is therefore: **where the seam sits and what its interface is**, not
whether he wants one.

## Answer

**Not four adapters at one render seam. Three surfaces at one CAPABILITY seam, each
owning its own presentation.** The seam is the **workflow layer**, it already exists and
is already load-bearing on the mutation half, and the work is to *finish* it — extend it
to reads — not to build a new one.

`Twig.RenderTree` is scoped down honestly to **the CLI's format layer**. It is not
promoted, and MCP/TUI are not migrated onto it.

### 1. The ticket's central evidence was misread — RenderTree has ONE adapter, not four

The question assumed `Twig.RenderTree`'s four adapters make the surface seam
"emphatically real." Verified against live code (2026-07-27), they do not. The four
adapters are `SpectreNodeRenderer` / `JsonRenderer` / `MinimalRenderer` / `IdsRenderer` —
four values of **`--format` on one surface**, dispatched by
`RendererFactory.cs:44-49`. At the *four-experiences* seam RenderTree has exactly **one**
adapter: the CLI.

Per this map's own rule — one adapter = hypothetical seam, two = real — RenderTree is a
**hypothetical** surface seam. The absence of a `Twig.RenderTree` reference from
`Twig.Mcp.csproj` and `Twig.Tui.csproj` is not the defect the audit read it as.

It also could not become that seam, because **`RenderTree` is presentational, not
semantic**:

- `RenderNode.Markup` (`RenderNode.cs:46`) carries literal Spectre.Console markup strings.
- `Section` is documented as "a visual grouping container"; `Hint` as "typically dim."

Migrating MCP onto it means an LLM consuming Spectre markup. The CLI already escapes its
own tree for exactly this reason: `SpectreRenderer` has 8 bespoke `Render*Async` methods
(~1,700 lines) that 5 commands bypass RenderTree entirely to reach — e.g.
`SeedViewCommand.cs:44-53` branches to `renderer.RenderSeedViewAsync` on the TTY path and
only falls through to the tree otherwise. A seam its sole adapter routes around is not a
seam that wants more adapters.

**Correction to the map:** "Only the CLI references Twig.RenderTree" is true but is not
evidence of drift. It is correct layering.

### 2. The real seam already exists, already has two adapters, and is already deep

All three composition roots reference exactly `Twig.Domain` + `Twig.Infrastructure` and
nothing else. They do not share an output stack because they **should not**. What they
share is capability — and on the mutation half that sharing is already formalised.

Six workflows in `Twig.Infrastructure/Services/Mutation/` — `StateTransitionWorkflow`,
`FieldUpdateWorkflow`, `NoteWorkflow`, `DiscardWorkflow`, `DeleteWorkflow`,
`PatchWorkflow` — each have **exactly two consumers**: one CLI command and MCP's
`MutationTools`. Two adapters at one seam. **Real by the rule, and nobody wrote it down.**

The interface is deep (`StateTransitionWorkflow.cs:90,116`):

```csharp
StateTransitionOutcome? Validate(WorkItem item, string stateName);
Task<StateTransitionOutcome> ExecuteAsync(WorkItem remote, string stateName, int revision, CancellationToken ct);
```

Two methods; the return is a **union the caller pattern-matches**. `StateCommand.cs:117-190`
renders eight outcome cases as human text; `MutationTools.cs:65-74` renders the same
outcomes into a JSON envelope. One capability, shaped twice, deliberately.

### 3. The seam's interface handles INTERACTIVITY, which is the sharpest axis

The two-phase split is not ceremony — it is the seam doing the job the ticket identified.

- `StateCommand.cs:68-82` — `Validate`, then **interactive conflict resolution at line 79**,
  then `ExecuteAsync`.
- `MutationTools.cs:65-74` — `Validate`, no interjection, `ExecuteAsync`.

**The interactive surface interjects between the phases; the non-interactive one does not.**
So the seam's interface is not "render this," it is:

> `Validate` → *(surface interjects if it can)* → `Execute` → **match the outcome union**

That is 0001's "interactive conflict resolution for humans, warn-and-advise for
agents/scripts" already expressed structurally rather than as prose. Interactivity, not
output format, is what varies across this seam — as 0001 §3d predicted.

### 4. Reads never got the treatment — and `WorkspaceContext` is the symptom

Nothing equivalent exists for reads. `SeedViewCommand` injects six dependencies and
composes the use-case inline; MCP's `ReadTools` composes the same work again through
`ctx.IterationService` / `ctx.AdoService`. Same capability, assembled twice, in two places.

The tell is `WorkspaceContext` (`src/Twig.Mcp/Services/WorkspaceContext.cs`): **34 public
properties** re-exporting the container — repositories, stores, resolvers, services, and
the six workflows. It is a god object that exists precisely because there is no read-side
workflow for MCP to depend on instead. It is not a design; it is the negative space where
the read seam should be.

**Read workflows take ONE method, not the mutation two-phase shape.** Reads have no write
to validate ahead of; a `Validate` on a read would be symmetry for its own sake. One
`ExecuteAsync` returning an outcome union.

### 5. REACH is an OUTCOME, not a policy — which resolves the 0001 §5 tension

§b asked: if an LLM triggers a fetch by asking about uncached data, who owns that
boundary? The single-method read shape answers it.

**A read workflow never decides to fetch.** Encountering data twig has not cached is a
**case of the outcome union** — `NotCached(id)` — returned to the caller. Each surface
then decides:

| Surface | Response to `NotCached` |
|---|---|
| Rich CLI | render the miss, advise `twig sync` |
| Script CLI | stable machine-readable miss; exit code, no implicit network |
| TUI | prompt the user — it can ask |
| MCP | return a hint; the LLM must ask the user to sync |

This puts the sync boundary back **in the surface, where 0001 §5 says the user owns it**,
instead of burying a fetch policy inside shared code where no surface can see it. It
resolves the map's open item *"Who owns the sync boundary when an LLM triggers a fetch?"*
— **nobody in the seam does; the seam reports, the surface decides.**

It also keeps MCP inside the 0012 freeze: no new tools, no parity work — existing tools
consume a seam that gets deeper beneath them.

### 6. How many surfaces, then

**Three surfaces, four experiences.** Rich CLI and Script CLI are one surface — one binary,
one composition root, one command set — with `--format` selecting presentation
(`RendererFactory`). That is a **presentation mode**, not a surface. §a's open question
resolves the same way from the other side: the TUI is a distinct surface because it has a
distinct *interaction model* (a persistent interjecting session), not because it serves a
different person.

Surfaces are counted by **interaction model**, not audience or output format:

| Surface | Interaction model | Experiences served |
|---|---|---|
| CLI | one-shot; may interject on a TTY | 1 (rich) + 2 (script) |
| TUI | persistent session; always interjects | 4 |
| MCP | one-shot; never interjects directly | 3 |

Experience 2's fileio contract (§c) is a **CLI output-target** concern and belongs to
0010, not to this seam. It is unaffected by this answer.

### 7. What this decides for the blocked tickets

- **0007 (single composition root):** three composition roots are **correct** — they are
  the three surfaces. The question narrows to whether the TUI's *packaging* follows, which
  is a shipping decision, not an architectural one. The duplication worth removing is
  `WorkspaceContext`, not the roots.
- **0009 (MCP hints contract):** hints are **surface-owned shaping of outcome unions**, not
  a seam concern. That `McpHintProvider.ApplyHintsAsync` has zero production callers is
  consistent with hints having no home yet — 0009 should give them one on the MCP surface.
- **0010 (toolchain output stability):** scoped to the CLI's format layer (RenderTree +
  fileio). It does **not** need to cover MCP, whose envelope is separate and stays separate.

### 8. Scope discipline

This is a decision, not a refactor. **No code moved.** The implied work — extending the
workflow seam to reads and collapsing `WorkspaceContext` — is deepening that must be done
**only where two surfaces already compose the same use-case**, never speculatively. That
keeps the one-adapter-vs-two rule honest at the new seam too, and is the failure mode to
watch: a read workflow with one consumer is a pass-through that has not earned its keep.
