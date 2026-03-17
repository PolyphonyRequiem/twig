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
# Or pick interactively from the current sprint:
twig flow-start

# Work on your code...

# Done: save changes → transition to Resolved → offer PR creation
twig flow-done

# Close: guard open PRs → transition to Completed → delete branch → clear context
twig flow-close
```

### Flow Command Options

```bash
# flow-start
twig flow-start 12345 --no-branch      # skip branch creation
twig flow-start 12345 --no-state       # skip state transition
twig flow-start 12345 --no-assign      # skip self-assignment
twig flow-start 12345 --force          # proceed with uncommitted changes
twig flow-start 12345 --output json    # structured output for scripts

# flow-done
twig flow-done --no-save               # skip saving pending changes
twig flow-done --no-pr                 # skip PR creation prompt
twig flow-done --output minimal        # emit PR URL only (for shell capture)

# flow-close
twig flow-close --force                # bypass guards (unsaved changes, open PRs)
twig flow-close --no-branch-cleanup    # keep branch after close
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
| `branch` | Create a git branch named from the active work item |
| `commit` | Commit with a work-item-enriched message and link to ADO |
| `pr` | Create an ADO pull request linked to the active work item |
| `hooks install` | Install Twig-managed git hooks into `.git/hooks/` |
| `hooks uninstall` | Remove Twig-managed git hooks from `.git/hooks/` |
| `stash` | Stash changes with work item context in the stash message |
| `stash pop` | Pop the most recent stash and restore Twig context |
| `log` | Show annotated git log with work item type/state badges |
| `context` | Show current branch, active work item, and linked PRs |
| `flow-start` | Start work: context + state + assign + branch |
| `flow-done` | Finish work: save + resolve + PR offer |
| `flow-close` | Close work: guard + complete + branch cleanup |
| `ohmyposh init` | Generate Oh My Posh shell hook and segment config |
| `tui` | Launch full-screen TUI mode |
| `version` | Show version |

## Output Formats

All commands support `--output human|json|minimal`:

- **human** — ANSI-colored with type badges, state colors, and contextual hints
- **json** — machine-readable, no ANSI escapes (pipe to `jq` or AI agents)
- **minimal** — plain text for scripting and `grep`

## Git Integration

Twig integrates with git to automate branch management and PR creation during the developer flow:

```bash
# flow-start creates a branch named from the active work item
twig flow-start 12345
# → creates branch: feature/12345-add-login (configurable template)

# flow-done offers to create a PR when the branch is ahead of the target
twig flow-done
# → prompts: Branch 'feature/12345-add-login' is ahead of 'main'. Create PR? [y/N]

# flow-close deletes the local branch after confirming
twig flow-close
# → prompts: Delete branch 'feature/12345-add-login'? [y/N]
```

Git commands are also available as standalone operations:

```bash
twig branch                        # create branch from active work item
twig commit -m "add validation"    # commit with enriched message (e.g. feat(#12345): add validation)
twig commit --amend -m "fix typo"  # amend with enriched message
twig pr                            # create PR targeting git.defaultTarget
twig pr --target develop           # create PR targeting a specific branch
twig pr --draft                    # create a draft PR

# Git hooks — auto-set context and prefix commit messages on every branch switch and commit
twig hooks install                     # install prepare-commit-msg, commit-msg, post-checkout
twig hooks uninstall                   # remove Twig-managed hook sections

# Stash changes with work item context embedded in the stash message
twig stash                             # stash → message: "[#12345 Implement auth]"
twig stash -m "wip: half done"         # stash → "[#12345 Implement auth] wip: half done"
twig stash pop                         # pop stash and restore Twig context from branch name

# Annotated log — work item type/state badges alongside each commit
twig log                               # last 20 commits, annotated with work item info
twig log --count 10                    # limit to 10 commits
twig log --work-item 12345             # filter to commits referencing #12345

# Git context — shows current branch, active work item, and linked PRs
twig context                           # show current git context summary
twig context --output json             # machine-readable context
```

### Git Configuration

All `git.*` settings live in `.twig/config`:

| Setting | Default | Description |
|---------|---------|-------------|
| `git.branchTemplate` | `feature/{id}-{title}` | Branch name template. Tokens: `{id}`, `{type}`, `{title}` |
| `git.branchPattern` | `(?:^|/)(?<id>\d{3,})(?:-|/|$)` | Regex to extract work item ID from branch name |
| `git.defaultTarget` | `main` | Target branch for PRs |
| `git.project` | *(from `project`)* | ADO project containing the git repository (if different from backlog project) |
| `git.repository` | *(auto-detected)* | Git repository name (auto-detected from `git remote get-url origin`) |

The following settings are also active and used by the implemented `twig branch`, `twig commit`, and `twig pr` commands:

| Setting | Default | Used by |
|---------|---------|---------|
| `git.committemplate` | `{type}(#{id}): {message}` | `twig commit` — commit message format. Tokens: `{type}` (conventional prefix), `#{id}` (work item), `{message}` (body) |
| `git.autolink` | `true` | `twig branch`, `twig commit`, `twig pr` — automatically add ADO artifact links to the work item |
| `git.autotransition` | `true` | `twig branch` — automatically transition Proposed items to Active on branch creation |

```bash
twig config git.branchtemplate "{type}/{id}-{title}"
twig config git.defaulttarget develop
twig config git.project BackendService
twig config git.repository my-api-repo
twig config git.committemplate "{type}(#{id}): {message}"
twig config git.autolink false
twig config git.autotransition false
```

### Hooks Configuration

The three git hooks installed by `twig hooks install` are individually enabled by default and can be toggled:

| Setting | Default | Description |
|---------|---------|-------------|
| `git.hooks.preparecommitmsg` | `true` | Auto-prefix commit messages with `#{workItemId}` |
| `git.hooks.commitmsg` | `true` | Warn when a commit message doesn't reference a work item |
| `git.hooks.postcheckout` | `true` | Auto-set Twig context on branch switch |

```bash
twig config git.hooks.preparecommitmsg false   # disable commit message prefixing
twig config git.hooks.commitmsg false           # disable work item reference warning
twig config git.hooks.postcheckout false        # disable auto context on branch switch
```

### Flow Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `flow.autoassign` | `if-unassigned` | Assignment in `flow-start`: `if-unassigned`, `always`, `never` |
| `flow.autosaveondone` | `true` | Auto-save pending changes in `flow-done` |
| `flow.offerprondone` | `true` | Show PR creation prompt in `flow-done` |

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

## Installation

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex
```

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash
```

### Update

```bash
twig upgrade
```

Installs to `~/.twig/bin/` and adds it to your PATH. The installer is idempotent — safe to run again to reinstall or repair.

## Prerequisites

- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (for authentication)

## Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build
dotnet test
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
