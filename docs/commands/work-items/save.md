---
command: save
group: work-items
summary: Declared internal save path, currently unreachable through top-level CLI discovery.
stability: experimental
mutates: ado
---

# `twig save`

**Not a usable standalone command in the current build.** Although a hidden
handler remains declared, top-level discovery rejects `save`, including
`save --help`, before execution. This page records the implementation boundary,
not an invocation recipe.

## Behavior

The retained Core save helper freezes eligible existing-item typed drafts and
uses preview, digest-bound plan, journal and independent verification.
Current `update`/`edit` save their own changes through that lifecycle.
To inspect a named Bench, use `twig workspace --bench MyWork`. To refresh its
view, use `twig workspace --bench MyWork --refresh`. Neither is a save command.

## Effects

The helper can publish, persist original attempts/receipts and settle exact
verified drafts. Reaching it through another command does not waive that
command's scope or provider restrictions.

## Failure modes

Current standalone invocation exits nonzero as an unknown command. Do not repair
that by bypassing CLI admission or invoking a retired writer.
