# Server, identity, connection evidence, and version

| Route | Purpose |
| --- | --- |
| [`server start`, `server status`, `server stop`, `server endpoint`](server.md) | Control the selected local server or discover its Windows HTTPS binding independently of a workspace |
| [`auth login`, `auth pat`](auth-login.md) | Explicit named enrollment/renewal |
| [`auth identities`](auth-status.md) | Recorded registered identity metadata |
| [`connection add`](auth-login.md) | Register provider dimensions independently of credentials or workspaces |
| [`connection bind`](auth-login.md) | Bind this user's credential to explicit provider dimensions |
| [`connection list`](auth-status.md) | All device-local connections and local user bindings, no workspace filter |
| [`auth status`, `connection status`](auth-status.md) | Local recorded selection, not fresh access evidence |
| [`connection writes`](auth-status.md) | Inspect-only correlated native transport evidence |
| [`version`](version.md) / `--version` | Offline installed version |

See [authentication](../../features/authentication.md) for setup and provider
differences. Tokens and PATs must not be passed in arguments or pasted into logs.

Identity/target hot-switches, migration, `auth clear/logout`, `upgrade`, and
`changelog` are retired. Use a fresh workspace for another target and rerun the
[installer](../../installation.md) to update the CLI.
