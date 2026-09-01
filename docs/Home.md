# Twig Wiki

> This wiki is generated from the [`docs/`](../tree/main/docs) directory in the twig repository.

## Architecture

| Document | Description |
|----------|-------------|
| [[Architecture Overview|architecture/overview]] | Layered architecture, project structure, key constraints |
| [[Data Layer|architecture/data-layer]] | SQLite storage, caching, sync coordination, process-agnostic design |
| [[Commands|architecture/commands]] | CLI framework, command lifecycle, rendering, telemetry |
| [[ADO Integration|architecture/ado-integration]] | REST client, authentication, conflict resolution |
| [[MCP Server|architecture/mcp-server]] | Tool catalog, workspace guard, shared domain layer |
| [[Build & Release|architecture/build-and-release]] | AOT compilation, versioning, release pipeline |

## Guides

| Document | Description |
|----------|-------------|
| [[Oh My Posh Integration|ohmyposh]] | Shell prompt integration |

## Command Reference

| Document | Description |
|----------|-------------|
| [[Command Reference|commands/README]] | Exhaustive command and subcommand reference, including side-effect classifications for automation |

## Feature Guides

| Document | Description |
|----------|-------------|
| [[Workspace, Bench, and Context|features/workspace-bench-context]] | Connection, Bench, and Context model |
| [[Seeds and Publishing|features/seeds-and-publishing]] | Local drafts through ordered ADO publication |
| [[Plans and Proposals|features/proposals]] | Digest-confirmed proposal lifecycle and journal |
| [[Authentication|features/authentication]] | Token chain, sign-in, and secure recovery |
| [[Reference Profile|features/reference-profile]] | Embedded policy profile and repository pin |
| [[Process Description|features/process-description]] | Byte-stable process descriptors for diffing |

## Quick Links

- [README](../blob/main/README.md) — Getting started, installation, command reference
- [Architecture Overview](architecture/overview.md) — Start here for codebase orientation
