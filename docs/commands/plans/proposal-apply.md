---
command: proposal apply
group: plans
summary: Apply a proposal after digest confirmation.
stability: stable
mutates: ado
---

# `twig proposal apply`

The only command in the plans group that writes to Azure DevOps. Apply
consumes executable proposal v1 files, verifies the caller-supplied digest
matches the current file digest exactly, and then runs each declared operation
as a journalled row. A valid
v2 proposal is refused before checking the supplied confirmation digest,
journal access, pending-state reads, or provider calls.

## Synopsis

```
twig proposal apply --file <path> --confirm <digest>
                    [-o human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--file`|string|_none_|Path to the proposal JSON file. Must resolve inside the current workspace root.|
|`--confirm`|string|_none_|Lowercase-hex SHA-256 digest of the canonical proposal bytes. Must match exactly.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `IPlanLifecycleService.ApplyAsync`
(`src/Twig/Commands/PlanCommand.cs`). The caller-supplied digest is compared
against the canonical digest recomputed from the current proposal file before
the operation journal is confirmed or any provider mutation runs.

- **Digest is a hard gate.** `--confirm` is required at the command layer;
  a mismatch against the current file digest is a lifecycle failure — you
  must re-preview (which recomputes the digest) and pass the new value.
- **Version 2 is not executable.** Regardless of the supplied confirmation
  digest, the lifecycle refuses it before digest comparison, journal lookup or mutation,
  pending-state inspection, or provider access. No v1
  integer identity or revision is synthesized.
- **Digest confirmation is required.** A digest mismatch returns exit 1;
  pass the exact digest from the preview of the current proposal.
- **Review-page Apply remains human-only.** The browser review page's explicit
  Apply action uses the digest from its retained review. The SDK exposes no
  Apply action and the native lifecycle refuses a changed digest.
- **Journal writes are the source of truth.** Every declared operation gets
  exactly one journal row keyed by digest and ordinal
  (`src/Twig.Domain/Services/Plan/PlanJournalOperation.cs:4-11`). Per-row
  failures land on `PlanJournalOperation.Error`; a terminal-level failure
  lands on `PlanApplyResult.Error`
  (`src/Twig.Domain/Services/Plan/PlanApplyResult.cs:14-26`).
- **Seed-publish readback never retries creation.** After an uncertain publish,
  readback may query for the existing immutable publish marker and finish local
  mapping only when the positive evidence and fetched item agree. It does not
  issue another create or perform remote link promotion or cleanup. Missing,
  ambiguous, or inconsistent evidence remains unresolved; it is not proof that
  the create was rejected or never happened.
- **All-or-nothing exit.** Exit 0 requires every operation to reach the
  Verified terminal state; any failed row returns exit 1.
- **Acknowledgment is not lost when a journal fence loses.** If the remote effect
  was acknowledged but another actor advanced its journal row without matching
  acknowledgment evidence, apply stops with exit 1 and
  `acknowledged-effect-unsettled`. The winning row is not overwritten. JSON adds
  `unsettledAcknowledgments`, keyed by operation ID with the acknowledgment JSON
  as a string; these are transient attempt results, not journal rows or settlement
  receipts. The existing native remote-write history retains the durable response.
  Do not retry the mutation: inspect that history and reconcile the winning row.
  Re-running apply does not reproduce the original transient acknowledgment.
  Malformed winning evidence cannot settle an acknowledgment. If the current
  journal is unavailable, the result explicitly reports that fact and returns no
  current operation rows, while retaining the acknowledged attempt evidence.
  A matching Applied or Verified winning receipt continues the existing verify-only
  resume path without another mutation.

## Examples

Human-in-the-loop apply after a fresh preview:

```console
$ twig proposal preview --file .twig/proposals/close-1234.json
proposal: digest=3f9c…a1b7  canApply=true
$ twig proposal apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7
proposal: applied  ops=3 verified
```

Agent-scripted apply emitting per-operation journal outcomes as JSON:

```console
$ twig proposal apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7 -o json
{ "digest": "3f9c…a1b7", "failed": false, "operations": [ /* rows */ ] }
```

### Per-operation `diagnostics`

Each row in `operations` carries an additive bounded `diagnostics` object beside
the raw `result`, `warning`, and `error` keys — see
[`proposal status` – Per-operation `diagnostics`](proposal-status.md#per-operation-diagnostics)
for the shape and vocabularies. The human and minimal surfaces render the
bounded disposition and summary and never echo the raw `warning`/`error` bodies;
`-o json` remains the full-evidence route (AB#881).

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Every operation reached the Verified terminal state.|`0`|
|One or more operations failed, or the digest did not match.|`1`|
|`--file` or `--confirm` omitted.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — always run first; it recomputes the digest you must confirm.
- [`proposal status`](proposal-status.md) — inspect the journal that this command writes.
- [`proposal validate`](proposal-validate.md) — cheap digest check.
- [`plan apply`](plan-apply.md) — deprecated alias.
