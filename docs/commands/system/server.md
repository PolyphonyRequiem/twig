---
command: server
group: system
summary: Start, inspect, or stop the selected local Twig server.
stability: stable
mutates: local
---

# `twig server`

Control the separately installed local Twig server on Windows x64 or Linux x64.
No initialized workspace or provider sign-in is required for these commands.
See the [server guide](../../daemon.md) for installation and platform prerequisites.

## Usage

```sh
twig server start [-o human|json|minimal]
twig server status [-o human|json|minimal]
twig server stop [-o human|json|minimal]
twig server --help
```

Each subcommand also accepts `-h` or `--help`. `--output` is the long form of
`-o`; the default format is `human`.

## Behavior

| Command | Behavior |
| --- | --- |
| `start` | Start or reuse the selected instance after verifying its identity and readiness. |
| `status` | Inspect the selected instance without starting it. |
| `stop` | Request graceful shutdown and confirm that the selected instance has stopped. |

JSON success output is `{"running":true}` or `{"running":false}`.
Minimal output is `running` or `stopped`. Human output is
`Twig server is running.` or `Twig server is stopped.`
A stopped, valid installation is a successful `status` result, not an error.

The command family is `twig server`, not `twig daemon`; the old spelling is not
an alias. Package names, `daemon-v*` release tags, installation paths,
`daemon-launch.ps1`, and `TWIG_DAEMON_INSTALL_ROOT` retain their existing names.

## Effects

`start` and `stop` change the selected shared process's lifecycle. Stopping it
affects other CLI and Canvas clients using that instance. Closing a CLI command
or Canvas view does not stop the shared server.

These commands do not install or upgrade packages, change the selected
installation, create or trust certificates, change sign-in startup settings,
or force-kill an unverified process. `status` does not start the server, and help
does not inspect or launch an installation.

## Failure modes

A missing or invalid installation, unavailable locked instance, failed identity
check, or unconfirmed lifecycle outcome exits with code `1`. JSON errors are
written to stderr as `{"error":"..."}`; they are not reported as a successful
stopped result. Invalid subcommands, arguments, or output formats exit with
code `2`. Success and help exit with code `0`.

Use the [installation and troubleshooting guide](../../daemon.md) to resolve
installation or readiness failures. Do not treat restarting the server as proof
that an interrupted work-item write succeeded or failed; inspect the original
proposal journal before another change.
