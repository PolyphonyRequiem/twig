---
command: server
group: system
summary: Start, inspect, stop, or discover the selected local Twig server.
stability: stable
mutates: local
---

# `twig server`

Control the separately installed local Twig server on Windows x64 or Linux x64.
No initialized workspace or provider sign-in is required for these commands.
See the [server guide](../../server.md) for installation and platform prerequisites.

## Usage

```sh
twig server start [-o human|json|minimal]
twig server status [-o human|json|minimal]
twig server stop [-o human|json|minimal]
twig server endpoint [-o human|json|minimal]
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
| `endpoint` | Discover the selected Windows HTTPS binding without contacting or starting the server. |

For `start`, `status`, and `stop`, JSON success output is `{"running":true}` or `{"running":false}`.
Minimal output is `running` or `stopped`. Human output is
`Twig server is running.` or `Twig server is stopped.`
A stopped, valid installation is a successful `status` result, not an error.

## Endpoint discovery

`twig server endpoint -o json` is the supported bootstrap surface for independent
local consumers. Do not parse private installation records or `endpoint.json`.
On Windows it returns one JSON object, at most **4096 UTF-8 bytes** including its
trailing newline, with these required members:

```json
{
  "origin": "https://localhost:7341",
  "certificateThumbprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "selectionId": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
  "serverId": null
}
```

The thumbprint and selection ID above are illustrative, not usable bindings.
`origin` is a canonical HTTPS localhost origin with an explicit non-default port.
`certificateThumbprint` is the selected certificate's uppercase 40-hex thumbprint.
The installed launcher verifies the selected runtime's manifest and closed file
inventory, then the runtime checks that the current-user certificate is already
trusted, valid for localhost server authentication, and has its private key.
Discovery never creates a certificate or changes trust.

`selectionId` is an opaque 64-hex change token binding the installation root and
selected installation record, including runtime, data, port, certificate and
Canvas-root selection. Compare it for equality; do not derive paths or versions
from it. A changed record (even formatting) may change the token. Switching away
and back may restore it; it is not a monotonic revision or a process identity.
`serverId` is the durable GUID already recorded by the selected server. It is
`null` only if that identity file is absent, normally before first startup.
An empty, malformed, oversized, unreadable or linked identity is an error, never
an absent identity. Discovery does not create identity, locks or application data.

The command works before startup and while stopped or running, but it does not
probe a listener or claim readiness. Missing/invalid installation, runtime or TLS
prerequisites fail with exit code `1`; there is no invented endpoint fallback.
A selection change observed during discovery is also an error. Linux's headless
command host has no HTTPS/Canvas endpoint, so `endpoint` fails explicitly there.
Human output is `Twig server endpoint is <origin>.`; minimal output is the origin.
Neither includes a readiness assertion. Future optional JSON members may be added.
Discovery has a 20-second deadline, bounds operator stdout and stderr separately
to 4096 UTF-8 bytes, and owns its Windows operator process tree. Oversized output,
read failure, cancellation, timeout or operator exit terminates residual discovery
children; abrupt CLI termination closes a kill-on-close job. The selected launcher
cannot execute until assigned to that job. Cleanup has a two-second confirmation
budget and reports an error if termination cannot be confirmed. This ownership
applies only to read-only endpoint discovery, not the shared server's lifecycle.

Consumers should discover before launching their presentation host, use
`twig server start` for authenticated readiness, and discover again afterward
and when revalidating their selection. Require unchanged origin, certificate pin
and selection ID; discard stale bootstrap state if any changes. After startup,
require a non-null durable ID and compare it with the same-owner authenticated
`/api/v1/owner-status` response's `serverId`. If a durable ID was already known,
it must not change unnoticed. Keep trusted/pinned TLS, Windows owner verification,
`protocol: "twig-bench-service-v1"` and `canvasDefaultDeny: true` checks.
The `X-Twig-Server-Instance` response header is a separate **per-process** GUID,
not the durable `serverId`; the launcher uses it to address graceful stop to one
process. CLI bootstrap is not domain authority or proof of a write's outcome.

Twig Server is distributed separately from the CLI under `server-v*` release
tags. See the [installation guide](../../server.md#install-the-release-package)
for the package names and installation settings.

## Effects

`start` and `stop` change the selected shared process's lifecycle. Stopping it
affects other CLI and Canvas clients using that instance. Closing a CLI command
or Canvas view does not stop the shared server.

These commands do not install or upgrade packages, change the selected
installation, create or trust certificates, change sign-in startup settings,
or force-kill an unverified process. `status` and `endpoint` do not start the server, and help
does not inspect or launch an installation.

## Failure modes

A missing or invalid installation, unavailable locked instance, failed identity
check, or unconfirmed lifecycle outcome exits with code `1`. JSON errors are
written to stderr as `{"error":"..."}`; they are not reported as a successful
stopped result. Invalid subcommands, arguments, or output formats exit with
code `2`. Success and help exit with code `0`.

Use the [installation and troubleshooting guide](../../server.md) to resolve
installation or readiness failures. Do not treat restarting the server as proof
that an interrupted work-item write succeeded or failed; inspect the original
proposal journal before another change.
