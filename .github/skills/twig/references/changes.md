# Changing tracker work

Use after the [Twig foundation](../SKILL.md). The current proposal contract is
Core version 3 only. Old v1/v2 documents, `plan` aliases and `proposal seed` are
not authoring or compatibility routes.

## Establish intent

Read `twig proposal --help` and the relevant leaf help. Confirm the connection,
provider-qualified identities, requested effects and observed revisions.
Discover type/field requirements and close gates; coupled state and gate-field
changes belong in one operation. Use the user's scope and explicit review hold.

Structured single-item mutations require `--id`. Do not change or rely on the
interactive active-item pointer for automation. File-scoped proposal operations
remain bound to their explicit document and targets.

## Validate, preview, apply, verify

1. Author the current native v3 document in the intended workspace using exact
   captured connection/schema/store/policy evidence. Do not invent identities,
   revision tokens, published IDs or execution state.
2. Run `twig proposal validate --file <path> -o json`, then
   `twig proposal preview --file <path> -o json`. Retain the complete output,
   workspace and exact digest. Preview is not publication or a lock.
3. Review every material operation, expected revision and effect from the exact
   proposal together with preview issues and blockers. The ordinary preview
   may expose only operation identities/kinds, not a rich effect model.
   Present exact facts in the destination's supported format. There
   is no bundled OMP viewer, rendered envelope or human Apply button.
4. Apply with `twig proposal apply --file <path> --confirm <digest>`.
   Changed canonical intent requires a fresh preview and digest. Do not flush or
   discard unrelated local work to make a proposal applicable.
5. Inspect all operation outcomes with
   `twig proposal status --file <path> -o json`, then refresh-read affected
   items. Distinguish verified effects, failures, untouched rows and unresolved
   outcomes. Multi-operation apply is not globally atomic.

## Recovery

Preserve the original proposal, digest, attempts and journal. An unknown outcome
is not proof that no write happened. Follow installed status/apply recovery
guidance; never blindly repeat a possibly successful creation or replay an
already verified prefix.

`connection writes` is inspect-only correlated transport evidence. The Core
proposal journal is execution authority. Neither a transport receipt, cached
value, local reset nor a zero exit code alone proves the remote outcome.
