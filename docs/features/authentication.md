# Authentication

Twig authenticates to Azure DevOps as *you*. Every ADO REST call needs an
`Authorization` header, and twig's whole authentication surface exists to
produce that header safely, quickly, and without leaking a secret to disk,
another process, or the terminal.

There are two independent code paths:

* **Azure Active Directory** (default). Twig owns a private refresh token in
  `~/.twig/.refresh-token`, mints short-lived access tokens against Azure AD
  over HTTPS, and caches them in memory and in `~/.twig/.token-cache`. The
  Azure CLI is used *once* to bootstrap the refresh token; afterwards twig
  never reads `~/.azure/` or shells out to `az` again.
* **Personal Access Token** (opt-in). Twig reads a PAT from `$TWIG_PAT` or
  `.twig/config` and sends it as HTTP Basic auth on every request. Nothing
  is cached, nothing is minted, and no MSAL machinery runs.

Which path is used is controlled by the `auth.method` config field
(`azcli`, the default, or `pat`). The `NetworkServiceModule` DI container
reads it once at startup and asks `AuthProviderFactory` to build the
matching provider chain
(`src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs:32-33`,
`src/Twig.Infrastructure/Auth/AuthProviderFactory.cs:15-21`,
`src/Twig.Infrastructure/Config/TwigConfiguration.cs:768-770`). The MCP
host does the same walk across every registered workspace and falls back to
`azcli` unless at least one workspace opts into `pat`
(`src/Twig.Mcp/Program.cs:33-37`).

---

## The AAD token chain

`AdoAccessTokenProvider` is the working brain of the AAD path
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:26-292`). Every
`GetAccessTokenAsync` call walks a fixed ladder under a semaphore so
concurrent callers never race the same rung:

### 1. In-memory cache (50-minute TTL)

An access token minted or read successfully in this process is held in a
private field along with its expiry. As long as `now < _cacheExpiry` the
same string is returned without touching disk or the network
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:28-29,74-76`). The
expiry is the *earlier* of a 50-minute TTL or the JWT's own `exp` claim
minus a 5-minute buffer (`StoreAndPersist`, lines `250-258`).

The in-memory cache is not persisted on process exit. A fresh `twig`
invocation always starts one rung lower.

### 2. `~/.twig/.token-cache` (cross-process file cache)

`TwigTokenFileCache` persists the access token so a second `twig`
invocation from the same shell — or `twig-mcp` from VS Code, or another
tool running the same binary — can reuse the mint without re-refreshing
(`src/Twig.Infrastructure/Auth/TwigTokenFileCache.cs:10-91`). The file is
plain UTF-8 with two lines: `expiry.UtcTicks\naccessToken\n`. Writes go via
`.tmp` + rename so a concurrent reader never sees a torn file, and on Unix
the file is `chmod 600`
(`src/Twig.Infrastructure/Auth/TwigTokenFileCache.cs:57-77`).

The provider *never trusts the file blindly*. Even on a live entry it
decodes the JWT and rejects it unless its `aud` claim matches the Azure
DevOps API resource ID (`499b84ac-1321-427f-aa17-267ca6975798`). A cache
entry with the wrong audience is deleted on the spot so it cannot poison a
second reader in this or any other process
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:78-91`). This is
the invariant that made the historical wrong-audience bug class
structurally impossible.

### 3. `~/.twig/.refresh-token` (twig's private refresh-token store)

When neither cache holds a live, audience-correct token, twig mints a new
one by exchanging a refresh token directly against Azure AD.
`TwigRefreshTokenStore` owns the file
(`src/Twig.Infrastructure/Auth/TwigRefreshTokenStore.cs:12-84`); the schema
is `TwigRefreshTokenStoreEntry` — `refresh_token`, `client_id`,
`tenant_id`, `authority_host`, `upn`, `oid`, `bootstrapped_at`, `source`
(`src/Twig.Infrastructure/Auth/TwigRefreshTokenStoreEntry.cs:10-42`).
Writes are atomic and `chmod 600` on Unix, matching the token cache.

`MsalTokenRefresher` POSTs the refresh token to
`https://{authorityHost}/{tenantId}/oauth2/v2.0/token` with the Azure
DevOps scope, extracts the new access token and — if the server rotated it
— the new refresh token, and hands both back to the provider
(`src/Twig.Infrastructure/Auth/MsalTokenRefresher.cs:16-114`). The
refresher never throws for network or protocol errors; it returns nulls
plus an `IsInvalidGrant` flag that the caller uses to distinguish "the
refresh token is dead" from "the network is flaky".

When AAD returns a rotated refresh token, the provider writes it back to
the store immediately. Without that write the store would slowly age out
of AAD's 90-day inactivity window even for a user who runs twig every day
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:178-191`).

### 4. One-time bootstrap from the Azure CLI MSAL cache

If `~/.twig/.refresh-token` does not yet exist, the provider reads
`~/.azure/msal_token_cache.json` once — the file the Azure CLI writes
after `az login` — extracts the best refresh-token context via
`MsalTokenRefresher.FindRefreshContext`, and writes it into twig's own
store with `source = "azcli"` and a UTC bootstrap timestamp
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:96-98,193-222`,
`src/Twig.Infrastructure/Auth/MsalTokenRefresher.cs:120-173`). Reading is
`FileShare.ReadWrite` so twig does not fight the CLI for the lock
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:283-291`).

After the bootstrap succeeds, twig never reads `~/.azure/` again on this
machine unless the user explicitly re-bootstraps via `twig auth clear` —
or a stored entry hits `invalid_grant` and the provider makes exactly one
re-bootstrap attempt in the same call
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:128-150`). A
freshly bootstrapped entry that fails again does *not* loop.

### 5. `twig auth login` — an alternate bootstrap

`twig auth login` is a direct-to-store alternative to the MSAL bootstrap.
It runs an interactive AAD sign-in (loopback PKCE by default, device-code
under `--device-code`) against Azure CLI's well-known public client ID
(`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) and writes the resulting entry
straight into `~/.twig/.refresh-token`, then wipes the access-token cache
so the next call mints against the new identity
(`src/Twig/Commands/AuthLoginCommand.cs:14-85`). It is the recommended way
to bootstrap a machine that does not have the Azure CLI installed, or to
switch identities without touching `~/.azure/`.

### If every rung fails

`AdoAccessTokenProvider.GetAccessTokenAsync` throws
`AdoAuthenticationException` with actionable guidance: run
`az login --scope 499b84ac-...798/.default`, then `twig auth clear` to
force a re-bootstrap, and consult `twig auth status` for details
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:102-106`). The
top-level command dispatcher catches the exception, prints the guidance,
and exits `1` (`src/Twig/Program.cs:327-331`).

---

## The PAT path

Setting `auth.method = pat` in the workspace config makes
`AuthProviderFactory` return `PatAuthProvider` instead of the AAD chain
(`src/Twig.Infrastructure/Auth/AuthProviderFactory.cs:15-21`,
`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:12-63`). Every ADO call
then resolves the PAT in the same fixed order:

1. `$TWIG_PAT` environment variable
   (`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:42-45`).
2. `auth.pat` in `.twig/config`
   (`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:47-50`).

The winning value is formatted as HTTP Basic:
`Basic base64(":<PAT>")`
(`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:59-63`). If neither
source has a value, the provider throws `AdoAuthenticationException` with
the message `No PAT found. Set the TWIG_PAT environment variable or
configure 'auth.pat' in .twig/config.`
(`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:52-53`).

The PAT path has no caches, no refresh, and no MSAL machinery.
`InvalidateToken()` is a no-op — there is no state to clear
(`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:37-38`).

### Precedence between AAD and PAT

`auth.method` picks the path; there is no automatic fallback between them.
A workspace on the default `azcli` method does *not* consult `$TWIG_PAT`
even if it is set, and a workspace on `pat` does *not* consult
`~/.twig/.refresh-token` even if it exists. If you want twig to use a PAT
temporarily, set `auth.method = pat` (repo scope) or export `TWIG_PAT`
*and* switch the method. The MCP host is stricter still: it scans every
registered workspace and only serves PAT mode if at least one of them
opted in (`src/Twig.Mcp/Program.cs:33-37`).

---

## Commands

Twig ships three narrow commands for driving this surface. All three are
documented in full on their own reference pages; the summary below is
scoped to how they fit into the token chain.

### `twig auth login`

Interactive AAD sign-in that writes `~/.twig/.refresh-token` directly and
wipes `~/.twig/.token-cache` so the next call mints fresh against the new
identity. Loopback PKCE by default, device-code under `--device-code`,
optionally scoped to a specific tenant with `--tenant`. Never prints the
refresh token or the access token — the success record contains only
UPN, tenant, source, and store path
(`src/Twig/Commands/AuthLoginCommand.cs:14-85`). See
[`auth login`](../commands/system/auth-login.md).

### `twig auth status`

Read-only diagnostic. Reports on both files:

* `~/.twig/.refresh-token` — source, bootstrap timestamp, tenant, object
  ID, client ID, authority host. The refresh token itself is never
  printed (`src/Twig/Commands/AuthStatusCommand.cs:20-35`).
* `~/.twig/.token-cache` — cache path and stored expiry. If the token is
  a JWT, the audience, expiry, and principal are decoded and printed via
  `JwtAccessTokenInspector.DescribeForDiagnostics`. If it is not a JWT
  (PAT or other opaque credential), the command says so and returns `0`
  without pretending to validate it
  (`src/Twig/Commands/AuthStatusCommand.cs:38-57`).

Exit code `1` is reserved for two hard invariants: wrong audience
(`IsValidAdoAudience` false) or expired / within 5 minutes of expiring.
Both suggest the specific recovery — `twig auth clear` plus an explicit
`az login --scope 499b84ac-...798/.default`
(`src/Twig/Commands/AuthStatusCommand.cs:62-76`). See
[`auth status`](../commands/system/auth-status.md).

### `twig auth clear`

Deletes both files idempotently, then calls
`IAuthenticationProvider.InvalidateToken()` so the running process — most
importantly a long-lived `twig-mcp` host — cannot keep serving the
already-loaded token until restart
(`src/Twig/Commands/AuthClearCommand.cs:27-41`). Reach for it after
switching identities with `az login`, after `auth status` blames audience
or expiry, or when you need to force a re-bootstrap on the next ADO call.
The hint that follows the clear reminds you to run `az login` first if
`~/.azure/msal_token_cache.json` is stale
(`src/Twig/Commands/AuthClearCommand.cs:49-57`). See
[`auth clear`](../commands/system/auth-clear.md).

---

## Security handling

The whole surface is designed under one rule: **the token never leaves the
process except as an `Authorization` header on an HTTPS request to ADO or
AAD.**

* **No secret is ever written to stdout or stderr.** `auth login` prints
  UPN, tenant, source, and store path — nothing else. `auth status`
  prints the paths, the refresh-store metadata *without the refresh
  token*, and JWT metadata *without the access token*. `auth clear`
  prints the paths of the files it deleted. Do not paste the contents of
  `~/.twig/.refresh-token` or `~/.twig/.token-cache` into bug reports or
  chat — the fields the commands print are the safe surface.
* **Files are `chmod 600` on Unix.** Both `TwigTokenFileCache` and
  `TwigRefreshTokenStore` call `File.SetUnixFileMode` after every write
  (`src/Twig.Infrastructure/Auth/TwigTokenFileCache.cs:70-71`,
  `src/Twig.Infrastructure/Auth/TwigRefreshTokenStore.cs:63-64`). On
  Windows twig relies on the ACLs of `%USERPROFILE%\.twig\`, matching the
  Azure CLI's own posture.
* **Writes are atomic.** Both files use `.tmp` + rename so a concurrent
  reader in another process never observes a partial write
  (`src/Twig.Infrastructure/Auth/TwigTokenFileCache.cs:65-68`,
  `src/Twig.Infrastructure/Auth/TwigRefreshTokenStore.cs:58-61`).
* **Every cached access token is audience-validated.** The provider
  decodes the JWT and rejects it unless the `aud` claim matches the ADO
  API resource ID, then deletes the file so the wrong-audience token
  cannot poison another reader
  (`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:78-91`). This
  is the structural fix for the wrong-audience failure class the surface
  was rebuilt around.
* **The refresh token is exchanged over HTTPS to
  `login.microsoftonline.com`.** There is no local proxy, no subprocess,
  and no `az` involvement in the steady state
  (`src/Twig.Infrastructure/Auth/MsalTokenRefresher.cs:70-114`).
* **`InvalidateToken` really invalidates.** `auth clear` drops the
  in-memory copy in addition to the two files, so a long-lived host like
  `twig-mcp` cannot keep serving requests with a token the user just
  chose to revoke (`src/Twig/Commands/AuthClearCommand.cs:38-41`).
* **PAT precedence is explicit.** `$TWIG_PAT` always wins over
  `.twig/config`. This lets you rotate a leaked PAT out of config and
  override with an environment variable in one command without editing a
  file (`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:42-53`). A PAT
  set only in config is still on disk; treat it accordingly.
* **Telemetry is deliberately silent about identity.** UPN, OID, tenant,
  client ID, and authority host are diagnostic-only. They are printed to
  the local terminal for the user; they never appear in telemetry
  properties. See the telemetry rules in `.github/copilot-instructions.md`.

---

## Failure modes and how to recover

### "Could not acquire an Azure DevOps access token"

`AdoAccessTokenProvider` reached the end of its ladder. Either the MSAL
cache is missing, has no refresh token for a matching account, or the
refresh call failed. The exception's own text suggests
`az login --scope 499b84ac-...798/.default` followed by
`twig auth clear`; the top-level dispatcher adds `Run 'az login' to
refresh.` (`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:100-106`,
`src/Twig/Program.cs:329-331`). If the user is on the PAT method, the
dispatcher instead suggests updating `TWIG_PAT` or `.twig/config`
(`src/Twig/Program.cs:327-328`).

### "This token's audience is NOT the Azure DevOps API"

Reported by `twig auth status`. The cached access token has an `aud`
claim other than `499b84ac-1321-427f-aa17-267ca6975798`. Almost always
caused by an `az login` that scoped to something else (Microsoft Graph,
ARM, a custom app). Recovery: `twig auth clear`, then
`az login --scope 499b84ac-...798/.default`, then re-run the failing
command
(`src/Twig/Commands/AuthStatusCommand.cs:62-68`).

### "This token is expired or expiring within 5 minutes"

Also reported by `auth status`. The in-memory rung would have refreshed
before hitting this, so the report means either the cache file was
poisoned by another process or the clock skewed. `twig auth clear`
returns you to a clean state
(`src/Twig/Commands/AuthStatusCommand.cs:70-76`).

### `invalid_grant` on a stored refresh token

Handled transparently. The provider observes the flag, deletes the
stored entry, and makes exactly one re-bootstrap attempt from the MSAL
cache — on the theory that the user just re-ran `az login` after a
revocation
(`src/Twig.Infrastructure/Auth/AdoAccessTokenProvider.cs:138-150`). If
the re-bootstrap or its refresh also fails, the call returns null and
the top-level exception path takes over.

### `PolicyBlocked` on `auth login --device-code`

The tenant's Conditional Access policy forbids the device-code grant.
`auth login` detects this and prints `Your tenant blocks the device code
grant. Try 'twig login' (loopback PKCE) instead.`
(`src/Twig/Commands/AuthLoginCommand.cs:56-59`).

### `LoopbackUnavailable` on `auth login` (default PKCE)

Twig could not bind a loopback listener — usually a firewall or an SSH
session with no port forwarding. `auth login` prints `Could not bind a
loopback listener. Try 'twig login --device-code'.`
(`src/Twig/Commands/AuthLoginCommand.cs:60-63`).

### "No PAT found"

Only on `auth.method = pat`. Neither `$TWIG_PAT` nor `auth.pat` in
`.twig/config` had a value
(`src/Twig.Infrastructure/Auth/PatAuthProvider.cs:52-53`). Set one of
the two; there is no third option.

### Refresh-token store write failure after a successful sign-in

`auth login` reports the underlying filesystem error and exits `1`.
Nothing was cached, so the next call retries the whole flow from the
top — usually a permissions problem on `~/.twig/`
(`src/Twig/Commands/AuthLoginCommand.cs:72-76`).

---

## See also

* [`auth login`](../commands/system/auth-login.md) — bootstrap or
  re-bootstrap the refresh-token store interactively.
* [`auth status`](../commands/system/auth-status.md) — decode the cached
  token and diagnose the failure without printing a secret.
* [`auth clear`](../commands/system/auth-clear.md) — wipe both files and
  invalidate the in-memory copy.
* [System commands group](../commands/system/README.md) — every other
  low-level utility that ships with twig.
* [Architecture: ADO integration](../architecture/ado-integration.md) —
  how the `Authorization` header this feature produces flows into the
  REST client, revision handling, and error mapping. Note that the
  authentication section there describes an earlier subprocess-based
  token broker; the current authoritative behavior is documented above.
