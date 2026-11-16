---
command: seed new
group: seeds
summary: Stage a UUID-backed typed seed without provider creation.
stability: stable
mutates: local
---

# `twig seed new`

```sh
twig seed new --no-parent --type <discovered-type> --title "Investigate failure"
twig seed new --parent 12345 --type <discovered-type> --title "First step" -o json
```

## Behavior

Stages typed local fields under a new canonical UUIDv7. Returns both `localId`
and its negative CLI alias; do not predict the alias.
Requires an actual discovered type and title (or supported editor input).
Initial state comes from acquired rules, not a hardcoded process name.

Machine calls must explicitly choose `--parent` or `--no-parent`; the two conflict.
GitHub and Twig-native require `--no-parent`; GitHub editor/creation links are not
qualified. Twig-native uses its explicit `Twig.Item` schema.
ADO can capture a published or existing local parent and repeatable typed
`--field <reference=value>` intent.

## Effects

Writes a local draft and can read parent/schema evidence. Never creates a provider
item. Native actor assignment is used only where the actual writable schema
supports it.

## Failure modes

Unknown/ambiguous type, unsupported fields/initial state, invalid parent or alias
collision refuses without guessing. See [seed lifecycle](../../features/seeds-and-publishing.md).
