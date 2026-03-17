# TWIG — Terminal Work Integration Gadget

A high-performance, opinionated CLI for Azure DevOps work item management. Twig brings a git-like interaction model to ADO work tracking: fast local reads from a SQLite cache, terse commands, context-aware operations, and seamless integration with developer workflows.

Built with C# .NET 10 Native AOT — single binary, sub-100ms cold start.

## Quick Start

```bash
# Initialize (connects to your ADO org and project)
twig init --org contoso --project MyProject

# Refresh sprint data from ADO
twig refresh

# View your work items
twig ws                     # your assigned items
twig sprint                 # full team sprint view
twig tree                   # hierarchical tree from active context

# Set active context and navigate
twig set 12345              # by ID
twig set "auth bug"         # by title substring match
twig up                     # navigate to parent
twig down "subtask"         # navigate to child
```

## Developer Flow

Twig provides an opinionated lifecycle for working on items:

```bash
# Start: set context → transition to InProgress → self-assign → create git branch
twig flow-start 12345

# Work on your code...

# Done: save changes → transition to Resolved → offer PR creation
twig flow-done

# Close: guard open PRs → transition to Completed → delete branch → clear context
twig flow-close
```

## Commands

| Command | Description |
|---------|-------------|
| `init` | Initialize workspace (connect to ADO org/project) |
| `refresh` | Sync sprint data from ADO (with dirty-item protection) |
| `set` | Set active work item context (by ID or title match) |
| `status` | Show active work item details |
| `tree` | Display hierarchical work item tree |
| `ws` | Personal workspace — your assigned items |
| `sprint` | Full team sprint view |
| `state` | Transition state (`p`roposed, a`c`tive, re`s`olved, `d`one, remo`x`ed) |
| `save` | Push pending changes to ADO |
| `note` | Add/view discussion comments |
| `update` | Update a field value |
| `edit` | Open work item in `$EDITOR` |
| `seed` | Create a child work item stub |
| `up` / `down` | Navigate parent/child hierarchy |
| `config` | Read/write configuration values |
| `flow-start` | Start work: context + state + assign + branch |
| `flow-done` | Finish work: save + resolve + PR offer |
| `flow-close` | Close work: guard + complete + branch cleanup |
| `prompt` | Shell prompt segment (for PS1/Oh My Posh) |
| `tui` | Launch full-screen TUI mode |
| `version` | Show version |

## Output Formats

All commands support `--output human|json|minimal`:

- **human** — ANSI-colored with type badges, state colors, and contextual hints
- **json** — machine-readable, no ANSI escapes (pipe to `jq` or AI agents)
- **minimal** — plain text for scripting and `grep`

## Cross-Project Support

Work items and git repos can live in different ADO projects:

```bash
twig init --org contoso --project MyProject          # backlog project
twig config git.project BackendService               # code project (for PRs)
twig config git.repository my-api-service            # repo name (auto-detected from git remote)
```

## Configuration

```bash
twig config display.icons nerd          # nerd font icons (default: unicode)
twig config display.treedepth 5         # tree display depth
twig config git.branchtemplate "feature/{id}-{title}"
twig config flow.autoassign if-unassigned
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (for authentication)
- Windows with [Visual C++ Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/) (for AOT compilation)

## Build & Test

```bash
dotnet build
dotnet test
```

## Publish (Native AOT)

```bash
dotnet publish src/Twig -r win-x64 -c Release
```

## Project Structure

```
src/
  Twig/                 — CLI entry point (ConsoleAppFramework + Spectre.Console)
  Twig.Domain/          — Pure domain model (zero NuGet dependencies)
  Twig.Infrastructure/  — SQLite cache, ADO REST client, auth, git integration
  Twig.Tui/             — Full-screen TUI (Terminal.Gui v2)
tests/
  Twig.Domain.Tests/
  Twig.Infrastructure.Tests/
  Twig.Cli.Tests/
  Twig.Tui.Tests/
docs/
  projects/             — Design docs, plans, scenarios
```

## License

[MIT](LICENSE)
