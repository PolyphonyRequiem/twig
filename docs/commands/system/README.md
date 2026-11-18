# Server, identity, connection evidence, and version

| Route | Purpose |
| --- | --- |
| [`server start`, `server status`, `server stop`](server.md) | Control the selected local server independently of a workspace |
| [`auth login`, `auth pat`](auth-login.md) | Explicit named enrollment/renewal |
| [`auth identities`](auth-status.md) | Recorded registered identity metadata |
| [`connection bind`](auth-login.md) | Explicit ADO endpoint-to-identity binding |
| [`connection list`](auth-status.md) | Recorded bindings, no selection change |
| [`auth status`, `connection status`](auth-status.md) | Local recorded selection, not fresh access evidence |
| [`connection writes`](auth-status.md) | Inspect-only correlated native transport evidence |
| [`version`](version.md) / `--version` | Offline installed version |

See [authentication](../../features/authentication.md) for setup and provider
differences. Tokens and PATs must not be passed in arguments or pasted into logs.

Identity/target hot-switches, migration, `auth clear/logout`, `upgrade`, and
`changelog` are retired. Use a fresh workspace for another target and rerun the
[installer](../../installation.md) to update the CLI.
