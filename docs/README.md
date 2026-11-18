# Twig documentation

Start with the [README](../README.md) for installation and a short introduction.
These guides describe the current source tree. An older installed release can
differ; use its `twig --version` and targeted `--help` to identify its contract.

## User guides

| Guide | Covers |
| --- | --- |
| [Installation](installation.md) | Supported platforms, downloads, verification, updates |
| [Getting started](getting-started.md) | Provider setup, first reads, provider differences |
| [Command reference](commands/README.md) | Current routes, side effects, retired commands |
| [Authentication](features/authentication.md) | Named ADO identities and exact GitHub account selection |
| [Connections, Benches, and context](features/workspace-bench-context.md) | Shared saved selections versus worktree-local context |
| [Seeds and publishing](features/seeds-and-publishing.md) | Local drafts, durable identity, publication and uncertain outcomes |
| [Change proposals](features/proposals.md) | Validate, preview, confirm, apply, and inspect |
| [Process description](features/process-description.md) | Discovered schema, requirements, and known gaps |
| [Installed help and skills](features/skills.md) | Offline guidance and explicit agent-skill installation |
| [Local server](daemon.md) | Windows/Linux installation, `twig server` lifecycle commands, canvas boundary, read-only MCP |

## Contributors

Begin with [contributing](contributing.md) and the [architecture overview](architecture/overview.md).
Detailed references cover [storage](architecture/data-layer.md),
[CLI composition](architecture/commands.md),
[ADO integration](architecture/ado-integration.md), and
[builds and releases](architecture/build-and-release.md).
The [clean-core contract](architecture/clean-core-contract.md) records detailed
implementation boundaries, qualification, and cutover decisions; it is not a
quick-start guide.

## Design and historical records

Plans, specifications, research, incident reports, handoffs, and captured process
evidence are engineering records, **not an alternate supported command manual**.
They may describe removed projects, earlier schemas, or unimplemented proposals.
Keep their recorded observations distinct from current release claims.

- [Record index](history.md): where design and historical material lives.
- [Retired commands](commands/README.md#retired-surfaces): replacements and limits.
- [Old MCP server](architecture/mcp-server.md), [reference profile](features/reference-profile.md),
  and [shell integration](ohmyposh.md): retirement notices.

Public documentation is maintained here in the repository. [Home](Home.md) and
the [sidebar](_Sidebar.md) are navigation entry points, not a separately maintained
wiki manual.
