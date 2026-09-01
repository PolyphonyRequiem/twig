# Plans — proposal lifecycle and pending changes

The **plans** group covers everything on the change-proposal path: the five
canonical `proposal <verb>` commands that validate, preview, apply, inspect,
and describe a proposal v1 file, plus `pending` — the read-only dump of every
staged pending change. A proposal is an immutable JSON declaration; state
lives in a per-workspace **journal** keyed on the proposal's canonical SHA-256
digest, and every board mutation that flows through this path is auditable by
that digest.

## Canonical vs. legacy verbs

Every command in this group has both a canonical name and a retained
deprecated alias:

|Canonical (use this)|Deprecated alias (still works)|
|---|---|
|`twig proposal validate`|`twig plan validate`|
|`twig proposal preview`|`twig plan preview`|
|`twig proposal apply`|`twig plan apply`|
|`twig proposal status`|`twig plan status`|
|`twig proposal seed`|`twig plan seed`|

The two forms share one handler, one help block, one exit contract, and one
underlying `IPlanLifecycleService`. The rename to **proposal** — from the
older **plan** — is a naming cutover, not a behavior change: existing scripts
using `twig plan …` continue to run unchanged, and both verbs are registered
in the same grouped help block so operators discover the pairing at first
`twig --help`. The legacy pages below exist so that a search for `plan apply`
still lands on documented behavior, and each one points to its canonical
sibling.

See `src/Twig/Program.cs:1354-1392` for the routing table:
`[Command("proposal apply|plan apply")]` and its four siblings dispatch to
`PlanCommand`, whose XML doc header names both surfaces explicitly
(`src/Twig/Commands/PlanCommand.cs:12-19`).

## Digest confirmation and the journal

`twig proposal apply` refuses to run without `--confirm <digest>`. The digest
is the lowercase-hex SHA-256 over the canonical bytes of the proposal file
itself; `proposal validate` and `proposal preview` both compute and report
it. Apply compares the confirmed digest against the current file digest and
fails closed on any mismatch — the digest a caller supplies is what they
signed off, and re-editing the file between preview and apply invalidates
the confirmation.

The **journal** is the durable per-workspace record of what a given proposal
did. Each row is keyed by digest, carries per-operation lifecycle states
(`src/Twig.Domain/Services/Plan/PlanOperationState.cs`), and is the only
place where proposal execution state lives — the proposal file itself is
declarative and stateless
(`src/Twig.Domain/Services/Plan/PlanDefinition.cs:4-8`). `proposal status`
reads this row; `proposal apply` writes it; `proposal preview` imports it
without mutating ADO. When a proposal reaches the per-operation loop, per-row
errors land on `PlanJournalOperation.Error`; a terminal-level failure lands
on the row's `Error` field
(`src/Twig.Domain/Services/Plan/PlanApplyResult.cs:14-26`).

## Command index

|Command|Summary|Mutates|
|---|---|---|
|[`proposal validate`](proposal-validate.md)|Validate a proposal v1 file; no ADO calls.|none|
|[`proposal preview`](proposal-preview.md)|Import journal, snapshot pending, report digest and `canApply`.|local|
|[`proposal apply`](proposal-apply.md)|Apply a proposal after digest confirmation and authorization.|ado|
|[`proposal status`](proposal-status.md)|Show journal state for a proposal file.|none|
|[`proposal seed`](proposal-seed.md)|Describe a staged seed for proposal authoring.|none|
|[`plan validate`](plan-validate.md)|Deprecated alias for `proposal validate`.|none|
|[`plan preview`](plan-preview.md)|Deprecated alias for `proposal preview`.|local|
|[`plan apply`](plan-apply.md)|Deprecated alias for `proposal apply`.|ado|
|[`plan status`](plan-status.md)|Deprecated alias for `proposal status`.|none|
|[`plan seed`](plan-seed.md)|Deprecated alias for `proposal seed`.|none|
|[`pending`](pending.md)|List raw staged pending changes in exact staging order.|none|
