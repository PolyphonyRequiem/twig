---
command: link artifact
group: links
summary: Attach a hyperlink or vstfs artifact link to a published work item.
stability: stable
mutates: ado
---

# `twig link artifact`

```sh
twig link artifact https://example.com/doc --id 1234 --name "Design doc"
twig link artifact vstfs:///Git/Commit/proj/repo/abc123 --id 42 -o json
```

Pass the URL as a positional argument and the published item's positive ID
with `--id`. An HTTP(S) URL becomes a `Hyperlink`; `--name` supplies its
comment. A `vstfs:///` URI becomes an `ArtifactLink`; `--name` names the
artifact and defaults to `Artifact` when omitted.

The command reads the target and binds the resource link to its observed
revision in a Core plan. It applies that plan by digest and reports
success only after verification. Missing or nonpositive IDs are usage errors.
An unresolved effect is not success or permission to retry.
