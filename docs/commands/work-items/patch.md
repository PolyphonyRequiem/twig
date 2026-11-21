---
command: patch
group: work-items
summary: Apply a discovered typed field object to one explicit published target.
stability: stable
mutates: ado
---

# `twig patch`

```sh
twig patch --id 12345 --json '{"System.Title":"New title"}' -o json
```

## Behavior

Requires an explicit positive target and exactly one JSON object source,
`--json` or `--stdin`. Use a positional ID or `--id` for human output;
use `--id` for machine output. The Core adapter does not patch unpublished seed aliases.

Actual discovered fields determine supported JSON value conversion.
Unknown, duplicate, read-only or invalid values refuse rather than warning and
continuing. Fields form one Core Batch under observed revision/schema/policy
evidence. Do not infer universal atomicity across providers: ADO and GitHub
concurrency guarantees differ.

## Effects

Can publish immediately and persist the original digest/attempt/verification.
No old conflict-resolution UI, automatic overwrite, or residual-note flush.

## Failure modes

Invalid/empty JSON, missing target, unsupported conversion, unmet coupled rules,
revision/admission changes or unresolved effects fail.
Use [`batch`](batch.md) for explicit named state/field composition.
