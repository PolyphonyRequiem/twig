# Operating Twig

Use after the [Twig foundation](../SKILL.md) when executing commands. This reference supplies procedure, not a second command catalog or customer-specific process policy.

## Discover the installed contract

Identify the executable with `twig --version`. Read `twig --skill` when foundation guidance is missing or may belong to another build. Use `twig skills status --help` to inspect installed guidance and `twig skills update --help` for explicit updates; ordinary use does not update skills or provider settings.

Start with `twig --help`, then the relevant group or leaf `--help`. Leaf help contains declaration-derived syntax and bundled behavior. Use `twig --help-all` only when complete command discovery is needed. Unknown commands are errors, not a reason to guess variants or repeatedly dump the catalog.

Ready when the intended executable and relevant command contract are known.

## Read and navigate

1. Confirm the intended connection and target. Distinguish the invocation directory, Git worktree, Twig binding and terminal host; older uses of “workspace” may mean different things.
2. Inspect command side effects: reads can refresh local caches or write output files. `twig set` changes the local pointer, not assignment or claim. `twig sync --pull-only` refreshes without pushing; ordinary sync can push pending edits.
3. Discover types, states, fields and gates through targeted `twig process` and `twig process description` help. Use returned names/reference names. A process description is not permission to perform a particular transition.
4. Choose human output for direct terminal use, JSON when parsing, or minimal output for its documented pipe contract. Parse actual complete machine output, never a truncated or colored rendering. Inspect exit status, stdout and stderr.

Ready when the requested facts, connection and relevant freshness/completeness limits are established without unintended writes.

## Structured mutation calls

Structured output (`json`, `json-full`, `json-compact`, `minimal`, or `ids`) marks a machine invocation. Mutations that otherwise target the active work item require an explicit `--id`; a missing ID is a usage error before workspace resolution, so the active item is not read or changed. Multi-item commands may use an explicit multi-item scope such as `--ids` or `--all`. `twig set`, `twig new --set`, navigation commands, and `twig seed publish` are refused with structured output because they read or change active context. For structured seed creation, select `--parent <id>` or `--no-parent` rather than inferring a parent from the active item. Reads retain their existing behavior, and file-scoped proposal operations use the identities in the proposal rather than the active item. Human CLI use is unchanged.

Examples:

- `twig state Done --id 1234 -o json`
- `twig update System.Title "New title" --id 1234 -o json`
- `twig show 1234 -o json` (a read; no `--id` mutation guard applies)

## Compact reads

Bind the qualified executable path and hash, invocation workspace and organization/project before comparing compact and full responses. Keep that binding across calls. Reuse help only for the same executable contract; reload when it changes.

Use installed help to confirm these opt-in projections:

- `show <id> --refresh -o json --fields System.Title,System.State` for selected fresh item facts; add `--sections links` when edges matter.
- `process description <type-reference> -o json --sections requirements` for type requirements, or `--sections fields --fields <reference,...>` for selected field constraints.

Follow the response's `fullRead` or full-route hint when omitted facts are needed. Preserve absent/unknown distinctions, partial coverage and local-change indicators. Type requirements are not evaluated transition authorization. Process capture is scoped to connection/process and capture time, not an invented server-wide revision; refresh before mutation decisions.

Ready when selected facts answer the question and the full route remains available; missing facts are not silently treated as empty.

## Local drafts and authentication

Read `twig seed --help` and the relevant leaf help before authoring drafts. Validate before publication. After an interruption, inspect seed reconciliation and proposal status rather than repeating creation. Use the [change procedure](changes.md) to publish through a reviewed proposal.

Inspect pending changes before any decision to push or discard them. Never automatically flush or discard unrelated work to make an operation succeed.

Use `twig auth --help` for authentication. Keep tokens, PATs, credentials and token-cache contents out of arguments, logs, notes and tracker content. Authentication changes require authority separate from a read or presentation request.
