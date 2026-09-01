---
command: proposal apply
group: plans
summary: Apply a proposal after digest confirmation and identity authorization.
stability: stable
mutates: ado
---

# `twig proposal apply`

The only command in the plans group that writes to Azure DevOps. Apply
consumes a proposal v1 file, verifies the caller-supplied digest matches
the current file digest exactly, verifies an identity has authorized the
apply, and then runs each declared operation as a journalled row.

## Synopsis

```
twig proposal apply --file <path> --confirm <digest>
                    [--authorize <identity>] [--rationale <text>]
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
|`--file`|string|_none_|Path to the proposal v1 JSON file. Must resolve inside the current workspace root.|
|`--confirm`|string|_none_|Lowercase-hex SHA-256 digest of the canonical proposal bytes. Must match exactly.|
|`--authorize`|string|_none_|Identity authorizing this apply. Recorded in the journal audit trail; without it the apply is refused.|
|`--rationale`|string|_none_|Optional reason for authorizing this apply, recorded alongside the authorization.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `IPlanLifecycleService.ApplyAsync`
(`src/Twig/Commands/PlanCommand.cs:92-129`). The command layer builds a
`ProposalAuthorization` record binding the confirmed digest, the resolved
authorization mode (from the session steering seam), the authorizer
identity, an optional rationale, and the current UTC timestamp
(`src/Twig/Commands/PlanCommand.cs:115-124`).

- **Digest is a hard gate.** `--confirm` is required at the command layer;
  a mismatch against the current file digest is a lifecycle failure — you
  must re-preview (which recomputes the digest) and pass the new value.
- **Authorization is required, but its absence is not a usage error.** A
  missing `--authorize` still returns exit 1, not 2: the gate resolves the
  required mode from the session (HITL vs. AFK) and refuses with the reason
  the session actually had (`src/Twig/Commands/PlanCommand.cs:85-91`).
- **Journal writes are the source of truth.** Every declared operation gets
  exactly one journal row keyed by digest and ordinal
  (`src/Twig.Domain/Services/Plan/PlanJournalOperation.cs:4-11`). Per-row
  failures land on `PlanJournalOperation.Error`; a terminal-level failure
  lands on `PlanApplyResult.Error`
  (`src/Twig.Domain/Services/Plan/PlanApplyResult.cs:14-26`).
- **All-or-nothing exit.** Exit 0 requires every operation to reach the
  Verified terminal state; any failed row returns exit 1.

## Examples

Human-in-the-loop apply after a fresh preview:

```console
$ twig proposal preview --file .twig/proposals/close-1234.json
proposal: digest=3f9c…a1b7  canApply=true
$ twig proposal apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7 \
      --authorize "Daniel Green" \
      --rationale "Closing AB#1234 after tests green"
proposal: applied  ops=3 verified
```

Agent-scripted apply emitting per-operation journal outcomes as JSON:

```console
$ twig proposal apply --file .twig/proposals/close-1234.json \
      --confirm 3f9c…a1b7 --authorize wayfinder-bot -o json
{ "digest": "3f9c…a1b7", "failed": false, "operations": [ /* rows */ ] }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Every operation reached the Verified terminal state.|`0`|
|One or more operations failed, digest did not match, or authorization gate refused.|`1`|
|`--file` or `--confirm` omitted.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — always run first; it recomputes the digest you must confirm.
- [`proposal status`](proposal-status.md) — inspect the journal that this command writes.
- [`proposal validate`](proposal-validate.md) — cheap digest check.
- [`plan apply`](plan-apply.md) — deprecated alias.
