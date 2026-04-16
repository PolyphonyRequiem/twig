# Twig Documentation

Welcome to the twig documentation. This is the central hub for all technical documentation about the twig CLI, MCP server, and TUI.

## Architecture

Comprehensive architecture reference for developers working on or integrating with twig.

| Document | Description |
|----------|-------------|
| [Architecture Overview](architecture/overview.md) | Layered architecture, project structure, key constraints, technology stack |
| [Data Layer](architecture/data-layer.md) | SQLite storage, caching strategy, sync coordination, process-agnostic design |
| [Commands](architecture/commands.md) | CLI framework, command lifecycle, rendering pipeline, telemetry |
| [ADO Integration](architecture/ado-integration.md) | REST client, authentication, conflict resolution, link management |
| [MCP Server](architecture/mcp-server.md) | Tool catalog, workspace guard, shared domain layer |
| [Build & Release](architecture/build-and-release.md) | AOT compilation, versioning, release pipeline, companion binaries |

## Guides

| Document | Description |
|----------|-------------|
| [Oh My Posh Integration](ohmyposh.md) | Shell prompt integration with Oh My Posh |

## Reference

| Document | Description |
|----------|-------------|
| [Example OMP Config](examples/twig.omp.json) | Sample Oh My Posh configuration for twig |

## For Contributors

- **Build**: `dotnet build` — builds all projects
- **Test**: `dotnet test` — runs all ~4,300 tests
- **Local publish**: `./publish-local.ps1` — builds and deploys to `~/.twig/bin/`
- **Architecture decisions** are documented in the [architecture docs](architecture/overview.md)
- **Process-agnostic principle**: see [.github/instructions/process-agnostic.instructions.md](../.github/instructions/process-agnostic.instructions.md)
- **Telemetry policy**: see [.github/instructions/telemetry.instructions.md](../.github/instructions/telemetry.instructions.md)
