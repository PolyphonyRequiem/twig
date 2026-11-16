# Issue tracker: Azure DevOps via Twig

Schedulable work for this repository is tracked on the **ADO board
`PolyphonyRequiem/Twig`**, using the Twig CLI. The GitHub remote does not select
the tracker connection.

| Record | Location |
| --- | --- |
| Work status and scheduling | ADO work items |
| Architecture decisions and specifications | Relevant repository docs, reviewed with the code |
| Public contributor reports | GitHub issues, linked to their ADO work when scheduled |

Do not create a work item merely to make a conditional request true. If an
outside report is tracked in ADO, link both records and keep the GitHub issue
open while the public defect remains unresolved. Commits implementing tracked
work use `AB#<id>`.

## Read and change deliberately

Use the task's explicitly chosen connection and initialized workspace; do not
assume another worktree's selection applies. Load the `twig` skill for shared
operations and follow the repository's work-item lifecycle instructions.

```sh
twig process
twig process description <actual-type-reference> -o json
twig show <id> --refresh -o json
twig query --title "search text" -o json
```

Discover actual types, states, fields and requirements. Do not infer their
meaning from a display name or copy a historical process roster.
Machine mutations require explicit `--id`; scripts do not set an active item as
a targeting workaround.

`update`, `state`, `note`, `batch` and `patch` can publish immediately through
Core. They are not a local queue awaiting sync. Ordinary `sync` can save eligible
remote drafts before pulling; `sync --pull-only` does not publish.

For new work, use `seed new` with an actual discovered type and explicit parent
choice. Capture its returned UUID/negative alias, inspect and validate intent,
and publish only through the supported explicit route. No quick `new` or
published-item deletion fallback is available.

## Close and verify

A state-only change cannot carry coupled required close fields. Discover them
from the connected type, put the transition and all required values in **one**
proposal operation, then preview and apply the exact file/digest:

```sh
twig proposal preview --file change.json -o json
twig proposal apply --file change.json --confirm <exact-preview-digest> -o json
twig proposal status --file change.json -o json
twig show <id> --refresh -o json
```

Follow the lifecycle protocol's notes and verification order. Do not mark tracked
work complete before the actual ADO transition is confirmed.
Applied acknowledgement is not independently Verified delivery.
After a partial/unknown outcome, inspect original journal evidence rather than
reposting a comment, recreating a seed or retrying verified operations.

See the [current command reference](../commands/README.md),
[draft lifecycle](../features/seeds-and-publishing.md), and
[proposal guide](../features/proposals.md) for exact supported boundaries.
