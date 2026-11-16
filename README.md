# Twig

**Work tracking from your terminal.** Twig is a local-first CLI for Azure DevOps,
GitHub issues, and Twig-native work items. Read cached items, organize work in
named **Benches**, draft new items locally, and apply changes through a
digest-bound proposal journal.

The CLI ships as a NativeAOT binary for **Windows x64 and Linux x64**. macOS
support is suspended. The optional **Windows daemon** is also owned and released
by this repository; its canvas presentation package is separate.

## Install

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex
```

Linux:

```sh
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash
```

These commands execute the installer from `main`; review it before running.
Installers download the latest CLI release into `~/.twig/bin`. To update, rerun
the installer; `twig upgrade` is retired. See [installation](docs/installation.md)
for manual downloads and checksum verification.

## Get started

Run initialization at a **fresh Git worktree root**, without an existing `.twig`
directory. Choose one provider; a Git remote does not select your tracker.

### Azure DevOps

Enroll a named identity, bind it to your project, then initialize:

```sh
twig auth login --identity work
twig connection bind --org contoso --project MyProject --identity work --default
twig init contoso MyProject
twig show 12345 --refresh
```

Use your actual organization, project, and work-item ID.
[`twig auth pat`](docs/features/authentication.md) is available instead of
interactive sign-in. Azure CLI is not required.

### GitHub

Sign in using `gh` first, then name the exact account:

```sh
twig init --provider github --host github.com --repository owner/repo --account my-account
twig show 42 --refresh
```

GitHub support is deliberately narrower than ADO: queries are bounded, writes
are observed-non-atomic, and external workflow rules are not discovered.
See [provider differences](docs/getting-started.md#provider-differences).

### Twig-native

No ADO or GitHub credentials are needed:

```sh
twig init --provider twig
twig seed new --no-parent --type Twig.Item --title "First item"
twig seed view
```

The seed is local and **not yet published**. The native authority lives separately
from the workspace mirror. See [drafting and publishing](docs/features/seeds-and-publishing.md).

## Everyday use

```sh
twig show 12345                         # cached read
twig show 12345 --refresh               # explicit provider read
twig query --title "authentication"     # bounded provider query
twig process                           # discover actual types
twig pending -o json                    # inspect local drafts
```

Benches are user-wide saved selections, not automatically your assigned sprint.
Create one explicitly, select it in an initialized worktree, and pin items:

```sh
twig bench create MyWork
twig bench switch MyWork
twig workspace track 12345
twig workspace
```

For automation, use explicit targets (`--id` for single-item mutations) and
structured output. Check each command's help: not every format or capability is
available on every route or provider.

**Changes are not all local drafts.** Commands such as `update`, `state`, `note`,
and `batch` can publish immediately through Core. `sync` can save pending edits;
use `sync --pull-only` when you only intend to read. For file-based changes, review
the exact proposal before `proposal apply --file ... --confirm <digest>`.
Unknown write outcomes must be inspected, never blindly retried.

## Daemon and integrations

Twig owns the Windows daemon in [`src/Twig.Daemon`](src/Twig.Daemon).
It shares native connections and user-wide Benches, serves scoped reads and
local drafting, and keeps Save/Apply human-only. It has its own installer,
launcher, and `daemon-v*` releases; installing the CLI does not install it.

The separate [twig-bench-canvas](https://github.com/PolyphonyRequiem/twig-bench-canvas)
package owns presentation assets. The daemon can run without those assets and
provides a **read-only MCP bridge**. This is not the retired `twig mcp` server.
See the [daemon guide](docs/daemon.md) for prerequisites, operation, and MCP setup.

## Documentation

| Need | Start here |
| --- | --- |
| Setup and first commands | [Getting started](docs/getting-started.md) |
| Supported commands and limitations | [Command reference](docs/commands/README.md) |
| Benches, drafts, and proposals | [User guides](docs/README.md#user-guides) |
| Windows daemon | [Daemon ownership and operations](docs/daemon.md) |
| Agent help | `twig --skill` and [installed skills](docs/features/skills.md) |
| Build and contribute | [Contributing](docs/contributing.md) |
| Implementation | [Architecture](docs/architecture/overview.md) |

Use `twig --help`, then `twig <command> --help` for executable-matched syntax.
The [documentation index](docs/README.md) separates current guidance from design
and historical records.

## License

[MIT](LICENSE)
