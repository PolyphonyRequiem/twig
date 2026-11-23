# Seeds and publishing

A **seed** is an unpublished typed Core draft. It has a canonical UUIDv7;
the negative ID displayed by the CLI is a convenience alias. It does not live in
the removed legacy `work_items`/`seed_links` cache model, and a title is never a
safe substitute for its identity.

## Author and inspect

Discover types first, then explicitly choose a parent or no parent:

```sh
twig process
twig seed new --no-parent --type <discovered-type> --title "Investigate failure"
twig seed view
twig pending -o json
```

For the Twig provider, the type is `Twig.Item`. For ADO and GitHub, use the actual
discovered type reference; do not assume Task, Issue, or Bug exists.
The provider's initial-state and required-field rules still apply.
Every CLI creation requires an explicit parent decision, even in human output.
For `seed new`, use exactly one of `--parent <id>` or `--no-parent`.
GitHub and the Twig provider require `--no-parent`; ADO also supports a parent.

`seed new` stages locally, never creates a provider item. Retained ADO conveniences
include editor, chain, and creation-link commands; they are not GitHub-qualified.
Use `seed <verb> --help` for actual arguments. Negative aliases are not sequential
`-1`, `-2` guarantees; capture the returned ID and UUID.

An original UUID can be read with `show --local-id <uuid> -o json`, including its
permanent mapping after verified publication. Imported items do not acquire a
second arbitrary local UUID registry.

## Add an existing seed to a Bench

A Bench can reference a seed staged with `twig seed new` before it has a
provider ID. The workspace still owns the original draft. Referencing the
same UUID on two Benches does not copy its fields or links; removing the
reference from one Bench does not discard the draft or remove it from the
other. A reference grants no permission to edit or publish.

Native clients can check for the `bench-seed-members-v1` server capability.
With the Bench selected in normal mode and the original source admitted,
use this view-scoped route with the usual content bearer and `X-Twig-Bench: 1`:

```text
PUT    /api/v1/views/{view}/benches/{bench}/sources/{source}/seed-members/{uuid}
DELETE /api/v1/views/{view}/benches/{bench}/sources/{source}/seed-members/{uuid}
```

Use the UUIDv7, not the negative CLI alias. Neither request takes a body;
the response is `{"changed":true}` when the rule changes and `{"changed":false}`
when already in the requested state. Read the ordinary Bench view to see
the result. A session without access to the originating workspace cannot
read the draft through the reference. Isolated Plan seeds are not eligible.

After verified creation, the remaining references become ordinary pins for
the provider ID. Removing a reference before delivery keeps that Bench out
of delivery; an existing pin is not duplicated. If the create response is
lost, keep the seed and its original attempt and receipts. Retrying a Bench
update is not permission to send another create.

## Publication

Use the negative alias returned by `seed new` in each command:

```sh
twig seed validate <alias>
twig seed publish <alias> --dry-run
twig seed publish <alias>
```

The last command publishes through Core's internally digest-bound save
path; it does not ask for a separate `--confirm` digest. Review intent before
issuing it. `--all` is an explicit wider scope, not the default.
`seed publish` is human-only. Automation uses an explicitly scoped Core plan.

Publication uses the existing Core plan/digest/journal path. A creation
attempt UUID is persisted before dispatch. Ordered dependencies and permanent
local-to-provider mappings preserve original identity; no published ID is
predicted from another item.

`seed discard` removes local unpublished intent under its own checks. It is not
a remote delete or permission to drop a protected uncertain creation.

## Interrupted publication

Do not create a replacement seed, change its UUID, or repeat publication because
a response was lost. Read the original plan journal and retained creation
evidence. Applied acknowledgement, verified provider readback, local mapping and
local delivery/settlement are distinct outcomes.

`seed reconcile` can settle eligible local state from original verified outcomes;
it cannot manufacture publication proof or blindly replay a create. A matching title or an empty
search does not settle uncertainty. Keep unknown, partial, verified, and
unattempted work distinct when reporting results.

The server's normal seed editor and isolated Plan path retain their own ownership
and admission checks; they do not adopt arbitrary CLI debt. See
[plans](plans.md) and [server operation](../server.md).
