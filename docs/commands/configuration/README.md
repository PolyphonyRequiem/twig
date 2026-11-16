# Help and skills

Use `twig --help`, then `twig <group> --help` and `twig <command> --help`.
Help is offline and does not initialize, authenticate, or install anything.
See the [known `--help-all` limitation](../README.md#known-discovery-limitation).

| Command | Purpose |
| --- | --- |
| [`skills install`](skills-install.md) | Install generic Twig guidance into an explicit provider/root |
| [`skills status`](skills-status.md) | Inspect integrity, version and bounded discovery |
| [`skills update`](skills-update.md) | Explicitly update unedited managed guidance |
| [`skills add`](skills-add.md) | Add a separately supplied local companion |
| [`skills configure`](skills-configure.md) | Select/clear a companion without editing host settings |

`twig --skill [operations|changes|presentation]` prints bundled guidance.
Installation does not prove a host has loaded it, and old presenter recipes
cannot override current CLI refusals.
See [skill ownership](../../features/skills.md).

The old `config`, `config status-fields`, `migrate-config`, and provider
discovery/bind commands are retired. Fresh initialization and native named
enrollment/bindings are the current setup path.
