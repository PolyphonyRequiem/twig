# Changing tracker work

Use after the [Twig foundation](../SKILL.md). This is the proposal procedure, not an organization-specific lifecycle policy or a new authorization mechanism. A Change Recipe generates proposals from inputs; it is not a presentation template.

## Establish intent

Read `twig proposal --help` and the relevant leaf help. Confirm connection, target identities, requested effects and current revisions. Discover required fields and transition gates; include fields coupled to a transition in the same operation.

Use the user's actual scope and any explicit review hold. A proposal does not itself require an extra human prompt or authorizer identity.

Ready when the intended effects, preconditions and applicable authority are explicit.

## Machine invocations and active context

With structured output (`json`, `json-full`, `json-compact`, `minimal`, or `ids`), a mutation targeting one work item must use `--id`; `show`/`tree` reads need a positional ID or `--id`. Do not set or rely on Twig's active-item pointer for automation. Use `twig process <type>`, not structured `states`. Machine workspace output and machine/browser Bench pulls preserve Bench scope and drafts without consulting active context. File-scoped proposal validate, preview and apply commands are already bound to explicit work-item IDs. Human CLI behavior is unchanged.

## Validate, review, apply, verify

1. **Author:** use the current native schema inside the intended Twig workspace. Execution state belongs in the journal, not the proposal. Obtain staged seed identities and fingerprints through `twig proposal seed`; never predict published IDs or treat temporary aliases as durable identities.
2. **Validate and preview:** run `twig proposal validate`; resolve every issue before proceeding. When a human review is requested **and** interactive OMP has the selected `twig_proposal_render({file,workspace?,full?})` presenter, call that tool once; it owns one native JSON capture (see the [presentation reference](presentation.md)). Otherwise run `twig proposal preview -o json` once through the ordinary noninteractive path. Retain the exact digest, workspace, complete structured result and supported `reviewModel` from whichever single path was used. Preview can write the local journal, not ADO. `canApply` is not proof that remote revision or process gates still hold. Native presenter support is proven by validating its returned envelope and model version, not by assuming a minimum version.
3. **Review:** inspect every operation, consequence, precondition and blocker from the complete structured preview. When the interactive presenter was used, details and Back switch retained native brief/full frames; close never applies. If the companion or tool is unavailable **before** a requested human review, disclose that fact and use the ordinary complete structured/plain preview instead. If a tool call begins but returns a malformed, failed, truncated, unsupported, noninteractive or digest-mismatched response, stop that review and report the failure; do not parse ANSI, run another preview, or substitute a mutation path.
4. **Apply:** call `twig proposal apply --confirm <digest>` with the exact reviewed digest, or use the review page's explicit human Apply action bound to that same digest. Changed canonical content requires a fresh preview and the new digest. Do not auto-flush or discard pending edits to make the proposal applicable.
5. **Verify:** inspect every result and `twig proposal status`, then refresh-read the affected items. For seed publication, retain and verify the positive published ID against the staged identity when returned. Report verified, failed, unknown and untouched operations separately. Multi-item apply is not atomic: a successful prefix can remain committed after a later failure.

Complete when every intended operation has a verified outcome or an explicitly reported unresolved result, with the native journal and authoritative readback retained.

## Recover without guessing

Read the native journal before retrying interrupted work. Follow the installed `proposal apply --help` and `proposal status --help` recovery contract. An unknown outcome is not proof that no write occurred.

Preserve the old proposal and journal. Establish what landed before drafting any safe remaining change or performing an allowed resume. Never blindly retry an Indeterminate write, repeat seed creation, or replay a verified prefix because the outer session lost its response. Use a fresh proposal when native recovery requires one.

Complete when successful effects remain accounted for, unresolved effects are identified and no possibly successful write has been duplicated. A cached value or zero exit code alone is not verification.
