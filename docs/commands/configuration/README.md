# Configuration commands

Commands for reading, writing, and migrating Twig's local configuration state. All commands
here operate on the current workspace's `.twig/` directory and the repo-root `twig.json`
manifest; none of them mutate Azure DevOps.

Twig splits configuration into two JSON files:

- `twig.json` at the repo root holds committed repo coordinates (organization, project,
  team, area, iteration, etc.) — see `Twig.Infrastructure.Config.TwigRepoConfig` and
  `src/Twig.Infrastructure/Config/TwigPaths.cs:58`.
- `.twig/config` inside the workspace holds per-user preferences (display, seed, tracking,
  auth method) — see `Twig.Infrastructure.Config.TwigUserConfig`.

The `help` entry documents the fast-path grouped help that intercepts `twig --help`,
`twig -h`, and the bare `twig help` pseudo-command in `src/Twig/Program.cs:131-135`.

## Commands

|Command|Summary|
|---|---|
|[`config`](./config.md)|Read or set a configuration value.|
|[`config status-fields`](./config-status-fields.md)|Configure which fields appear in the status view.|
|[`migrate-config`](./migrate-config.md)|Split a legacy `.twig/config` into `twig.json` + per-user prefs (AB#3296).|
|[`help`](./help.md)|Grouped help fast-path (`twig --help`, `twig -h`, `twig help`).|
