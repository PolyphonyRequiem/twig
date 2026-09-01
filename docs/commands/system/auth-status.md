---
command: auth status
group: system
summary: Inspect the refresh-token store and cached ADO access token without ever printing the token.
stability: stable
mutates: none
---

# `twig auth status`

Reports on twig's two credential files: the bootstrap refresh-token store
(`~/.twig/.refresh-token`) and the short-lived access-token cache
(`~/.twig/.token-cache`). For JWT access tokens it decodes and prints the
audience, expiry, tenant, and principal — never the token itself — so you can
diagnose 403s caused by a wrong-audience token or an expired cache. Reach for
it whenever an ADO call fails and you cannot tell whether the problem is
identity, tenant, audience, or expiry.

## Synopsis

```
twig auth status [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o, --output <format>` | string | `human` | Output format: `human`, `json`, or `minimal`. Applies to the error summary lines; the descriptive block from the JWT inspector is always rendered as human-readable text. |

## Behavior

The command reads two files without mutating anything:

1. `~/.twig/.refresh-token` via `TwigRefreshTokenStore.TryRead()`. If it is
   missing, the command prints "(not bootstrapped — first ADO call will read
   `~/.azure/msal_token_cache.json`)". If present, it prints the source,
   bootstrap timestamp, tenant ID, object ID, client ID, and authority host —
   in that order — but never the refresh token itself
   (`src/Twig/Commands/AuthStatusCommand.cs:20-35`).
2. `~/.twig/.token-cache` via `TwigTokenFileCache.TryRead()`. If missing, the
   command reports the cache path and suggests running a command that hits
   ADO (for example, `twig refresh`) to populate it, then exits `0`
   (`src/Twig/Commands/AuthStatusCommand.cs:38-43`).

When a cached token is present, `JwtAccessTokenInspector.TryDecode` attempts
to parse it as a JWT. If it cannot (typical of a PAT or opaque credential),
the command prints "token is not a JWT (likely a PAT or opaque credential)."
and returns `0` — twig cannot verify audience for opaque credentials, so ADO
itself is the arbiter (`src/Twig/Commands/AuthStatusCommand.cs:50-57`).

For a decoded JWT the command prints the diagnostic block from
`JwtAccessTokenInspector.DescribeForDiagnostics` and then checks two
invariants:

* **Audience.** If `IsValidAdoAudience` is false, the command prints "This
  token's audience is NOT the Azure DevOps API." to stderr, suggests
  `twig auth clear` followed by an explicit `az login --scope
  499b84ac-1321-427f-aa17-267ca6975798/.default`, and exits `1`
  (`src/Twig/Commands/AuthStatusCommand.cs:62-68`).
* **Expiry.** If the token is expired or within five minutes of expiring,
  the command prints "This token is expired or expiring within 5 minutes."
  to stderr, suggests `twig auth clear`, and exits `1`
  (`src/Twig/Commands/AuthStatusCommand.cs:70-76`).

Neither the raw refresh token nor the raw access token is ever written to
stdout or stderr. Do not paste the contents of `~/.twig/.refresh-token` or
`~/.twig/.token-cache` into bug reports; the fields this command prints are
the safe surface.

## Examples

Healthy status on a bootstrapped workstation:

```
$ twig auth status
refresh-store: C:\Users\alice\.twig\.refresh-token
  source:       AzureCli
  bootstrapped: 2026-08-30T14:22:11Z
  tenant:       11111111-1111-1111-1111-111111111111
  oid:          22222222-2222-2222-2222-222222222222
  client_id:    04b07795-8ddb-461a-bbee-02f9e1bf7b46
  authority:    https://login.microsoftonline.com

cache:   C:\Users\alice\.twig\.token-cache
stored:  2026-09-01 10:14:02Z (file-cache expiry)

audience: 499b84ac-1321-427f-aa17-267ca6975798  (Azure DevOps)
expires:  2026-09-01T10:14:02Z (in 43 minutes)
principal: alice@example.com
```

Wrong-audience token diagnosed on stderr with exit `1`:

```
$ twig auth status
refresh-store: C:\Users\alice\.twig\.refresh-token
  ...

cache:   C:\Users\alice\.twig\.token-cache
stored:  2026-09-01 10:14:02Z (file-cache expiry)

audience: https://graph.microsoft.com  (NOT Azure DevOps)
...

error: This token's audience is NOT the Azure DevOps API.
error: Run 'twig auth clear' then 'az login --scope 499b84ac-1321-427f-aa17-267ca6975798/.default' to refresh.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
| Store missing, or store present and cached token missing, or opaque/PAT credential | `0` |
| Cached JWT is present, valid audience, and not expiring within 5 minutes | `0` |
| Cached JWT has a non-ADO audience | `1` with an audience diagnostic on stderr |
| Cached JWT is expired or expiring within 5 minutes | `1` with an expiry diagnostic on stderr |

## See also

* [`auth clear`](auth-clear.md) — wipe the token this command diagnosed.
* [`auth login`](auth-login.md) — bootstrap or re-bootstrap the refresh-token store.
* [System commands group](README.md)
