---
command: note
group: work-items
summary: Publish a comment on an explicit qualified published item through Core.
stability: stable
mutates: ado
---

# `twig note`

```sh
twig note --id 12345 --text "Investigation complete" -o json
twig note --id 12345 --file findings.md --format markdown
```

## Behavior

Requires a published target and a nonempty body from inline text/`--text`, file,
stdin, or the supported human editor. Conflicting sources refuse. Name the
published item with `--id`. ADO uses the qualified HTML/comment formatting path; GitHub
retains Markdown. Local seeds do not support queued note publication.

## Effects

Can publish a provider comment immediately through a digested Core operation.
It is not a local audit note awaiting `save`.

## Failure modes

Invalid body/format, unsupported provider comment capability, denied/stale
admission or uncertain acknowledgement/readback fails. A lost response is not
permission to post the comment again.
