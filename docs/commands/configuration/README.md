# Configuration commands

Commands for reading, writing, and migrating Twig's local configuration state,
plus provider-target discovery and binding, offline help and explicit skill installation. Workspace configuration uses
`.twig/` and `twig.json`; skill commands use only their explicit target root.
None of these commands mutate remote providers. GitHub discovery performs a fixed
remote read and observes the existing local pointer without initializing the
central registry. Binding repeats that read, confirms the expected immutable IDs
and attachment revision, and writes only the local target selection. Neither
command changes configuration or enrolls credentials; see
[`config`](config.md#work-item-provider) for the explicit setup sequence.

Twig splits configuration into two JSON files:

- `twig.json` at the repo root holds committed repo coordinates (organization, project,
  team, area, iteration, etc.) — see `Twig.Infrastructure.Config.TwigRepoConfig` and
  `src/Twig.Infrastructure/Config/TwigPaths.cs:58`.
- `.twig/config` inside the workspace holds per-user preferences (display, seed, tracking,
  auth method) — see `Twig.Infrastructure.Config.TwigUserConfig`.

The `help` entry documents progressive root, group and leaf discovery, the full
catalog escape hatch and executable-matched `--skill` entry guidance.

## Commands

|Command|Summary|
|---|---|
|[`config`](./config.md)|Read or set a configuration value.|
|[`config status-fields`](./config-status-fields.md)|Configure which fields appear in the status view.|
|[`provider discover github`](provider-discover-github.md)|Observe immutable GitHub repository identity without binding.|
|[`provider bind github`](provider-bind-github.md)|Select a freshly confirmed GitHub repository identity locally.|
|[`migrate-config`](./migrate-config.md)|Split a legacy `.twig/config` into `twig.json` + per-user prefs (AB#3296).|
|[`help`](./help.md)|Progressive offline help and `--skill` guidance.|
|[`skills install`](skills-install.md)|Install the canonical Twig skill into an explicit root.|
|[`skills update`](skills-update.md)|Update Twig-owned guidance while preserving companions.|
|[`skills configure`](skills-configure.md)|Select or clear a companion without rewriting it.|
|[`skills status`](skills-status.md)|Inspect identity, integrity and bounded discovery.|
|[`skills add`](skills-add.md)|Install a separately supplied integration or variant package.|
