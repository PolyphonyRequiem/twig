# Authentication and binding

Normal work uses the exact admitted provider/source/principal binding, never an
ambient token, last-login account or Git-remote-derived tracker selection.
Identity enrollment, binding creation, fresh initialization and reading work
are distinct actions with distinct authority.

ADO supports explicit named AAD loopback PKCE enrollment and named PAT
enrollment. Use `auth login --help` / `auth pat --help`; credentials belong only
in the supported secure-input boundary, never command examples, tracker notes
or logs. The recorded principal must match subsequent admitted work.

GitHub fresh initialization captures an explicitly named native `gh` account.
Explicit GitHub App enrollment is separate; the two credential sources never
silently substitute for each other. Provider selection does not resolve an ADO
target through GitHub utilities.

`auth identities`, `auth status`, `connection list` and `connection status`
describe recorded metadata/selection. They do not prove fresh authentication,
remote access or write permission. An explicit refreshed work read exercises
its own admitted access, not all capabilities.

Binding creation may establish an initial default but must not hot-switch an
existing workspace/principal. Current delivery has no identity/target migration,
adoption or no-op compatibility route. Missing, stale, cross-source or changed
evidence refuses.

The current native daemon authenticates its Windows owner and trusted pinned
localhost TLS separately from provider work credentials. Its read-only MCP
surface does not expose Save/Apply. The standalone MCP host and old companion
adapters are retired.
