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

For Twig-native, the type is `Twig.Item`. For ADO and GitHub, use the actual
discovered type reference; do not assume Task, Issue, or Bug exists.
The provider's initial-state and required-field rules still apply.

`seed new` stages locally, never creates a provider item. Retained ADO conveniences
include editor, chain, and creation-link commands; they are not GitHub-qualified.
Use `seed <verb> --help` for actual arguments. Negative aliases are not sequential
`-1`, `-2` guarantees; capture the returned ID and UUID.

An original UUID can be read with `show --local-id <uuid> -o json`, including its
permanent mapping after verified publication. Imported items do not acquire a
second arbitrary local UUID registry.

## Publication

Validate the selected seed with `seed validate`, then inspect
`seed publish <actual-negative-id> --dry-run`. The non-dry-run human
`seed publish` command publishes through Core's internally digest-bound save
path; it does not ask for a separate `--confirm` digest. Review intent before
issuing it. `--all` is an explicit wider scope, not the default.
Structured seed publication is refused because it can change active context;
automation uses an explicitly scoped current Core proposal instead.

Publication uses the existing Core proposal/digest/journal path. A creation
attempt UUID is persisted before dispatch. Ordered dependencies and permanent
local-to-provider mappings preserve original identity; no published ID is
predicted from another item.

`seed discard` removes local unpublished intent under its own checks. It is not
a remote delete or permission to drop a protected uncertain creation.

## Interrupted publication

Do not create a replacement seed, change its UUID, or repeat publication because
a response was lost. Read the original proposal journal and retained creation
evidence. Applied acknowledgement, verified provider readback, local mapping and
local delivery/settlement are distinct outcomes.

`seed reconcile` can settle eligible local state from original verified outcomes;
it cannot manufacture publication proof or blindly replay a create. A matching title or an empty
search does not settle uncertainty. Keep unknown, partial, verified, and
unattempted work distinct when reporting results.

The daemon's normal seed editor and isolated Plan path retain their own ownership
and admission checks; they do not adopt arbitrary CLI debt. See
[proposals](proposals.md) and [daemon operation](../daemon.md).
