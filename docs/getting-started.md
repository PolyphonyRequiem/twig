# Getting started

Twig separates a provider connection, a local workspace mirror, and user-wide
saved Benches. Initialization does not import your backlog or choose a Bench.
Run at a fresh Git worktree root with no `.twig` directory.

## Choose a provider

### Azure DevOps

```sh
twig auth login --identity work
twig connection bind --org contoso --project MyProject --identity work --default
twig init contoso MyProject
twig show 12345 --refresh
```

Replace the example coordinates and ID. `auth pat` is the alternative enrollment
route. If a different default binding already exists, do not treat `bind` as an
account switch: choose the intended registered binding explicitly with
`init --binding <binding-id>` in a fresh worktree.
See [authentication](features/authentication.md).

### GitHub

Use `gh auth login` separately, then name the account:

```sh
twig init --provider github --host github.com --repository owner/repo --account my-account
twig show 42 --refresh
```

The account name is explicit; the ambient token environment or whichever `gh`
account happens to be active does not replace that selection.

### Twig-native

```sh
twig init --provider twig
twig process
twig seed new --no-parent --type Twig.Item --title "First item"
twig seed view
```

This provider needs no remote tracker credentials. Its durable per-user authority
is separate from each workspace's client mirror. Its schema has `Twig.Title`,
Markdown `Twig.Description`, and `Twig.State` with Open, Active, and Closed values.
These are this provider's explicit schema, not defaults imposed on ADO or GitHub.
No assignee, comment, or relationship capability is implied.

## Read and organize work

```sh
twig show 12345 --refresh
twig show 12345
twig query --title "authentication"
twig process
twig bench create MyWork
twig bench switch MyWork
twig workspace track 12345
twig workspace
```

Use real IDs from your provider. `show` is cached unless you request `--refresh`.
Queries can perform provider reads. A Bench is a saved selection, not an
automatically populated "assigned to me" view. Creating one does not select it.
See [Benches and context](features/workspace-bench-context.md).

## Change work deliberately

Read `twig <command> --help` first. `update`, `state`, `note`, `batch`, and `patch`
can write to the provider immediately through Core; they are not merely a local
editing buffer. Seeds remain local until explicitly published.

For file-based work use the [proposal lifecycle](features/proposals.md):

```sh
twig proposal validate --file change.json -o json
twig proposal preview --file change.json -o json
twig proposal apply --file change.json --confirm <exact-preview-digest> -o json
twig proposal status --file change.json -o json
```

The file must use the current canonical Core format and belong to this workspace's
connection. Older plan JSON is not translated. Recheck affected items with
`show <id> --refresh` and inspect unresolved journal outcomes before any retry.

For scripts, single-item mutations require `--id` with structured output.
Structured reads need an explicit target; never select an active item as a
workaround. Use actual discovered field, type, and state references.

## Provider differences

| Provider | Important boundaries |
| --- | --- |
| ADO | Discovers the connected process. Native atomic batches can combine transitions and their required fields. Targeted trees, navigation, editing, and Bench planning have retained ADO routes. |
| GitHub | Numeric issue locators resolve within the pinned repository. CLI queries support title/body/state with at most 100 total candidates; larger acquisitions refuse rather than claim completeness. Tree/navigation/editor/chain/sync conveniences are not qualified. Writes are observed-non-atomic; external workflow rules are not discovered. |
| Twig-native | Uses a durable local authority with an explicit small schema. Local authority is not the workspace cache. Do not infer ADO/GitHub capabilities. |

The [GitHub provider reference](../src/Twig.Providers.GitHub/README.md) distinguishes
adapter capabilities from the narrower CLI surface.

## Existing or partial local state

`init` creates `.twig/core.db` and its workspace marker. It refuses an existing
`.twig` directory, database, or SQLite sidecar rather than migrating, resetting,
or adopting it. A custom `--core-store <new-absolute-file>` needs an existing parent;
protect custom files and sidecars outside `.twig` from Git separately.

For disposable developer data, use a fresh worktree and, when needed, a fresh
explicit `TWIG_USER_HOME`. Discard only identified Twig application data.
Credentials and upstream items are not reset targets. If a write outcome is
uncertain, retain its original proposal and evidence: a new workspace does not
establish what happened or make replay safe.
