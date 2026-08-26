# Transport attachment contract — settled design (T1, AB#744)

> Status: settled sub-spec closing AB#744. Downstream tickets AB#745 (core seam), AB#746 (Herdr adapter), and AB#747 (Windows Terminal adapter) implement against the contract here without opening a new capability, lifecycle, or registry decision.

## 1. Scope and non-goals

This document resolves the host-neutral Transport Attachment contract that
Spec AB#730 mandates and that MAP AB#726 delegated to a sub-spec. It fixes:

- the field-by-field shape of a Transport Attachment record and the exact
  set of shape combinations the validator accepts;
- the capability declaration model each adapter exposes to Twig core, and
  the absent-capability degradation for every optional capability;
- the core-neutral status vocabulary, the mapping rule adapters follow,
  and the treatment of Herdr's documented-ambiguous `idle`;
- the bounded probe semantics, the timeout defaults, the named result of a
  timed-out probe, and the freshness rule that governs any reuse of a
  prior observation;
- the semantics of detach, close, and partial close, with unverified host
  behaviour degraded safely rather than assumed;
- the adapter registration surface, the structured target passed through
  dispatch, the core's selection rule, and the null-adapter path that lets
  the seam be exercised with no live host;
- where the Transport Attachment record persists, the tombstone-envelope
  that preserves the CAS token across detach and reattach, and the
  serialization boundary that keeps it out of ADO;
- the explicit "verbs rejected" list a conformance test can enumerate,
  including the session-steering-mode isolation rule and the
  event-boundary assertion on transport outcomes;
- the single Change Proposal rendering integration point, its DTOs and
  support registry, its post-selection refusal fallback, and the
  authorization-neutral fallback that must remain universal;
- the named failure identifiers this contract introduces, and for each
  operation whether the outcome is `Result.Ok(observation)` or
  `Result.Fail(identifier)`.

### 1.1 What "observe-only" means, precisely

This contract is observe-only. That phrase is used elsewhere in AB#730
and MAP AB#726 as a slogan; it is spelled out here so §§3, 6, 7, 9,
and 12 cannot be read as loosening it. Observe-only means all three of:

- **(a) No creation of host surfaces.** No adapter creates a workspace,
  tab, pane, agent session, or terminal-host window through the seam
  this contract defines. The `Custom.` creation/management surface is
  the deferred MAP AB#726 decision (see below) and is out of scope
  here in full.
- **(b) No management verbs.** No adapter reaches Herdr `focus`,
  `rename`, `move` / `layout`, `resize`, `zoom`, `prompt`, `start`, or
  any equivalent verb on any other host through the seam. These are
  the R12–R15 rejected rows in §9.1 and are non-negotiable at v1.
- **(c) No implicit host mutation on any path.** `Close` and
  `PartialClose` are the single permitted category of host mutation
  in this contract, and only when a caller invokes them **explicitly**.
  No probe (`ReportStatus`, `ProbeLiveness`), read
  (`TransportAttachmentStore.ReadWithRevision`), detach, validator,
  rendering-selection function (`SelectPresentation`, `Render`),
  storage-write path, or adapter-internal event handler may reach
  `Close` or `PartialClose`. No retry, cleanup, sweep, error-recovery,
  cascade-hint, or dispatch-degradation path may reach them either.
  A conformance test (§9.1 event-boundary invariant applied to R11–R15
  and the two mutation verbs) walks the reachable event/call graph
  from every non-close entry point and fails on any reachable
  invocation of `Close` or `PartialClose`.

The rationale for keeping `Close` and `PartialClose` on the surface
(rather than stripping them alongside R11–R15) is the AB#744 versus
MAP AB#726 scope split: AB#744 explicitly scopes "detach and close
seams" as its deliverable, and the AB#746 / AB#747 acceptance criteria
list "implements detach and close, including the degradation rules for
partial close" as required. The broader creation/management surface —
which would include focus, rename, move, resize, zoom, prompt, start,
and any create verb — remains deferred to the MAP AB#726 follow-up.

Explicit non-goals, each named so downstream implementers do not confuse
settled from open:

- **Transport creation and management surface.** MAP AB#726 explicitly
  deferred this decision. This contract is observe-only per §1.1: no
  create verb, no management verb, no implicit host mutation. No CLI
  verb, MCP tool, or programmatic API for creating a workspace, tab,
  pane, agent session, or terminal-host window is fixed here, and no
  focus / rename / move / resize / zoom / prompt / start verb is fixed
  either. The deferred surface would be unblocked by a follow-up
  sub-spec named on the MAP AB#726 register; until it lands, adapters
  MUST NOT expose create/manage operations through the seam this
  contract defines.
- **Change Proposal record format, journal file layout, and plan lifecycle
  gates.** Owned by the Change Proposal / plan lifecycle design; this
  contract fixes only the single rendering integration point (§10) and
  the minimal DTOs that integration requires.
- **Claim record shape, lifecycle, and state enum.** Owned by AB#737 and
  AB#739; the Transport Attachment carries no claim identity and never
  reads claim state.
- **Session-steering-mode derivation.** Owned by AB#736 / do-work
  routing; §9 forbids consulting any transport field for steering.
- **ADO field or link projection.** Prohibited entirely; §8 and §9 fix
  the boundary.
- **Cross-machine transport discovery, remote host bridges, and Windows
  Terminal enumeration.** `local://host-surfaces.md` shows no such
  surface exists today; this contract does not invent one.

The design is process-agnostic: no work-item type, state name, field
reference, host product identifier, or person is hard-coded here.
Adapter identity is opaque to core (§7).

## 2. The record and its shapes

### 2.1 Field-by-field definition

A Transport Attachment is exactly three fields, envelope-wrapped for
CAS. The envelope, the record inside it, and the tombstone form together
constitute the persisted document `transport.json`.

```json
{
  "$schema": "twig-transport-attachment/v1",
  "version": 1,
  "revision": <positive integer, monotonic>,
  "connectionRef": "<opaque hash from worktree-attachment-storage §5.1>",
  "recordedAt": "<RFC3339 UTC timestamp>",
  "state": "attached" | "detached",
  "record": <Record | null>
}
```

Envelope semantics:

- `revision` is a positive integer that increments on every mutation
  (attach, reattach, detach) and is preserved across the `detached`
  tombstone state so that detach + reattach cannot silently rewind the
  CAS token. It is the token compared by `expectedRevision` (§8.4).
- `state = "attached"` REQUIRES `record` present; `state = "detached"`
  REQUIRES `record = null` and marks the tombstone.
- `connectionRef` is the AB#736 §5.1 hash of the live `twig.json`
  connection block; a mismatch at read time raises
  `transport-connection-mismatch` (§11).

The `Record` shape is exactly three fields, each independently
nullable subject to the shape validator in §2.2:

```
Record = {
  worktree: WorktreePayload | null,
  agent:    AgentPayload    | null,
  terminal: TerminalPayload | null
}
```

The three payload shapes are:

| Field | Payload |
|---|---|
| `worktree` | `{ worktreeFingerprint: <§3.2 tuple from AB#736>, target: <§7.4 TransportAdapterTarget with role = "worktree"> }` |
| `agent` | `{ target: <§7.4 TransportAdapterTarget with role = "agent">, sessionKind: <opaque, adapter-defined enum string>, recordedStatus: <core-neutral status per §4>, recordedAt: <RFC3339 UTC>, capabilities: <§3.3 capability set> }` |
| `terminal` | `{ target: <§7.4 TransportAdapterTarget with role = "terminal">, capabilities: <§3.3 capability set> }` |

The `worktreeFingerprint` inside `worktree` MUST byte-equal the
worktree's fingerprint recorded by AB#736 §3.2; on mismatch, the read
raises `transport-worktree-fingerprint-mismatch` (§11).

`target` is the structured `TransportAdapterTarget` defined in §7.4
and carries the `adapterId`, `hostAttachmentId`, `hostAttachmentIdKind`,
and `adapterContext` fields dispatch needs to reach the correct host
attachment. The structured target replaces the earlier flat
`adapterId`/`hostAttachmentId`/`hostAttachmentIdKind` fields and is
what every §7 adapter method receives.

`hostAttachmentId` is treated by core as an opaque string; interpretation
is entirely the adapter's concern. `adapterId` is the registration key
defined in §7.

Every `capabilities` block is a set of the **optional** capability
names in §3.3. `RecordIdentity` and `DescribeAdapter` are mandatory
common-denominator capabilities (§3.1) and MUST NOT appear inside a
persisted `capabilities` set; a validator seeing either raises
`transport-unknown-capability`. Core treats every valid entry as an
opaque string for forward compatibility but rejects any string not in
the current §3.3 optional enumeration with `transport-unknown-capability`.

### 2.2 Valid shapes

Exactly two shapes are accepted, and each is decidable from the record
alone with no reference to caller intent or adapter-declared metadata.
The shape validator runs on every `ReadTransport` and `WriteTransport`
boundary and, on rejection, returns a named failure identifier per §11.

| Shape | `worktree` | `agent` | `terminal` | Meaning |
|---|---|---|---|---|
| **Direct-human** | present | `null` | present | A human is working the worktree directly through a terminal host (e.g. Windows Terminal). No agent mediates the session. |
| **Agent-driven** | present | present | present or `null` | An agent session (e.g. a Herdr agent) is driving the worktree, optionally hosted in a terminal-host attachment. |

The direct-human shape is defined structurally: the presence of a
`terminal` payload with `agent = null` and `worktree` present IS the
direct-human shape. Nothing in the record identifies "human" beyond
this positional rule, and no adapter-declared "human-owned kind"
metadata exists in this contract.

Every other combination is invalid. The rejection identifiers, matching
the AB#736 §8 kebab-case string-constant convention, and their fixed
evaluation order are:

| # | Rejection | Identifier |
|---|---|---|
| 1 | Envelope/record schema parse failure (unparseable JSON, unknown `$schema`, wrong `version`, `state`/`record` disagreement, malformed `revision`). | `transport-record-invalid` |
| 2 | `state = "attached"` but `record.worktree` field absent. | `transport-worktree-missing` |
| 3 | `state = "attached"` with only `worktree` set — both `record.agent` and `record.terminal` `null`. | `transport-bare-worktree` |
| 4 | `state = "attached"` and the record fits **neither** the direct-human row (`agent = null` AND `terminal` present) nor the agent-driven row (`agent` present). This fires for the residual shapes that survive rows 2–3, e.g. `worktree` present, `agent` present, `terminal` malformed such that neither shape row matches. | `transport-orphan-terminal` |
| 5 | Any `agent.recordedStatus` not in §4.1's enumeration. | `transport-unknown-status` |
| 6 | Any capability name in a `capabilities` block outside §3.3's optional catalogue (including `RecordIdentity`/`DescribeAdapter`, which are common-denominator and MUST NOT appear in a persisted set). | `transport-unknown-capability` |

Ordering is fixed as listed so the conformance test is deterministic.
The validator MUST evaluate the checks in order and return the
first-tripped identifier. The rows are non-overlapping by
construction:

- row 1 rejects everything that is not structurally a parseable
  envelope + record. `transport-record-invalid` no longer overlaps
  the "missing required field" case that row 2 owns: row 1 is
  restricted to *schema-level* parse failures (JSON syntax, unknown
  `$schema`, wrong `version`, `state`/`record` disagreement,
  non-integer `revision`). A field-absent `worktree` in an otherwise
  parseable envelope is row 2, not row 1;
- row 2 fires only when `record.worktree` is field-absent;
- row 3 fires only when `worktree` is present AND both `agent` and
  `terminal` are `null`;
- row 4 fires only when neither row 2 nor row 3 applies AND the record
  does not fit either §2.2 shape row;
- rows 5 and 6 fire only when the shape check has already passed.

The tombstone (`state = "detached"`) skips rows 2–6 entirely — a
tombstone's `record` is `null` by construction and the envelope check
(row 1) validates it.

### 2.3 Settled and deferred

Settled: the envelope + record shape, the two valid shapes, the
structural definition of direct-human (no intent capture, no
human-owned-kind metadata), and the six rejection identifiers in fixed
order. Deferred: any additional shape (e.g. multi-agent per-worktree,
multi-terminal per-worktree) — deferred to a follow-up MAP AB#726
sub-spec, unblocked only by a settled multi-attachment policy.

## 3. Capability model

### 3.1 The genuine common denominator

`local://host-surfaces.md` establishes that the two shipping hosts are
radically asymmetric. Herdr exposes workspace-qualified opaque IDs,
a five-value status vocabulary, liveness signals (`interactive_ready`,
`state_change_seq`, `revision`, `pane process-info`), and explicit
`herdr tab close` / `herdr pane close` verbs. Windows Terminal exposes
**no** query, enumeration, or status surface: `wt.exe` creates and
acts, `settings.json` is configuration rather than live state, there is
no documented way to list windows, obtain stable tab/pane IDs, query
focus, observe liveness, or observe the outcome of a partial close.
`local://host-surfaces.md` further records that supplying a nonexistent
`--window <id>` **silently creates a new window** rather than failing,
so even the identity handle Windows Terminal accepts is not a
discovery mechanism.

A contract that requires a status vocabulary, detach/close, or a probe
budget from *every* adapter is therefore unimplementable for Windows
Terminal. The settled shape is a **capability-declaration model**: the
core requires only the genuine common denominator, and everything
richer is an optional capability an adapter declares at registration
time. This is the call, and it is settled.

The common denominator every adapter MUST implement — the mandatory
capabilities that are NOT declared in `Capabilities` because every
adapter has them:

| Capability | Semantics |
|---|---|
| `RecordIdentity` | Accept an opaque `hostAttachmentId` + `hostAttachmentIdKind` supplied at attachment time (never discovered by the adapter, because Windows Terminal cannot discover) and echo it back on read. |
| `DescribeAdapter` | Return the adapter's registration metadata: `adapterId`, a display name, the set of declared optional capabilities (§3.3), and a stable adapter version. |

No adapter is required to observe, probe, or manipulate the host beyond
recording what the caller supplied. The null adapter (§7.3) implements
exactly these two and declares no optional capabilities.

### 3.2 Dispatch model — per-operation degradation is the default

`Capabilities` on a registered adapter is exactly the set of **optional**
capability names from §3.3 the adapter declares. `RecordIdentity` and
`DescribeAdapter` are never members. Core queries the declared set on
every optional-capability invocation. For each capability, the
dispatch layer applies a defined per-operation behaviour:

| Optional capability | Adapter declared | Adapter NOT declared |
|---|---|---|
| `StatusReporting` | Adapter is invoked; returns a §5 `TransportStatusObservation`. | Core returns `Result.Ok(TransportStatusObservation { status = unobservable, recordedAt = null, freshness = unobservable })`. |
| `LivenessProbe` | Adapter is invoked; returns a §5 `TransportLivenessObservation`. | Core returns `Result.Ok(TransportLivenessObservation { presence = unknown, recordedAt = null, freshness = unobservable })`. |
| `Detach` | Adapter is invoked to drop its per-host bookkeeping; the record is then removed by §8. | Core removes the record via §8 and returns `Result.Ok()` — detach at the record level is always available. |
| `Close` | Adapter is invoked to run the host close verb; success writes the §8 detach tombstone inside the same transaction. | Core returns `Result.Fail("transport-close-not-supported")`. |
| `PartialClose` | Adapter is invoked to scope-close; returns a §6.3 outcome. | Core returns `Result.Fail("transport-partial-close-not-supported")`. |

`transport-capability-not-declared` (§11) is reserved for a caller
invoking a capability whose name is **not** in the §3.3 catalogue at
this schema version — a client-bug rail against a future/unknown name.
It is never raised for any of the five capabilities above; those either
return their adapter result or the row-defined degradation.

A caller MUST NOT assume any behaviour beyond the row's degradation for
a not-declared capability; specifically, `unobservable` is not a
`working`/`idle`/`blocked`/`done` guess, and `unknown` presence is not
evidence the host is absent.

**Interaction with §1.1(c).** For declared adapters, `Close` and
`PartialClose` are still only reachable when the caller invokes them
explicitly on the seam. Nothing in this dispatch table permits an
implicit invocation from a probe, read, detach, validator,
rendering-selection, storage, retry, cleanup, sweep, or
error-recovery path (§9.1 event-boundary invariant on R11–R15 and the
two mutation verbs).

### 3.3 Optional capability catalogue

The optional capabilities — the exhaustive `Capabilities` domain at v1:

| Capability | Declared behaviour |
|---|---|
| `StatusReporting` | Adapter runs a bounded host query under the §5.1 budget and returns `TransportStatusObservation { status, recordedAt, freshness }` per §5. Whether the adapter caches the result to reuse across rapid successive reads is an implementation choice; if it caches, the §5.3 staleness rule governs reuse and any stale reuse MUST report `freshness = stale`. |
| `LivenessProbe` | Adapter runs a bounded existence/availability probe under the §5.1 budget and returns `TransportLivenessObservation { presence ∈ { present, absent, unknown, error }, recordedAt, freshness }`. |
| `Detach` | Adapter releases any host-side tracking it owns (e.g. Herdr adapter drops cached lifecycle references for the `hostAttachmentId`) without acting on the host process. Returns `Result.Ok()`. Detach never terminates a host session; it is a Twig-side stop-tracking. |
| `Close` | Adapter issues the host-defined close for the referenced `hostAttachmentId` (e.g. Herdr adapter's single unpiped `herdr tab close`), then returns `Result.Ok()` or a named failure `transport-close-adapter-failed`. Only reachable when the caller explicitly invokes `Close`; §1.1(c) forbids any implicit reach. |
| `PartialClose` | Adapter attempts to close a subset of the host attachment (e.g. a single pane inside a tab) scoped by `PartialCloseScope` (§7.4) and reports the outcome per §6.3. Only reachable when the caller explicitly invokes `PartialClose`; §1.1(c) forbids any implicit reach. |

The five optional capabilities above are exhaustive at v1. New
capabilities are a schema change to this document.

### 3.4 Justification from host surfaces

The optional/required split maps directly onto `local://host-surfaces.md`:

- **`StatusReporting` is optional** because Windows Terminal exposes no
  status surface at all. Herdr's adapter declares it and maps the five
  host values; Windows Terminal's adapter does not declare it and
  callers receive `unobservable`.
- **`LivenessProbe` is optional** because Windows Terminal has no
  enumeration or query surface — and because a nonexistent `--window`
  silently creates a new window, even the identity handle is not a
  probe. Herdr can report presence via `pane current` / `agent
  explain`; Windows Terminal cannot.
- **`Detach` is nominally optional** because Herdr has no dedicated
  detach verb, so even the Herdr adapter's declaration is a Twig-side
  bookkeeping operation. Windows Terminal has no concept exposed.
- **`Close` is optional** because Windows Terminal has no documented
  API to close a specific tab or pane by ID from outside; the surface
  documents user actions (`Ctrl+Shift+W`) only. Herdr provides
  `herdr tab close` and `herdr pane close`.
- **`PartialClose` is optional AND its Herdr-side outcome is
  explicitly UNVERIFIED** in `local://host-surfaces.md`. §6.3 requires
  the Herdr adapter's declaration to degrade safely under this
  uncertainty rather than pretend the outcome is observable.

## 4. Status vocabulary

### 4.1 Core-neutral values

The core-neutral status enumeration, exhaustive:

| Value | Meaning to core |
|---|---|
| `idle-ambiguous` | Adapter observed a host state that means "ready for input or turn-finished" but cannot distinguish. **The only value Herdr's `idle` maps to.** Callers MUST NOT read this as proof of any turn boundary. |
| `working` | Host reports the session actively producing output/turn work. |
| `blocked` | Host reports a recognized approval/question/waiting-for-input UI. |
| `done` | Host reports background work finished. This is a **hint**, not a completion proof; callers still consult Change Proposal / plan status for authoritative completion. |
| `unknown` | Host is present but the state is not confidently classifiable. |
| `unobservable` | No `StatusReporting` capability. Distinct from `unknown`: `unknown` means "adapter probed and the host was inconclusive"; `unobservable` means "no probe is possible on this transport". |

### 4.2 Adapter mapping rule

Adapters MUST map host status to core status by table lookup only. No
adapter may synthesize a value not in §4.1.

Herdr adapter (concrete mapping, mandated):

| Herdr `agent_status` | Core value |
|---|---|
| `idle` | `idle-ambiguous` |
| `working` | `working` |
| `blocked` | `blocked` |
| `done` | `done` |
| `unknown` | `unknown` |

Windows Terminal adapter (concrete mapping, mandated): does not declare
`StatusReporting`; core returns `unobservable` per §3.2.

### 4.3 The `idle` handling rule

The Herdr `idle`→`idle-ambiguous` collapse is deliberate and settled.
`local://host-surfaces.md` documents that `idle` is ambiguous between
never-prompted and turn-finished, and that status alone never proves
delivery. The contract makes the ambiguity unreadable to a caller: the
value is *named* ambiguous, and any code that branches on it MUST
handle it as neither `working` nor `done`. Adapters MUST NOT map
`idle` to `done` even when a lifecycle signal suggests turn completion;
`done` is reserved for the host's own `done`. This rule is the
authorization-neutrality safeguard for §9 — a status field must never
be usable as a completion proof.

### 4.4 Settled and deferred

Settled: the six-value core vocabulary, the collapse rule, and the
Herdr concrete mapping. Deferred: additional adapter-provided
lifecycle facets (e.g. `visible_blocker`, `interactive_ready`,
`state_change_seq`) — deferred to an optional `LifecycleFacets`
capability, unblocked only when a caller demonstrates a use case that
authorization-neutrality (§9) cannot serve with the six-value set.

## 5. Liveness, freshness and probe budget

### 5.1 Bounded probe semantics

Every `LivenessProbe` and `StatusReporting` invocation runs under a
bounded timeout. **No probe may block indefinitely.** The default
budget is:

| Probe | Default timeout | Rationale |
|---|---|---|
| `LivenessProbe` | 2000 ms | Envelope for a socket round-trip against Herdr's `pane current` / `agent explain`; conservative enough to succeed under load. |
| `StatusReporting` | 500 ms | Envelope for a snapshot query against Herdr's `herdr api snapshot` or `herdr pane current --current`; a timeout at this budget indicates the host observation surface is not responding within the intended interactive window. |

Callers MAY override the budget per call via
`TransportProbeOptions { timeoutMs }`. Core clamps to `[100, 30000]` ms
inclusive; out-of-range values raise `transport-probe-budget-invalid`
as `Result.Fail`. The `agent wait` / `pane wait-output`
indefinite-when-omitted timeouts that `local://host-surfaces.md`
records are explicitly **rejected** as a probe path: no adapter may
build any capability on top of an indefinite-blocking Herdr verb.

### 5.2 Timeout and adapter-failure result — named, never silent

A timed-out or adapter-failed probe yields a **named result**, never an
exception. Whether the result is `Result.Ok(observation)` or
`Result.Fail(identifier)` is fixed per operation:

- `LivenessProbe` timeout → `Result.Ok(TransportLivenessObservation { presence = error, error = "transport-probe-timeout", recordedAt = <RFC3339 now>, freshness = stale })`. Timeout is an **observation** (the probe ran, took too long, reports its bounded failure), not a dispatch-level `Result.Fail`; callers rendering "we tried to probe" surface the observation.
- `StatusReporting` timeout → `Result.Ok(TransportStatusObservation { status = unknown, recordedAt = <RFC3339 now>, freshness = stale, timeoutError = "transport-probe-timeout" })`.
- `LivenessProbe` adapter internal failure (adapter code threw / host command failed for a non-timeout reason) → `Result.Fail("transport-probe-adapter-failed")`. This is a dispatch-level failure the caller branches on via `Result`; the adapter could not produce a bounded observation at all.
- `StatusReporting` adapter internal failure → `Result.Fail("transport-probe-adapter-failed")`, same rationale.

The distinction is intentional: `transport-probe-timeout` names a
completed-but-slow probe (the adapter honoured its budget and returned
the bounded failure observation); `transport-probe-adapter-failed`
names a probe the adapter could not complete at all. Callers branch on
the `Result` shell for the second and on the embedded observation for
the first; the storage and adapter layers never surface an unnamed
error to §12's implementers.

### 5.3 Freshness of an observation — timestamp and optional reuse

Every observation carries a `freshness` field in
`{ fresh, stale, unobservable }`. Both `TransportStatusObservation`
and `TransportLivenessObservation` carry it; the earlier draft omitted
it from liveness and is corrected here.

Freshness is defined against the observation's own `recordedAt`
timestamp, which the adapter stamps at the moment it obtains the value
from the host (successful bounded query return, or bounded-failure
observation on timeout per §5.2):

- `fresh`: `now - recordedAt <= freshWindowMs`; default
  `freshWindowMs = 2000` ms.
- `stale`: `now - recordedAt > freshWindowMs`. Callers MAY still
  consume it — Change Proposal rendering (§10) treats `stale` as a
  rendering hint, not a decision gate.
- `unobservable`: the adapter does not declare the corresponding
  capability (§3.2 dispatch degradation); `recordedAt` is `null`.

**Reuse is an implementation choice.** An adapter MAY keep an
in-process cache of the most recent observation per `hostAttachmentId`
and return it on a subsequent invocation instead of re-querying the
host, but this is a permitted optimisation, not a mandated
architecture. When an adapter reuses a cached observation, the
returned `recordedAt` MUST be the original observation timestamp (not
`now`) so `freshness` computed by the caller — or by the adapter
itself before return — correctly degrades from `fresh` to `stale`
once `freshWindowMs` has elapsed. An adapter that does not cache
re-queries the host on every invocation under the §5.1 budget; the
returned observation is always `fresh` on return by construction.

**Failure-mode observations carve-out.** A bounded-failure observation
(timeout, or any other §5.2 failure that produces an
`Result.Ok(observation)`) MUST report `freshness = stale` regardless
of `recordedAt`. The adapter did not obtain a genuine host value on
that call; the stale label signals "do not trust this as a live
read". This is the sole exception to the timestamp-based rule above
and is what §5.2 hardcodes for both the `LivenessProbe` and the
`StatusReporting` timeout paths.

**Herdr adapter — grounded observation surface.** Herdr's live
observation surface is poll-only: `herdr api snapshot` and
`herdr api schema` on the `herdr api` verb; `herdr pane current
--current`; `herdr agent explain <target> --json`; and
`herdr agent wait <target> --until <state> --timeout <ms>` for
bounded blocking waits. There is no subscribe / event / stream /
watch verb on `herdr api`, and no push feed the adapter could
subscribe to. The Herdr adapter therefore implements
`StatusReporting` and `LivenessProbe` as bounded snapshot queries
under the §5.1 budget, MAY cache the result for reuse under this
section, and MUST pass an explicit `--timeout` on every
`herdr agent wait` invocation because omission blocks indefinitely
(§5.1). No dedicated subscription thread, no reconnect loop, and no
broker event handler are permitted by this contract, because Herdr
has no host mechanism underneath them.

**Windows Terminal adapter.** Does not declare `StatusReporting` or
`LivenessProbe`; §3.2's absent-capability degradation applies and
the `freshness = unobservable` value is emitted synchronously with
no cache.

**Null adapter.** Does not declare `StatusReporting` or
`LivenessProbe`; same as Windows Terminal.

`freshWindowMs` is a constant of this contract, not an adapter tuning
knob; changing it is a schema change to this document. The
timestamp-based staleness rule keeps observation costs bounded across
rapid successive reads (e.g. Change Proposal renders) without letting
an old value silently outlive the host's turn: an observation
continues to advertise its true age, and callers rendering a stale
read see it as `stale` rather than being told a lie about `fresh`.

### 5.4 Settled and deferred

Settled: bounded probe, `transport-probe-timeout` embedded
observation, `transport-probe-adapter-failed` dispatch-level failure,
the two default budgets, the caller-override clamp range, the
three-value freshness enum on both observation types, freshness
defined against the observation's `recordedAt`, and the optional
in-process cache as a permitted implementation optimisation governed
by the §5.3 staleness rule (not a mandated push-feed subscription,
because Herdr's surface has no such feed to subscribe to). Deferred:
adaptive/backoff probe scheduling — deferred to a follow-up perf
sub-spec, unblocked only when a concrete regression demonstrates the
fixed budget is inadequate.

## 6. Detach and close

### 6.1 Detach

Detach is a **Twig-side** stop-tracking, not a host action. Detach on
any adapter, declared or absent (§3.2), succeeds unless storage itself
fails.

- With the `Detach` capability declared, the adapter is invoked before
  the record is removed so it may drop internal caches keyed on the
  `hostAttachmentId`. A failure returned by the adapter is surfaced as
  `Result.Fail("transport-detach-adapter-failed")`; core still writes
  the detach tombstone (§8.2 — detach is idempotent from the record's
  perspective, and the CAS revision advances even under adapter
  failure so a subsequent reattach cannot ABA-collide with the
  pre-detach record).
- Without the capability, core writes the detach tombstone and returns
  `Result.Ok()`.

Detach never issues any host-side termination. `Detach` MUST NOT
reach `Close` or `PartialClose` on any path — including adapter
internal cleanup, error recovery, or retry — per §1.1(c). Callers
wanting a host action MUST invoke `Close` explicitly.

### 6.2 Close

Close is the host-defined termination of the referenced attachment,
and is reachable only when the caller explicitly invokes `Close` on
the seam.

- With the `Close` capability declared, the adapter runs the host's
  close verb (Herdr adapter: exactly one unpiped `herdr tab close` /
  `herdr pane close`, matching `local://host-surfaces.md`'s
  cross-check guidance) and returns `Result.Ok()` or
  `Result.Fail("transport-close-adapter-failed")`. After a successful
  close, the Transport Attachment tombstone is written by §8 inside the
  same transaction.
- Without the capability, core returns
  `Result.Fail("transport-close-not-supported")`.

`Close` and `PartialClose` are the single permitted host mutation
category in this contract, and their reachability is strictly
gated on explicit caller invocation (§1.1(c)). AB#744 scopes the
detach-and-close seams as a deliverable and AB#746 / AB#747 both
accept "implements detach and close, including the degradation rules
for partial close"; the broader creation/management surface remains
deferred to MAP AB#726 (§1).

### 6.3 Partial close — the UNVERIFIED-behaviour safeguard

`local://host-surfaces.md` marks Herdr's partial-close outcome as
UNVERIFIED, and Windows Terminal has no close-by-ID surface at all.
The contract therefore does not assume that a per-pane/per-tab close
leaves the enclosing structure predictable, and partial close is
treated as a distinct capability, not a variant of `Close`. Like
`Close`, `PartialClose` is only reachable via explicit caller
invocation.

- With the `PartialClose` capability declared, the adapter attempts
  the scoped close (scope carried by `PartialCloseScope`, §7.4) and
  returns either `Result.Ok(TransportPartialCloseOutcome { attempted, observedRemaining ∈ { unknown, subset, none }, error? })`
  on a completed attempt, or
  `Result.Fail("transport-partial-close-adapter-failed")` on an
  adapter internal failure that could not produce a bounded outcome.
  The `observedRemaining` field is populated as `subset` or `none`
  only when the adapter can independently confirm the post-state via
  its own declared observation capabilities (e.g. Herdr adapter
  re-queries `pane list`). If the adapter cannot confirm, it MUST
  return `observedRemaining = unknown` and MUST NOT invent an outcome.
- Without the capability, core returns
  `Result.Fail("transport-partial-close-not-supported")`.

A caller receiving `observedRemaining = unknown` MUST NOT re-issue a
compensating `Close`; the contract explicitly permits a leaked
host-side pane/tab over a destructive assumption. This is the
"unverified host behaviour degrades safely rather than being assumed"
rule, and it is reinforced by §1.1(c)'s ban on any implicit
`Close`/`PartialClose` reach from a retry / cleanup / sweep /
error-recovery path.

### 6.4 Settled and deferred

Settled: detach as Twig-side stop-tracking, close as host-defined
termination reachable only via explicit caller invocation, partial
close as a distinct capability with UNVERIFIED-safe degradation, and
the "never shell out from outside the adapter" rule. Deferred: cascade
close semantics (close-agent-and-terminal in one call) — deferred to
the same follow-up that lands multi-attachment policy.

## 7. Registration surface

### 7.1 Adapter identity

Every adapter registers with core via a single interface. Names below
follow AB#736 §9's language-neutral form; exact CLR types are the
implementer's concern.

```
ITransportAdapter:
    AdapterId       -> string                # opaque registration key; e.g. "herdr", "windows-terminal", "null"
    DisplayName     -> string                # human-facing, never routed on
    AdapterVersion  -> string                # opaque semver-shaped
    Capabilities    -> Capability[]          # declared subset of §3.3 OPTIONAL capabilities only
    RecordIdentity(RecordIdentityRequest)             -> Result<TransportAttachmentRecord>
    DescribeAdapter()                                 -> AdapterDescription
    ReportStatus(TransportAdapterTarget, options)     -> Result<TransportStatusObservation>       # only if StatusReporting declared
    ProbeLiveness(TransportAdapterTarget, options)    -> Result<TransportLivenessObservation>     # only if LivenessProbe declared
    Detach(TransportAdapterTarget)                    -> Result                                   # only if Detach declared
    Close(TransportAdapterTarget)                     -> Result                                   # only if Close declared
    PartialClose(TransportAdapterTarget, PartialCloseScope)
                                                      -> Result<TransportPartialCloseOutcome>     # only if PartialClose declared
```

Core inspects `Capabilities` before dispatching a call. For an
undeclared **§3.3 capability**, core applies the per-operation
degradation defined in §3.2 rather than invoking the adapter; the
adapter is never called. `transport-capability-not-declared` is raised
only when the caller invokes a capability name not present in the §3.3
catalogue at all (client bug rail).

### 7.2 Core selection rule

Core resolves the adapter for a given Transport Attachment record by
matching `record.<field>.target.adapterId` against the registry
populated at DI composition time. Registration follows the
`local://core-seams.md` `AddConnectionServices` pattern: a singleton
factory lambda in `TwigServiceRegistration` returns a
`TransportAdapterRegistry` containing every adapter available in this
build. The registry is resolved by `adapterId` string equality; there
is no fallback, no discovery, and no ordering-driven priority. Unknown
`adapterId` raises `Result.Fail("transport-adapter-not-registered")`.

Adapter DI registration is fixed as:

- `ITransportAdapterRegistry` → `TransportAdapterRegistry` (singleton).
- Each `ITransportAdapter` implementation → singleton, injected into
  the registry by constructor list.

Nothing about the registry is reflective; the composition is
source-generated-friendly and AOT-compatible per the repository
constraints.

### 7.3 Null adapter — the "no live host" path

AB#745 requires the seam be exercisable with no live host. The
contract fixes a null adapter, `adapterId = "null"`, that:

- declares `Capabilities = { }` (empty optional set — only the common
  denominator applies);
- implements `RecordIdentity` by echoing the caller's request into a
  `TransportAttachmentRecord`;
- implements `DescribeAdapter` with a fixed description;
- returns the absent-capability degradation for every optional call
  per §3.2.

The null adapter is always registered. It is the AB#745 test path and
also the runtime path when a Transport Attachment is recorded with a
null `adapterId` throughout. The null adapter's **one valid recorded
shape** is a direct-human record: `worktree.target.adapterId = "null"`,
`agent = null`, `terminal.target.adapterId = "null"`; the shape
validator (§2.2) accepts this because it structurally matches the
direct-human row. No other null-adapter shape is a valid persisted
record; earlier drafts described a "bare" null record and are
corrected here.

Core selects the null adapter only when the caller supplies
`adapterId = "null"`; a record with an `adapterId` unknown to this
build MUST NOT silently fall through to null — that would be
authorization-neutrality laundering. It raises
`Result.Fail("transport-adapter-not-registered")`.

### 7.4 Structured target and DTOs

Every dispatch call carries a `TransportAdapterTarget` rather than a
bare `hostAttachmentId` so the adapter can workspace-qualify a Herdr
ID, distinguish tab from pane, and run AB#746's mandated preflight
against live records. The DTOs are:

```
TransportAdapterTarget = {
  role:                  "worktree" | "agent" | "terminal",
  adapterId:             string,
  hostAttachmentId:      string,                # opaque; adapter-defined
  hostAttachmentIdKind:  string,                # opaque enum; adapter-defined
  adapterContext:        Map<string, string>    # opaque, adapter-defined key/value context
}

RecordIdentityRequest = {
  worktreeFingerprint: <§3.2 tuple from AB#736>,
  worktreeTarget:      TransportAdapterTarget,
  agentTarget:         TransportAdapterTarget | null,
  agentSessionKind:    string | null,           # only when agentTarget non-null
  terminalTarget:      TransportAdapterTarget | null
}

PartialCloseScope = {
  scopeKind:   string,                          # opaque, adapter-defined ("pane", "tab", …)
  scopeId:     string,                          # opaque, adapter-defined; e.g. Herdr pane id
  reason:      "user-requested" | "cascade-hint" | "adapter-internal"
}

AdapterDescription = {
  adapterId:        string,
  displayName:      string,
  adapterVersion:   string,
  capabilities:     Capability[],               # declared §3.3 optional capabilities
  supportedRoles:   ("worktree" | "agent" | "terminal")[],
  humanReadable:    string                      # for diagnostics only, never a router key
}
```

`adapterContext` is opaque to core. Adapter obligations:

- **Herdr adapter (mandated).** MUST populate `adapterContext` with at
  least `workspace` and, when applicable, `tab` and `pane` to satisfy
  the preflight cross-check on workspace/tab/pane IDs before issuing
  any close. `hostAttachmentIdKind` is one of `"herdr-workspace"`,
  `"herdr-tab"`, or `"herdr-pane"`.
- **Windows Terminal adapter (mandated).** MUST normalize a caller's
  integer window handle to a decimal string with no leading zeros in
  `hostAttachmentId`, with `hostAttachmentIdKind = "wt-window-integer"`
  when the caller supplied an integer, or
  `hostAttachmentIdKind = "wt-window-name"` when the caller supplied a
  named `--window` target per `local://host-surfaces.md` §Identity. The
  normalized string is what every downstream read sees; the raw
  pre-normalization value is never persisted.
- **Null adapter.** `adapterContext` is empty; `hostAttachmentIdKind`
  is `"null"`; `hostAttachmentId` is a caller-supplied opaque string.

### 7.5 Settled and deferred

Settled: single interface, factory-lambda DI registration matching the
existing `AddConnectionServices` pattern, string-equality selection,
mandatory null adapter (with its single valid direct-human shape),
the structured `TransportAdapterTarget`, and the `RecordIdentityRequest`,
`PartialCloseScope`, and `AdapterDescription` DTOs. Deferred: adapter
hot-swap and adapter capability upgrade at runtime — deferred to the
follow-up sub-spec that lands creation/management surface.

## 8. Storage placement

### 8.1 Tier

The Transport Attachment record lives in the **worktree-local tier**
(AB#736 §4.2). Rationale: transport identity is per-checkout — a
linked worktree may be attached to a different host session than the
primary — and the record must never leave the machine.

### 8.2 File and tombstone envelope

Fixed path: `<worktreeRoot>/.twig/transport.json`.

Layout follows the other worktree-local documents in AB#736 §4.2.
`transport.json` is gitignored by the existing `.twig/` root exclusion;
no additional `.gitignore` change is required.

The persisted document is the envelope defined in §2.1. Once the file
has been written for the first time, it is **never deleted**; detach
writes a tombstone envelope (`state = "detached"`, `record = null`) with
an incremented `revision` in place of the previous document. This
preserves the CAS token across detach + reattach and prevents an ABA
where a reader observing "no attachment then a new attachment" cannot
tell whether the two are the same by revision.

- **No file present:** never-attached. Reads return
  `Result.Ok(no-attachment, revision = 0)`; the caller MUST NOT
  synthesize a default record.
- **File present, `state = "attached"`:** live attachment. `record` is
  the §2.1 `Record`.
- **File present, `state = "detached"`:** tombstone. `record` is `null`.
  Reads return "no attachment for rendering purposes" but the envelope's
  `revision` remains the CAS anchor.

First-write is created by the atomic-write path (§8.4). Every subsequent
attach, reattach, or detach is a `revision + 1` write of the envelope,
also via §8.4. The tombstone is compacted away only by an explicit
reinit-of-storage path, which is not defined here.

### 8.3 Serialization boundary — Twig-local only

Every field of a Transport Attachment record is Twig-local. **No
Transport field may reach any ADO field, link payload, description,
comment, tag, area/iteration path, or work-item reference at any
layer.** The compile-time seam is a necessary condition, not a
sufficient one: a caller reading a transport string (an
`adapterId`, a `hostAttachmentId`, a status name) and passing it
through a generic ADO field/link/comment API would evade a type-based
guard. The serialization boundary is therefore enforced by three
complementary rails, all of which MUST hold:

1. **Namespace seam (compile-time).** `TransportAttachmentRecord` and
   every nested type — including `TransportAdapterTarget`,
   `TransportStatusObservation`, `TransportLivenessObservation`,
   `TransportPartialCloseOutcome`, `AdapterDescription`,
   `RecordIdentityRequest`, and `PartialCloseScope` — are registered
   on `TwigJsonContext` per `local://core-seams.md`, declared in the
   `Twig.Infrastructure.Persistence.Transport` namespace, and MUST NOT
   be referenced by any ADO projection namespace's dependency graph.
   No `IChangeSink.SubmitAsync` or `IPlanLifecycleService.Apply*`
   overload accepts a transport type.
2. **Call-graph architecture test (runtime-invariant).** A conformance
   test walks the outgoing call graph from every ADO
   payload-builder / mutation entry point (`IChangeSink.SubmitAsync`,
   `IPlanLifecycleService.Apply*`, every ADO REST DTO constructor
   named on `local://core-seams.md`) and asserts that no node in the
   reachable graph — regardless of type — is defined in
   `Twig.Infrastructure.Persistence.Transport`, `Twig.Application.Transport`,
   or reads a file matching `.twig/transport.json`. This catches a
   scalar-through-a-string leak that the namespace seam cannot see.
3. **ADO DTO typed construction rule.** Every ADO field / link /
   comment payload is constructed through a typed builder whose
   parameter types are enumerated in a compile-time allow-list; a
   generic `object`- or `string`-typed passthrough into an ADO DTO
   builder is a build error. A caller that reads a transport scalar
   still cannot reach ADO because no builder accepts an
   opaque-provenance string.

Any accidental serialization into an ADO payload raises
`Result.Fail("transport-ado-projection-forbidden")` at the ADO client
boundary — the runtime backstop for the three rails above.

### 8.4 Atomic write, CAS, and revision preservation

`transport.json` inherits the AB#736 §6.1 atomic-write obligation
verbatim: write to a sibling temp under `<worktreeRoot>/.twig/tmp/`,
`fsync(2)`, then `rename(2)` into place. Every mutation runs inside a
`TransportAttachmentStore` that:

- serializes writes with an in-process semaphore plus an exclusive
  `transport.json.lock` (`FileShare.None`), matching
  `WorktreeLocalAttachmentStore`;
- rereads and revalidates the envelope + record under the lock;
- accepts an `expectedRevision` CAS token and rejects mismatches with
  `Result.Fail("transport-version-mismatch")`;
- verifies `connectionRef` equals the live `twig.json` connection ref
  and rejects drift with `Result.Fail("transport-connection-mismatch")`;
- verifies `record.worktree.worktreeFingerprint` byte-equals the
  current worktree fingerprint and rejects drift with
  `Result.Fail("transport-worktree-fingerprint-mismatch")`;
- maps any OS-level failure of the temp write, `fsync`, or `rename`
  onto `Result.Fail("transport-atomic-write-failed")`. This is the
  single named identifier for atomic I/O failure; storage layers
  never surface an unnamed exception.

The store surface is:

```
TransportAttachmentStore:
    ReadWithRevision(worktreeAnchor) -> Result<{ envelope: Envelope, revision: int }>
    Write(worktreeAnchor, newRecord: Record, expectedRevision: int)   -> Result<{ writtenRevision: int }>
    Detach(worktreeAnchor, expectedRevision: int)                     -> Result<{ writtenRevision: int }>
    Close(worktreeAnchor, expectedRevision: int)                      -> Result<{ writtenRevision: int }>
```

`ReadWithRevision` returns the envelope and the current CAS revision
(0 when the file does not exist). `Write` mutates from
`state = "detached"` or from a never-existent file into
`state = "attached"`, or replaces one attached record with another;
either way `revision` increments by 1. `Detach` and `Close` both write
a `state = "detached"` tombstone with `revision + 1`; on a
never-existent file both are no-ops that still assert
`expectedRevision = 0` and return `writtenRevision = 0`. Every
returned `writtenRevision` is the new CAS token for the next call.

`Result<T>` return values follow the `local://core-seams.md` Result
convention; every failure carries a §11 identifier string, never an
exception.

### 8.5 Settled and deferred

Settled: worktree-local `transport.json`, envelope layout with
`state`+`revision`+`record`, tombstone-preserved revision across
detach + reattach, `ReadWithRevision` / CAS `Write` / CAS `Detach` /
CAS `Close` surface, atomic-write model inherited from AB#736 mapped
onto `transport-atomic-write-failed`, and the three-rail Twig-local
serialization boundary. Deferred: system-store cross-worktree transport
index and tombstone compaction — deferred to the follow-up
creation/management sub-spec, unblocked only if a use case demonstrates
a cross-worktree recovery need.

## 9. No-authority boundary

### 9.1 Verbs rejected — enumerable conformance list

Spec AB#730 fixes that Transport Attachment has no workflow authority.
The following list is the **complete** set of verbs a Transport
Attachment MUST NOT trigger, phrased so a conformance test can assert
against each row. The list is exhaustive over the union of the two
shipping-host surfaces documented in `local://host-surfaces.md`;
Herdr's management verbs (`focus`, `rename`, `resize`/`zoom`,
`prompt`, `start`) each appear as their own row per §1.1(b)'s
"observe-only, no management" rule.

| # | Rejected verb | Justification |
|---|---|---|
| R1 | Claim mint / activate / release / retire | Claim lifecycle is AB#737. No transport field is an input. |
| R2 | Change Proposal state transition (submit / accept / reject) | Owned by plan lifecycle. §10 restricts transport to rendering only. |
| R3 | Plan validate / preview / apply / status | Owned by `IPlanLifecycleService`; transport never enters the digest. |
| R4 | ADO work-item state move (`System.State` mutation) | ADO mutation is projection-only, never transport-driven. |
| R5 | ADO field update | §8.3 forbids projection of any transport field into ADO. |
| R6 | ADO link add / remove | Same as R5. |
| R7 | ADO comment / note publication | Same as R5. |
| R8 | Session-steering-mode derivation | §9.2 makes this explicit. |
| R9 | Attach / detach / retire of the AB#738 primary-scope attachment | Primary-scope attachment lifecycle is orthogonal. |
| R10 | Managed-worktree init or reinit | AB#736 §6.3. |
| R11 | Adapter creation of a host workspace / tab / pane / agent session / terminal window | Creation is the deferred MAP AB#726 decision (§1, §1.1(a)). |
| R12 | Adapter focus / bring-to-front (Herdr `focus`) | Management surface; §1.1(b). |
| R13 | Adapter rename of any host object (Herdr `rename`) | Management surface; §1.1(b). |
| R14 | Adapter move / layout / resize / zoom (Herdr `resize`, `zoom`, and any layout mutation) | Management surface; §1.1(b). |
| R15 | Adapter prompt / start / spawn of an agent turn or process (Herdr `prompt`, `start`, or any equivalent) | Management surface; §1.1(b). Distinct from R11 because these mutate an existing host object rather than create a new one. |

A conformance test enumerates every row R1–R15 and asserts two
independent invariants:

- **Field-reference invariant.** No `TransportAttachmentRecord` field
  (or any nested type field: `TransportAdapterTarget`,
  `TransportStatusObservation`, `TransportLivenessObservation`,
  `TransportPartialCloseOutcome`, `AdapterDescription`) appears in any
  call path implementing the verb, either as input or as branch
  predicate. This is a reachable-types walk.
- **Outcome / event-boundary invariant.** No transport operation
  (`ReportStatus`, `ProbeLiveness`, `Detach`, `Close`, `PartialClose`,
  `RecordIdentity`, `SelectPresentation`, `Render`, the `Read` and
  `Write` methods on `TransportAttachmentStore`) and no transport
  observation (`TransportStatusObservation`,
  `TransportLivenessObservation`, `TransportPartialCloseOutcome`) —
  as a return value, exception, event, or callback — appears in the
  reachable event/call graph of any R1–R15 entry point. This is a
  reachable-events walk: it forbids a `Close.OnSuccess` handler from
  triggering an R1 claim retirement or an R2 Change Proposal
  submission, and it forbids a probe callback from raising an event a
  steering-mode derivation subscribes to.

The two invariants are independent conformance tests; both must pass.
The matrix asserts each row against each invariant, R1–R15 × 2 = 30
distinct assertions.

**§1.1(c) reverse invariant.** A separate, third conformance
assertion completes the observe-only guarantee: the event-boundary
invariant applied in reverse. It walks the reachable event/call graph
from every **non-close** transport entry point — `ReportStatus`,
`ProbeLiveness`, `Detach`, `RecordIdentity`,
`TransportAttachmentStore.ReadWithRevision`,
`TransportAttachmentStore.Write`, the §2.2 shape validator, the §10
rendering selection function `SelectPresentation`, the §10 `Render`
function, every §5.3 optional-cache reuse path, and every
adapter internal retry / cleanup / sweep / error-recovery path — and
asserts that `Close` and `PartialClose` are unreachable from any of
them. This is the assertion that operationalizes §1.1(c)'s ban on
implicit host mutation.

### 9.2 Session-steering-mode isolation

No field of a Transport Attachment may be consulted when deriving
session-steering mode. Specifically:

- `agent.recordedStatus`, `agent.sessionKind`,
  `terminal.target.hostAttachmentIdKind`, and the entire `capabilities`
  set are OFF-LIMITS to steering-mode derivation.
- Steering-mode derivation code paths MUST NOT `using` or otherwise
  reference the `Twig.Infrastructure.Persistence.Transport` namespace.
  The compile-time seam is the same as §8.3.

Violation is caught by the same conformance-test walk in §9.1: the
event-boundary invariant applied to R8 fails if any transport
outcome reaches steering-mode derivation.

### 9.3 Settled and deferred

Settled: the R1–R15 rejection list, the compile-time seam, the
event-boundary invariant, the §1.1(c) reverse invariant, and the
R1–R15 × 2 + §1.1(c) reverse conformance-matrix enumeration. Deferred:
none — this list is exhaustive at v1 and any addition is a schema
change to this document.

## 10. Change Proposal rendering integration

### 10.1 The single integration point

Change Proposal rendering is the **only workflow-domain consumer** that
reads Transport Attachment data. The validator, `TransportAttachmentStore`,
adapter dispatch, probes, detach, close, and the conformance tests all
read the record too; the earlier "reads the record at all" wording
overstated the constraint and is corrected here. The workflow-domain
constraint is what §9 protects: rendering is the only workflow-domain
call path that ingests transport data, and it does so only to pick a
presentation.

The integration point is a selection function plus a render function:

```
ChangeProposalRenderer.SelectPresentation(
    proposal:         ChangeProposalRenderProposal,
    transportRecord:  TransportAttachmentRecord | null   # null and any read error are handled per §10.2
) -> Presentation

ChangeProposalRenderer.Render(
    proposal:         ChangeProposalRenderProposal,
    presentation:     Presentation
) -> RenderedProposal
```

The renderer calls `SelectPresentation` once per proposal render, then
`Render` on the returned presentation. No other consumer reads the
record for rendering purposes.

### 10.2 Rendering-selection rule and DTOs

The DTOs consumed here are declared in this contract so AB#745 can
implement the selection function without waiting on the Change Proposal
design:

```
ChangeProposalRenderProposal = {
  proposalId:       string,                # opaque identifier owned by the Change Proposal design
  content:          <opaque payload>,      # every renderer receives the same payload; content shape
                                           # is owned by the Change Proposal design and is not
                                           # inspected here beyond passthrough
  metadata:         Map<string, string>    # opaque diagnostics-only annotations
}

Presentation =
    | TerminalTextPresentation
    | RichAdapterPresentation { adapterId: RichAdapterId }

RichAdapterId = { adapterId: string, role: "agent" | "terminal" }

RenderedProposal = {
  presentationKind:  "terminal-text" | "rich-adapter",
  adapterId:         string | null,        # non-null only when presentationKind = "rich-adapter"
  ...                                      # visible output shape owned by the terminal / rich renderer
}
```

Whether *this* renderer build knows how to invoke *that* adapter is a
**renderer-side** predicate distinct from a §3.3 capability declaration.
It is decided by a deterministic registry:

```
IChangeProposalPresentationSupportRegistry:
    IsSupported(RichAdapterId) -> bool
    RegisteredRichAdapters()   -> RichAdapterId[]     # for diagnostics and conformance tests
```

The registry is populated at DI composition time from a compile-time
list; nothing is reflective, nothing runs I/O, and `IsSupported` is
pure. AB#745 registers exactly one entry — the terminal/text fallback
support — as a baseline, and downstream renderer builds add rich-adapter
entries when they implement them.

Precedence, top-down; the first satisfied clause wins:

1. `transportRecord` is `null`, unreadable (any `TransportAttachmentStore.ReadWithRevision` `Result.Fail`), or its adapter is unregistered → `TerminalTextPresentation`.
2. `transportRecord.record.agent` is present and `IsSupported({ adapterId = transportRecord.record.agent.target.adapterId, role = "agent" })` returns true → `RichAdapterPresentation { RichAdapterId { adapterId, role = "agent" } }`.
3. `transportRecord.record.terminal` is present and `IsSupported({ adapterId = transportRecord.record.terminal.target.adapterId, role = "terminal" })` returns true → `RichAdapterPresentation { RichAdapterId { adapterId, role = "terminal" } }`.
4. Otherwise → `TerminalTextPresentation`.

The rendering-selection rule therefore consults transport **only** to
pick a rendering, matching Spec AB#730's mandate.

### 10.3 Rich-render refusal at invocation time — unconditional fallback

Selection precedes invocation. The universal fallback guarantee holds
even when a rich renderer selected in §10.2 subsequently refuses or
fails; the guarantee is enforced at both boundaries.

`Render(proposal, presentation)` runs the following, in order:

1. If `presentation` is `TerminalTextPresentation`, render the proposal
   through the terminal/text renderer and return.
2. If `presentation` is `RichAdapterPresentation`:
   - a. If the registered rich renderer for `presentation.adapterId` is
     unavailable at invocation time (not registered, throws on
     construction, or fails its own precondition check) → render the
     **unchanged** proposal through the terminal/text renderer and
     return.
   - b. Invoke the rich renderer. If it returns a "refused" outcome
     (renderer-defined explicit refusal), throws, or times out under
     its own contract-defined budget → render the **unchanged**
     proposal through the terminal/text renderer and return.
   - c. Otherwise return the rich renderer's `RenderedProposal`.

The terminal/text renderer is guaranteed available in every build
because AB#745 registers it. No case in step 2 alters `proposal.content`;
the fallback renders the same proposal payload the rich path attempted.
Neither step 2a nor step 2b nor step 2c may reach `Close` or
`PartialClose` (§1.1(c) reverse invariant).

A conformance case (`RichRenderersAllDecline`) constructs a build where
every registered rich renderer for the presentation returns "refused"
(or throws) and asserts that `Render` produces a
`RenderedProposal { presentationKind = "terminal-text" }` with the
proposal's content preserved.

### 10.4 Universal terminal/text fallback and authorization-neutrality

The `TerminalTextPresentation` MUST remain unconditional:

- it renders every proposal in full;
- it is available on every host, including hosts with no Transport
  Attachment;
- it is functionally identical (fields, semantics, keystrokes) to what
  a `RichAdapterPresentation` would show;
- it is the presentation used by every authorization decision. Any
  approval, digest confirmation, or state-transition gate MUST branch
  against the same proposal the terminal/text fallback exposes;
  branching against a rich-adapter-only field is forbidden.

This closes AB#730's authorization-neutrality mandate: transport
selects appearance, never authority.

### 10.5 Settled and deferred

Settled: the single integration point, the four-clause selection rule,
the `ChangeProposalRenderProposal` / `Presentation` /
`RichAdapterId` / `RenderedProposal` DTOs, the
`IChangeProposalPresentationSupportRegistry` predicate,
`Render`-time refusal fallback with the `RichRenderersAllDecline`
conformance case, and the universal terminal/text fallback. Deferred:
the concrete content shape inside `ChangeProposalRenderProposal.content`
— owned by the Change Proposal design, out of scope here; the rendering
integration is decoupled from that decision.

## 11. Failure identifiers

Every failure this contract introduces, consolidated. Identifiers are
kebab-case string constants declared in `TransportAttachmentFailure`,
matching the `AttachmentStorageFailure` pattern from
`local://core-seams.md` and AB#736 §8. The "Result shell" column
records whether the failure is a `Result.Fail(identifier)` the caller
branches on (dispatch-level), or an identifier embedded inside a
`Result.Ok(observation)` (bounded-observation-level). Every identifier
is raised by at least one section of this document and each row cites
the section that raises it.

| Identifier | Result shell | Raised by | Trigger |
|---|---|---|---|
| `transport-record-invalid` | `Result.Fail` | §2.2 (row 1), §8.4 | Envelope / record schema parse failure. |
| `transport-worktree-missing` | `Result.Fail` | §2.2 (row 2) | Shape validator: `record.worktree` field absent while `state = "attached"`. |
| `transport-bare-worktree` | `Result.Fail` | §2.2 (row 3) | Shape validator: only `worktree` set, both `agent` and `terminal` `null`. |
| `transport-orphan-terminal` | `Result.Fail` | §2.2 (row 4) | Shape validator: record fits neither §2.2 shape row. |
| `transport-unknown-status` | `Result.Fail` | §2.2 (row 5) | Shape validator: `agent.recordedStatus` outside §4.1. |
| `transport-unknown-capability` | `Result.Fail` | §2.1, §2.2 (row 6) | Shape validator: capability name outside §3.3's optional catalogue, including a common-denominator name (`RecordIdentity` / `DescribeAdapter`) appearing in a persisted set. |
| `transport-connection-mismatch` | `Result.Fail` | §8.4 | `transport.json.envelope.connectionRef` disagrees with current `twig.json`. |
| `transport-worktree-fingerprint-mismatch` | `Result.Fail` | §2.1, §8.4 | `record.worktree.worktreeFingerprint` disagrees with the live §3.2 tuple. |
| `transport-version-mismatch` | `Result.Fail` | §8.4 | CAS: `expectedRevision` disagrees with the on-disk envelope revision. |
| `transport-adapter-not-registered` | `Result.Fail` | §7.2, §7.3 | Record's `adapterId` not in the registry. |
| `transport-capability-not-declared` | `Result.Fail` | §3.2, §7.1 | Caller invoked an operation for a capability name not in the §3.3 catalogue (client-bug rail). Never raised for one of the five §3.3 capabilities themselves — those degrade per §3.2. |
| `transport-probe-timeout` | `Result.Ok(observation)` (embedded `error` / `timeoutError`) | §5.2 | `StatusReporting` or `LivenessProbe` exceeded its (possibly caller-overridden) timeout; the adapter returned its bounded-failure observation. |
| `transport-probe-budget-invalid` | `Result.Fail` | §5.1 | Caller-supplied `timeoutMs` outside `[100, 30000]` ms. |
| `transport-probe-adapter-failed` | `Result.Fail` | §5.2 | Adapter's declared `StatusReporting` or `LivenessProbe` could not produce a bounded observation (adapter code threw / host command failed for a non-timeout reason). |
| `transport-detach-adapter-failed` | `Result.Fail` | §6.1 | Adapter's declared `Detach` returned a failure. Core still writes the detach tombstone. |
| `transport-close-not-supported` | `Result.Fail` | §3.2, §6.2 | Core dispatch: `Close` invoked on an adapter that did not declare it. |
| `transport-close-adapter-failed` | `Result.Fail` | §6.2 | Adapter's declared `Close` returned a failure. |
| `transport-partial-close-not-supported` | `Result.Fail` | §3.2, §6.3 | Core dispatch: `PartialClose` invoked on an adapter that did not declare it. |
| `transport-partial-close-adapter-failed` | `Result.Fail` | §6.3 | Adapter's declared `PartialClose` could not produce a bounded outcome. |
| `transport-ado-projection-forbidden` | `Result.Fail` | §8.3 | ADO client boundary: a transport type reached ADO serialization (runtime backstop for the three §8.3 rails). |
| `transport-atomic-write-failed` | `Result.Fail` | §8.4 | Temp write, `fsync`, or `rename` failed for `transport.json` (single mapping for every atomic I/O failure). |

Every identifier is stable across releases; adding a new one is a
schema change to this document. Storage and adapter surfaces never
return an unnamed error to AB#745 / AB#746 / AB#747.

## 12. What AB#745, AB#746 and AB#747 each implement

### 12.1 AB#745 — core seam

Implements:

- `TransportAttachmentRecord`, the envelope, every nested type
  (`TransportAdapterTarget`, `TransportStatusObservation`,
  `TransportLivenessObservation`, `TransportPartialCloseOutcome`,
  `AdapterDescription`, `RecordIdentityRequest`, `PartialCloseScope`,
  `Presentation` and its variants, `RichAdapterId`,
  `ChangeProposalRenderProposal`, `RenderedProposal`) per §2.1, §7.4,
  and §10.2, registered on `TwigJsonContext`.
- The §2.2 shape validator returning the six named rejection
  identifiers in the fixed order.
- `TransportAttachmentStore` per §8.4: envelope + revision-CAS,
  tombstone on detach/close, atomic write mapped onto
  `transport-atomic-write-failed`, connectionRef and
  worktreeFingerprint verification, all named failure paths.
- `ITransportAdapter` interface per §7.1 and
  `ITransportAdapterRegistry` per §7.2, wired through
  `TwigServiceRegistration` following the `AddConnectionServices`
  factory-lambda pattern.
- The mandatory null adapter (`adapterId = "null"`) per §7.3, with the
  single valid direct-human recorded shape.
- The core dispatch layer that applies the per-operation §3.2
  degradation for undeclared optional capabilities and raises
  `transport-capability-not-declared` only for a name outside the
  §3.3 optional catalogue.
- Bounded probe budget with clamp per §5.1, `transport-probe-timeout`
  embedded observation per §5.2, `transport-probe-adapter-failed`
  dispatch failure per §5.2, and freshness computation per §5.3
  including the adapter-owned cache abstraction the Herdr adapter
  populates.
- The R1–R15 conformance test per §9.1 (field-reference invariant AND
  event-boundary invariant, 30 assertions), plus the §1.1(c) reverse
  invariant that walks every non-close transport entry point and
  asserts `Close`/`PartialClose` unreachable, the compile-time seam
  test per §8.3 and §9.2, the §8.3 call-graph architecture test, and
  the §8.3 ADO DTO typed-construction check.
- `ChangeProposalRenderer.SelectPresentation` and
  `ChangeProposalRenderer.Render` per §10.1–§10.3 with the four-clause
  selection rule, the `IChangeProposalPresentationSupportRegistry`
  predicate, the invocation-time refusal fallback, the
  `RichRenderersAllDecline` conformance case, and the universal
  terminal/text fallback.
- `TransportAttachmentFailure` string-constant table per §11.

MUST NOT implement:

- Any host adapter (deferred to AB#746 / AB#747).
- Any creation/management verb (deferred per §1, §1.1(a), §1.1(b)).
- Any status-vocabulary extension (deferred per §4.4).
- Any Change Proposal content shape inside
  `ChangeProposalRenderProposal.content` (owned by the Change Proposal
  design per §10.5).

### 12.2 AB#746 — Herdr adapter

Implements:

- `ITransportAdapter` with `AdapterId = "herdr"` and the declared
  `Capabilities = { StatusReporting, LivenessProbe, Detach, Close, PartialClose }`.
  `RecordIdentity` and `DescribeAdapter` are common-denominator and
  are NOT members of the `Capabilities` set (§3.1).
- The concrete Herdr status mapping per §4.2 (`idle` →
  `idle-ambiguous`, never to `done`).
- `StatusReporting` implemented as a bounded query against
  `herdr api snapshot` (or `herdr pane current --current` /
  `herdr agent explain <target> --json` where the pane- or
  agent-scoped view is required) under the §5.1 500 ms budget. An
  in-process cache reused per §5.3 is permitted as an optimisation
  but not mandated. NO subscription, dedicated thread, reconnect
  loop, or broker event handler is used — `herdr api` exposes only
  `snapshot` and `schema`, and Herdr's only blocking primitive is
  `herdr agent wait <target> --until <state> --timeout <ms>`, which
  MUST always be passed `--timeout` because omission blocks
  indefinitely (§5.1). Herdr's grounded observation mechanisms are
  therefore exactly: `herdr api snapshot`, `herdr pane current
  --current`, `herdr agent explain <target> --json`, and
  `herdr agent wait --until <state> --timeout <ms>`; polling and
  bounded blocking waits are the only options.
- `LivenessProbe` implemented against `herdr pane current` /
  `herdr agent explain`, honouring the §5.1 budget and NEVER using
  the indefinite-when-omitted `agent wait` / `pane wait-output`
  timeouts. Timeouts return `transport-probe-timeout` embedded;
  non-timeout adapter failures return
  `transport-probe-adapter-failed`.
- `Close` implemented as exactly one unpiped
  `herdr tab close <tab_id>` (or `herdr pane close <pane_id>` for the
  pane variant), with the AGENTS.md preflight cross-check on
  workspace/tab/pane IDs — populated via `TransportAdapterTarget.adapterContext`
  per §7.4 — against live records before issuing. Reachable only via
  explicit caller invocation (§1.1(c), §6.2).
- `PartialClose` scoped by `PartialCloseScope` per §7.4, with
  `observedRemaining` populated only when the adapter can independently
  confirm via `pane list`; MUST return `observedRemaining = unknown`
  when the confirmation is unavailable, per §6.3. Reachable only via
  explicit caller invocation (§1.1(c), §6.3).
- `Detach` as a Twig-side cache drop for the `hostAttachmentId`
  (including any §5.3 optional-cache observation entry the adapter
  chose to keep for this target). MUST NOT reach `Close` or
  `PartialClose` on any path (§1.1(c), §6.1).

MUST NOT implement:

- Creation of any workspace/tab/pane/agent session (R11, §1.1(a)).
- Any host management: focus (R12), rename (R13),
  resize/zoom/layout (R14), prompt / start (R15) — §1.1(b).
- A `LifecycleFacets` capability beyond the six-value core
  vocabulary (deferred per §4.4).
- Any inference from `idle` to `done` (§4.3).
- Any indefinite-blocking read (§5.1).
- Any post-`PartialClose` compensating `Close` on
  `observedRemaining = unknown` (§6.3).
- Any implicit `Close` or `PartialClose` invocation from a probe,
  read, detach, cache reuse, retry, cleanup, sweep, or
  error-recovery path (§1.1(c)).

### 12.3 AB#747 — Windows Terminal adapter

Implements:

- `ITransportAdapter` with `AdapterId = "windows-terminal"` and
  `Capabilities = { }` (empty optional set — the common denominator
  only, per §3.1).
- `RecordIdentity` echoing the caller-supplied `hostAttachmentId`
  (integer or name of the `--window` target, per
  `local://host-surfaces.md` §Identity), normalized per §7.4 into a
  decimal string (integer path) or the caller's exact string (named
  path), with the corresponding `hostAttachmentIdKind`.

MUST NOT implement:

- `StatusReporting`, `LivenessProbe`, `Close`, or `PartialClose`.
  `local://host-surfaces.md` establishes these surfaces do not exist
  in Windows Terminal; declaring them would violate §3.4. In
  particular, `LivenessProbe` MUST NOT attempt to probe existence by
  sending a `wt.exe --window <id>` command, because a nonexistent
  window ID silently creates a new window instead of failing —
  making the "probe" a destructive side effect.
- Any discovery, enumeration, or listing of existing
  windows/tabs/panes.
- Any host action beyond what a caller explicitly recorded — creation
  and management remain the deferred surface (§1, §1.1(a), §1.1(b),
  R11–R15).
- Any OS-level window/process inspection (out of contract per
  `local://host-surfaces.md`).

Callers of a Windows Terminal Transport Attachment receive
`Result.Ok(TransportStatusObservation { status = unobservable, ... })`
for status per §3.2, `Result.Ok(TransportLivenessObservation { presence = unknown, ..., freshness = unobservable })`
for liveness per §3.2, and
`Result.Fail("transport-close-not-supported")` /
`Result.Fail("transport-partial-close-not-supported")` for close
operations per §3.2. This is the settled degradation, not a gap.
