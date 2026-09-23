# Changing tracker work

Use after the [Twig foundation](../SKILL.md). This is the proposal procedure, not an organization-specific lifecycle policy or a new authorization mechanism. A Change Recipe generates proposals from inputs; it is not a presentation template.

## Establish intent

Read `twig proposal --help` and the relevant leaf help. Confirm connection, target identities, requested effects and current revisions. Discover required fields and transition gates; include fields coupled to a transition in the same operation.

Use the user's actual scope and approval policy. A proposal does not itself require an extra human prompt. Explicit review holds still apply. Attribute delegated agent authorization truthfully, not as an authenticated human signature or approval the human never gave.

Ready when the intended effects, preconditions and applicable authority are explicit.

## Validate, review, apply, verify

1. **Author:** use the current native schema inside the intended Twig workspace. Execution state belongs in the journal, not the proposal. Obtain staged seed identities and fingerprints through `twig proposal seed`; never predict published IDs or treat temporary aliases as durable identities.
2. **Validate and preview:** run `twig proposal validate`; resolve every issue before proceeding. When the applicable authorization policy requires human review **and** interactive OMP has the selected `twig_proposal_render({file,workspace?,full?})` presenter, call that tool once; it owns one native JSON capture (see the [presentation reference](presentation.md)). Otherwise, including delegated AFK and headless work, run `twig proposal preview -o json` once through the ordinary noninteractive path. Retain the exact digest, workspace, complete structured result and supported `reviewModel` from whichever single path was used. Preview can write the local journal, not ADO. `canApply` is not proof that remote revision or process gates still hold. Native presenter support is proven by validating its returned envelope and model version, not by assuming a minimum version.
3. **Review:** inspect every operation, consequence, precondition, blocker and available choice from the complete structured preview. When the interactive presenter was used, details and Back switch retained native brief/full frames; close never authorizes or applies. If the companion or tool is unavailable **before** a human-review call, disclose that fact and use the ordinary complete structured/plain preview instead. If a tool call begins but returns a malformed, failed, truncated, unsupported, noninteractive or digest-mismatched response, stop the affected authorization and report the failure; do not parse ANSI, run another preview, or substitute a mutation path. Delegated AFK work retains its existing authorization and apply gates without waiting for an interactive viewer.
4. **Apply:** call `twig proposal apply` with the exact reviewed digest and required authorizer. Changed canonical content requires fresh review and applicable authorization. Presentation never supplies authorization. Do not auto-flush or discard pending edits to make the proposal applicable.
5. **Verify:** inspect every result and `twig proposal status`, then refresh-read the affected items. For seed publication, retain and verify the positive published ID against the staged identity when returned. Report verified, failed, unknown and untouched operations separately. Multi-item apply is not atomic: a successful prefix can remain committed after a later failure.

Complete when every intended operation has a verified outcome or an explicitly reported unresolved result, with the native journal and authoritative readback retained.

## Recover without guessing

Read the native journal before retrying interrupted work. Follow the installed `proposal apply --help` and `proposal status --help` recovery contract. An unknown outcome is not proof that no write occurred.

Preserve the old proposal and journal. Establish what landed before drafting any safe remaining change or performing an allowed resume. Never blindly retry an Indeterminate write, repeat seed creation, or replay a verified prefix because the outer session lost its response. Use a fresh proposal when native recovery requires one.

Complete when successful effects remain accounted for, unresolved effects are identified and no possibly successful write has been duplicated. A cached value or zero exit code alone is not verification.
