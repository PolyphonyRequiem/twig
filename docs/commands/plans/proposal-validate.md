---
command: proposal validate
group: plans
summary: Validate canonical Core proposal format and report its digest.
stability: stable
mutates: none
---

# `twig proposal validate`

```sh
twig proposal validate --file change.json -o json
```

## Behavior

Requires an explicit current canonical Core proposal file inside the admitted
workspace file boundary. Reports digest, format version and operation count.
`validationScope: "canonical-format"` and `applicationEvidenceChecked: false`
mean that process, policy and current execution evidence have **not** been checked.
Old plan v1/provider-plan v2 JSON is not translated.

## Effects

No provider request, journal import, draft change or publication.
Data-command workspace admission still applies.

## Failure modes

Missing file/invalid canonical input is an error. Syntax success does not imply
`canApply`; separately [preview](proposal-preview.md) the exact file.
Human, JSON aliases and minimal inspection are supported, not `ids`.
