---
name: twig-changes
description: Use when authoring, reviewing, applying or recovering a Twig change proposal, including seed publication and multi-item board changes. Load twig-cli for shared discovery, process and companion rules.
---

# Twig changes

Load **twig-cli** first for the shared operating rules, installed-command discovery
and additive companion selection. This skill supplies the change procedure, not
an organization-specific lifecycle policy or a new authorization mechanism.

## Establish the intended change

Read `twig proposal --help`, then the relevant leaf help for exact syntax and
behavior. Confirm the connection, target identities, requested effects and current
revisions. Discover the target process's required fields and transition gates;
include coupled fields in the same operation when the process requires that.
A proposal does not automatically require an additional human prompt: use the
user's actual scope and approval policy. Explicit approval requirements still
apply. Record a delegated agent authorizer truthfully, not as an authenticated
human click or signature.

## Validate, review, apply, verify

1. Author the proposal inside the intended workspace using the current native
   schema. Keep execution state in Twig's journal, not in the proposal. Use
   `twig proposal seed` for staged identities and fingerprints; do not predict
   published IDs or reuse transient aliases as durable identities.
2. Run `twig proposal validate` and stop on every issue. Run
   `twig proposal preview` and inspect the exact digest, all operations, material
   effects, preconditions, blockers and pending changes. Preview is a local
   journal write, not an ADO mutation; `canApply` is not proof that remote
   revisions or every process gate still hold at execution time.
   The preview JSON and native journal are the semantic source; rendered excerpts are presentation only and must never be reparsed as JSON or treated as complete facts.

3. Review the full material change under the user's authority. Retain item
   identity and links, process type and useful parent/Bench context when known.
   Distinguish unknown before-values from empty ones. A summary or companion
   may rearrange the explanation but cannot replace the proposal's facts or
   hide destructive effects, blockers, failed operations or untouched items.
4. Apply only the exact reviewed digest with the required authorizer through
   `twig proposal apply`. Changed canonical content requires fresh review and
   authorization; absent, expired or unrelated approval is not approval.
   Do not auto-flush or discard pending edits to make a proposal applicable.
5. Read `twig proposal status`, then refresh-read the affected items to confirm
   what actually landed. Report successes, failures, unknown outcomes and
   untouched work separately. A multi-item apply is not one atomic transaction:
   an earlier successful operation may remain committed after a later failure.

## Recover without guessing

Read the native journal before retrying an interrupted apply. Its actual states
and recovery rules are described by `twig proposal apply --help` and
`twig proposal status --help`. Reconcile unknown outcomes against authoritative
reads. Preserve the old proposal and journal as evidence; author a fresh proposal
for a safe remaining change when native recovery requires it. Never repeat seed
creation or replay a verified prefix merely because the outer session lost its
response. A cached value or a zero exit code alone is not verification.

## Report to the human

For a single success, a short confirmation with the affected item and verified
result is sufficient. For several items, provide readable links and explicit
per-item outcomes. Use Twig's human output when no companion or agent is present;
JSON remains the complete structured route. Preserve supported icon/color cues,
but use textual labels and a readable fallback when the surface or font cannot
render them. Color alone must not distinguish outcomes. A presenter changes
layout, not proposal meaning, approval policy or executable authority.
