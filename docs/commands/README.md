# Command reference

This reference covers the current **Core CLI and native host utilities**.
Command existence does not imply provider parity. Use `twig --version`, then
`twig --help-all` for the installed command catalog or `twig <command> --help`
for arguments, defaults, and examples. Use these pages for behavior and effects.

Setup, identity, connection, work-item and Bench commands require the selected [server](../server.md) to
be running. The CLI sends one request over owner-only local IPC, not through the
browser or MCP. The server uses the caller's workspace and the same native
metadata home; provider, target, policy and journal checks remain authoritative.
There is no in-process or legacy cache fallback. If the connection is lost
after dispatch, inspect the original journal before attempting another change:
the CLI does not replay the request. Help, version and skills remain offline.
PAT enrollment reads the secret at the caller's terminal, not from argv.
The [getting-started guide](../getting-started.md) covers provider setup.

## Find a task

| Task | Commands and effects |
| --- | --- |
| [Control the server](system/server.md) | `server start` starts or reuses the selected instance; `server status` observes without starting; `server stop` explicitly shuts it down |
| [Initialize](getting-started/README.md) | `init` creates fresh local state |
| [Read and find](context/README.md) | `show`, `show-batch` are cached by default; `show --refresh` and `query` can perform provider reads |
| [View a hierarchy](views/README.md) | Targeted `tree` / `show --tree`; not GitHub-qualified |
| [Manage Benches](bench/README.md) | Shared user-wide definitions, named rules and explicit ADO planning discovery |
| [View workspace](workspace/README.md) | `workspace --bench <name-or-id>`, optional refresh and local exclusions |
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

There is no current item or Bench in the CLI. Human commands require explicit
targets too; they cannot borrow a viewer's focus. Seed creation must choose
`--parent <id>` or `--no-parent`. Use `process <type>` for explicit schema reads.
File-scoped proposal operations are already explicitly bound.

`workspace --bench <name-or-id>` reports that Bench's scope and protected drafts.
Parse complete actual JSON, not terminal output
or a truncated display. Inspect both exit status and application/journal outcomes.

## Retired surfaces

These are not experimental alternatives or compatibility routes:

| Retired | Current direction |
| --- | --- |
| [`server start/status/stop`](system/server.md) | Manage the local Twig Server |
| Quick `new`, bare `seed` | `seed new`, then explicit validation/publication |
| `delete` | No qualified CLI deletion route |
| `history`, `tree-set`, `tree --all`, `sprint`, live/full-backlog views | Cached `show`, explicit refresh, bounded queries, static workspace or targeted tree; no equivalent whole-history export |
| `set`, `nav` (all directions), back/forward, `states` | `show <id>`, targeted `tree <id>` or `process <type>` |
| `bench switch`, contextual `sync` and `refresh` | `workspace --bench <name-or-id>`; add `--refresh` for a supported pull without saving drafts |
| `bench browser/detail/discussion/configuration`, captured `draft` envelopes | Separate native server host; not a CLI envelope fallback |
| `policy assess/reconcile`, `proposal latest/seed/reconcile` | Current file-scoped proposal lifecycle; no automatic repair/seed-export replacement |
| `process layout`, old complete process descriptor | `process description` reports acquired Core schema and explicit gaps |
| `config`, `config status-fields`, migrations, reset/reinitialize, identity/target hot-switches | Explicit fresh initialization and named enrollment/bindings |
| `provider discover/bind github` | Fresh `init --provider github` with exact account and repository |
| `upgrade`, `changelog` | Rerun the CLI installer; read GitHub Releases |
| `tui`, `mcp`, `ohmyposh init`, automatic companions | No current CLI launch path; server read-only MCP is separate |

Retirement pages remain at old documentation URLs to avoid turning existing
links into runnable obsolete recipes.

## Known discovery limitation

The hidden `save` handler also remains declared but is rejected by top-level
command discovery, including `save --help`. It is not a usable standalone CLI
route in this build. Existing-item `update`/`edit` save their own changes;
a Bench refresh does not save pending edits.

`index.json` is the machine-readable current reference mapping, not a promise of
runtime permission or a replacement for the installed command's help.
