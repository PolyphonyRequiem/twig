# Command reference

This reference covers the current **Core CLI and native host utilities**.
Command existence does not imply provider parity. Use `twig --version`, then
`twig --help-all` for the installed command catalog or `twig <command> --help`
for arguments, defaults, and examples. Use these pages for behavior and effects.

Setup, identity, connection, work-item and Bench commands require the selected [daemon](../daemon.md) to
be running. The CLI sends one request over owner-only local IPC, not through the
browser or MCP. The daemon uses the caller's workspace and the same native
metadata home; provider, target, policy and journal checks remain authoritative.
There is no in-process or legacy cache fallback. If the connection is lost
after dispatch, inspect the original journal before attempting another change:
the CLI does not replay the request. Help, version and skills remain offline.
PAT enrollment reads the secret at the caller's terminal, not from argv.
The [getting-started guide](../getting-started.md) covers provider setup.

## Find a task

| Task | Commands and effects |
| --- | --- |
| [Control the daemon](../daemon.md) | `daemon start` starts or reuses the selected instance; `daemon status` observes without starting; `daemon stop` explicitly shuts it down |
| [Initialize](getting-started/README.md) | `init` creates fresh local state; `sync` can save drafts before pulling; `refresh` / `sync --pull-only` only pull |
| [Read and find](context/README.md) | `show`, `show-batch` are cached by default; `show --refresh` and `query` can perform provider reads; `set` changes human active context |
| [View a hierarchy](views/README.md) | Targeted `tree` / `show --tree`; not GitHub-qualified |
| [Navigate](navigation/README.md) | Human `nav up/down/next/prev`; changes local context and can acquire missing observations |
| [Manage Benches](bench/README.md) | Shared user-wide metadata, pins/rules and explicit ADO planning discovery |
| [View workspace](workspace/README.md) | Static membership, local exclusions, pins, and retained planning rules |
| [Inspect schema](process/README.md) | `process`, `process <type>`, `process description`; provider discovery, no work-item mutation |
| [Change items](work-items/README.md) | `update`, `state`, `note`, `batch`, `patch`, `edit` can publish; `discard` removes eligible local intent |
| [Relationships](links/README.md) | Qualified ADO native links and explicitly local-only association routes; reparent is refused |
| [Draft new items](seeds/README.md) | `seed new/view/edit/link/chain/validate`; publication is explicit and human-output-only |
| [Review proposals](plans/README.md) | File-scoped `proposal validate/preview/apply/status`; preview retains local journal, apply can publish |
| [Identity and evidence](system/README.md) | Named enrollment and binding, recorded status, read-only transport evidence, version |
| [Help and skills](configuration/README.md) | Offline help and explicit skill lifecycle |

## Automation

Structured output (`json`, `json-full`, `json-compact`, `minimal`, `ids`) is a
machine invocation. Not every command supports every format.
Single-item mutations require **`--id`**. Structured `show` and targeted `tree`
need a positional ID or `--id`; conflicting targets refuse. Original seed UUID
reads use `show --local-id <uuid> -o json`.

Do not use active-item selection as a script target. Structured `set`, navigation,
`states`, and seed publication are refused. Structured seed creation must choose
`--parent <id>` or `--no-parent`. Use `process <type>` for explicit schema reads.
File-scoped proposal operations are already explicitly bound.

Machine workspace output excludes implicit active-item relationships but keeps
explicit Bench scope and drafts. Parse complete actual JSON, not terminal output
or a truncated display. Inspect both exit status and application/journal outcomes.

## Retired surfaces

These are not experimental alternatives or compatibility routes:

| Retired | Current direction |
| --- | --- |
| Quick `new`, bare `seed` | `seed new`, then explicit validation/publication |
| `delete` | No qualified CLI deletion route |
| `history`, `tree-set`, `tree --all`, `sprint`, live/full-backlog views | Cached `show`, explicit refresh, bounded queries, static workspace or targeted tree; no equivalent whole-history export |
| Bare interactive `nav`, history/back/fore and top-level navigation aliases | `nav up/down/next/prev` only |
| `bench browser/detail/discussion/configuration`, captured `draft` envelopes | Separate native daemon host; not a CLI envelope fallback |
| `policy assess/reconcile`, `proposal latest/seed/reconcile` | Current file-scoped proposal lifecycle; no automatic repair/seed-export replacement |
| `process layout`, old complete process descriptor | `process description` reports acquired Core schema and explicit gaps |
| `config`, `config status-fields`, migrations, reset/reinitialize, identity/target hot-switches | Explicit fresh initialization and named enrollment/bindings |
| `provider discover/bind github` | Fresh `init --provider github` with exact account and repository |
| `upgrade`, `changelog` | Rerun the CLI installer; read GitHub Releases |
| `tui`, `mcp`, `ohmyposh init`, automatic companions | No current CLI launch path; daemon read-only MCP is separate |

Retirement pages remain at old documentation URLs to avoid turning existing
links into runnable obsolete recipes.

## Known discovery limitation

The hidden `save` handler also remains declared but is rejected by top-level
command discovery, including `save --help`. It is not a usable standalone CLI
route in this build. Existing-item `update`/`edit` save their own changes;
ordinary `sync` can save eligible remote drafts before pulling and therefore
must not be treated as a harmless read workaround.

`index.json` is the machine-readable current reference mapping, not a promise of
runtime permission or a replacement for the installed command's help.
