# Twig

**Work tracking from your terminal.** Twig is a local-first CLI for Azure DevOps,
GitHub issues, and work items from the Twig provider. Read cached items, organize
work in named **Benches**, draft new items locally, and apply changes through a
digest-bound plan journal.

The CLI ships as a NativeAOT binary for **Windows x64 and Linux x64**. macOS
support is suspended. The optional **Windows server** is also owned and released
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

Select an installed [server](docs/server.md) and run `twig server start` before
working with a provider. `twig server status -o json` checks readiness without
starting anything. Help and version are available without a server.

Run initialization at a **fresh Git worktree root**, without an existing `.twig`
directory. Choose one provider; a Git remote does not select your tracker.

### Azure DevOps

Register the provider connection, enroll your own identity, bind it, then initialize:

```sh
twig connection add --provider ado --org contoso --project MyProject
twig auth login --identity work
twig connection bind --provider ado --org contoso --project MyProject --identity work
twig init contoso MyProject
twig show 12345 --refresh
```

Use your actual organization, project, and work-item ID.
[`twig auth pat`](docs/features/authentication.md) is available instead of
interactive sign-in. Azure CLI is not required.

For a specific directory, add `--tenant <tenant-GUID-or-domain>` to
`twig auth login`. Named domains such as `microsoft.com` resolve to the concrete
tenant in the issued Azure DevOps token; registration still verifies the
refreshed token's tenant and principal.

### GitHub

Sign in using `gh` first, then bind the exact account to your connection:

```sh
twig connection add --provider github --host github.com --repository owner/repo
twig connection bind --provider github --host github.com --repository owner/repo --account my-account
twig init --provider github --host github.com --repository owner/repo
twig show 42 --refresh
```

GitHub support is deliberately narrower than ADO: queries are bounded, writes
are observed-non-atomic, and external workflow rules are not discovered.
See [provider differences](docs/getting-started.md#provider-differences).

For a repository that already has `twig.json`, run just `twig init` after registering
and binding the matching connection on your device. Keep the manifest unchanged:
it declares provider dimensions, not a contributor's credential or a device-local ID.
`twig connection list` lists all registrations through the server, regardless of
the current directory. Credentials and bindings remain private to the local user.

### Twig provider

No ADO or GitHub credentials are needed:

```sh
twig init --provider twig --database personal
twig seed new --no-parent --type Twig.Item --title "First item"
twig seed view
```

The seed is local and **not yet published**. Each named native database is an
isolated authority, separate from the workspace mirror. Initialize another fresh
repository with the same database name to share its published items; choose a
different name to keep work separate. There is no implicit default database.
See [drafting and publishing](docs/features/seeds-and-publishing.md).

## Everyday use

```sh
twig show 12345                         # cached read
twig show 12345 --refresh               # explicit provider read
twig query --title "authentication"     # bounded provider query
twig process                           # discover actual types
twig pending -o json                    # inspect local drafts
```

Benches are user-wide saved selections, not automatically your assigned sprint.
Create one, then name it when reading its members. No CLI invocation inherits
an item or Bench from a previous command or an open view.

```sh
twig bench create MyWork
twig workspace --bench MyWork
```

For automation, use explicit targets (`--id` for single-item mutations) and
structured output. Check each command's help: not every format or capability is
available on every route or provider.

**Changes are not all local drafts.** Commands such as `update`, `state`, `note`,
and `batch` can publish immediately through Core. To refresh a supported Bench
without saving drafts, use `twig workspace --bench MyWork --refresh`. For file-based
changes, `twig plan apply --file change.json` applies the exact plan in the named file.
Use `twig plan preview --file change.json` to inspect it first.
Unknown write outcomes must be inspected, never blindly retried.

## Server and integrations

The [Twig server](src/Twig.Server) owns work-item operations for the CLI and
Bench Canvas. It runs as the current user. Use `twig server start` to start or
reuse it and `twig server stop` for an explicit shutdown. Clients do not switch
to direct database or provider access if the server is unavailable.

The separate [twig-bench-canvas](https://github.com/PolyphonyRequiem/twig-bench-canvas)
package owns presentation assets. Closing a Canvas view does not stop the server.
Herdr panes and the interactive terminal browser have been removed; static CLI
output is unchanged. The server can run without Canvas assets and
provides a **read-only MCP bridge**. This is not the retired `twig mcp` server.
See the [server command reference](docs/commands/system/server.md) for lifecycle
syntax and output formats, and the [server guide](docs/server.md) for
prerequisites, installation, and MCP setup.

### Native Agent HTTP regression ownership

`python eng/ci-checks.py native-agent-http` runs ten individually isolated,
60-second TRX-verified scenarios: diagnostics, access, membership, rules,
rule-guards, draft, connections, connection-guards, owner-automatic and MCP.
`NativePublicationConsumerTests.AgentHttp.cs` composes the production server
with real native bindings, SQLite, captures and the owned synthetic provider
transport; every scenario requires zero provider PATCH attempts. Owner APIs
are synthetic admission, **not** Windows Negotiate, hosted trust or live-provider
qualification. Only private managed Twig setup is started; GitHub requests are
never approved and no shared installation or credentials are changed.

The scenarios preserve `twig-bench-canvas`'s `AgentRouteTests.cs` at
`f154a3502f2458cd328e51b71802ae108bbfa12d`
(blob `f7a6d4fcba2e67bb253a1c7e807613b99ee3c259`), using the canonical registry
Bench store and existing native editor APIs rather than consumer fixture links.
Twig setup requests supply the explicit private database name required since
`2b49ef36` (#655); the original consumer bodies omit that required field.
MCP exercises the actual resource-serving route with exact finite fixture bytes,
MIME, CSP and authorization; it does **not** qualify assembled HTML. The explanation
text, absent vendor placeholder and bundled DOMPurify assertions remain on actual
`buildMcpApp()` output in consumer `tests/mcp-details-browser.mjs`, introduced by
`d81feac9ca21aef2ea5606c592695943a04bb3dc`. Separate editor, seed, Plan and
relationship HTTP suites, browser rendering and periodic metadata scheduling
are not adopted by this Agent-only gate.

## Documentation

| Need | Start here |
| --- | --- |
| Setup and first commands | [Getting started](docs/getting-started.md) |
| Supported commands and limitations | [Command reference](docs/commands/README.md) |
| Benches, drafts, and plans | [User guides](docs/README.md#user-guides) |
| Local server | [Server installation and operations](docs/server.md) |
| Agent help | `twig --skill` and [installed skills](docs/features/skills.md) |
| Build and contribute | [Build and release](docs/architecture/build-and-release.md) |
| Domain language | [Twig glossary](GLOSSARY.md) |
| Implementation | [Architecture](docs/architecture/overview.md) |

Use `twig --help`, then `twig <command> --help` for executable-matched syntax.
The [documentation index](docs/README.md) lists the maintained guides.

## License

[MIT](LICENSE)
