---
command: workspace migrate
group: workspace
summary: Review or explicitly confirm preservation of an admitted ADO workspace.
stability: experimental
mutates: local
---

# `twig workspace migrate`

Without flags this command reports data movement, the reviewed source digest,
the planned workspace-local backup directory, and rollback preconditions. It
does not create a migration intent, copy files, switch readers, publish work, or
flush pending changes.

## Synopsis

```console
twig workspace migrate -o json
twig workspace migrate --confirm <reviewed-digest> -o json
twig workspace migrate --rollback --confirm <original-digest>
twig workspace migrate --cancel-intent <preparing-intent-id> --confirm <original-digest>
```

## Flags

| Flag | Effect |
|---|---|
| `--confirm <digest>` | Explicitly authorize the exact reviewed local cutover. |
| `--rollback` | Return to original readers; requires the original digest. |
| `--cancel-intent <id>` | Cancel only that preparing intent; requires its original digest. |
| `-o human\|json\|minimal` | Select the complete output format. |

## Behavior

Omitting `--confirm` performs read-only inspection; no intent or backup is
created, no reader is switched, and no pending work is published or flushed.

Only an already-admitted ADO store with a known cached resource scope is
supported. A fresh process reads that scope from existing durable ADO identity
observations, not an in-memory sync warmup. Missing or multiple cached scopes
refuse rather than guessing or making a provider request.
A provider-target attachment, including GitHub, refuses before
opening ADO storage: no provider-native storage-generation producer exists yet.
Unadmitted or legacy stores must not be relabelled as admitted generations.
Unknown schemas, unmapped references and unreadable payloads also refuse.

`--confirm` is explicit authority to apply the exact reviewed local cutover.
It is not authority for any provider mutation. Apply keeps protected originals
and restore-qualified SQLite backups in `.twig/backups`; there is no automatic
backup deletion. Seed payloads, independent local/staged identities, opaque
context, ordered navigation IDs/timestamps/cursor, and existing durable pending
and publication evidence are preserved.

After an interruption, inspect again and resume with the original digest.
If ordinary edits changed that source, use `--cancel-intent` with the exact
preparing intent and its original digest, then review a new preview. Cancellation
retains intent history and backups; it does not discard the new work.
Inspection of a stale preparation reports `canApply: false`, the source-change
blocker, and the exact cancellation guidance; it exits `1` rather than presenting
the old digest as still eligible.

`--rollback` returns to original readers only while the same admitted generation,
reviewed write coverage, and original/retained parity still hold. It never restores
over divergent current data or deletes backups. A rolled-back intent is terminal.

Human, JSON and minimal output are supported. JSON includes `migration`,
`canApply`, `movements`, `unsupportedWork`, `rollbackPreconditions` and `nextSteps`.
`unsupportedWork` is empty only after the actual source inventory was verified;
unsupported source/provider generations are an explicit error, not an empty
successful report.

## Exit codes

Successful inspection or a verified local transition exits `0`.
Failed reads or migration guards exit `1`; invalid flag
combinations/digests exit `2`. No process template names are assumed.
