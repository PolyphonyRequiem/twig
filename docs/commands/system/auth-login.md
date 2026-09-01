---
command: auth login
group: system
summary: Sign in to Azure DevOps interactively and persist a refresh token under ~/.twig/.
stability: stable
mutates: local
---

# `twig auth login`

Runs an interactive Azure Active Directory sign-in against the multi-tenant
Azure DevOps API and writes the resulting refresh token to
`~/.twig/.refresh-token`. Once this succeeds, subsequent twig commands mint
access tokens from that refresh token directly and no longer need to read
`~/.azure/msal_token_cache.json` or shell out to `az`. Reach for it when you
are bootstrapping a fresh machine, switching identities, or recovering from a
"wrong audience" failure that `auth clear` alone cannot resolve.

## Synopsis

```
twig auth login [--device-code] [--tenant <id>] [--no-browser] [-o <format>]
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
| `--device-code` | bool | `false` | Use the OAuth 2.0 device authorization grant instead of the default loopback PKCE flow. Required on headless boxes without a browser but frequently blocked by tenant Conditional Access policy. |
| `--tenant <id>` | string | `organizations` | AAD tenant ID, domain, or the literal `organizations`. Set this when your account is a guest in multiple directories and you need the sign-in bound to a specific one. |
| `--no-browser` | bool | `false` | Print the authorize URL to stdout instead of launching the system browser. Use over SSH or when the OS launcher is unreliable. Ignored when `--device-code` is set. |
| `-o, --output <format>` | string | `human` | Output format for the "signed in" summary: `human`, `json`, or `minimal`. |

## Behavior

The command dispatches on `--device-code`: without it, it starts a loopback
HTTP listener and runs PKCE against Azure CLI's well-known public client
(`04b07795-8ddb-461a-bbee-02f9e1bf7b46`), optionally launching the browser
(`src/Twig/Commands/AuthLoginCommand.cs:43-50`, `src/Twig/Commands/AuthLoginCommand.cs:21`).
With it, the command runs the device code grant, printing a code and a
verification URL for you to enter in another browser
(`src/Twig/Commands/AuthLoginCommand.cs:139-154`).

On success the refresh-token entry — tenant ID, object ID, client ID,
authority host, source, and the refresh token itself — is written atomically
via a `.tmp` file and rename to `~/.twig/.refresh-token`
(`src/Twig/Commands/AuthLoginCommand.cs:67-76`, `src/Twig.Infrastructure/Auth/TwigRefreshTokenStore.cs:14-16,57-61`).
The old in-process access-token cache at `~/.twig/.token-cache` is then
deleted so the next ADO call mints a fresh token against the new identity
instead of reusing a wrong-audience one from a previous sign-in
(`src/Twig/Commands/AuthLoginCommand.cs:78-81`).

Failure paths are distinguished so twig can suggest the other flow:
* `InteractiveAuthErrorKind.PolicyBlocked` combined with `--device-code`
  emits "Your tenant blocks the device code grant. Try 'twig auth login'
  (loopback PKCE) instead." (`src/Twig/Commands/AuthLoginCommand.cs:56-59`).
* `InteractiveAuthErrorKind.LoopbackUnavailable` emits "Could not bind a
  loopback listener. Try 'twig auth login --device-code'."
  (`src/Twig/Commands/AuthLoginCommand.cs:60-63`).

The command never echoes the refresh token or access token to the terminal.
The success record contains only user principal name, tenant ID, source, and
the store path.

## Examples

Interactive sign-in on a workstation with a browser:

```
$ twig auth login
Signed in as alice@example.com (tenant 11111111-1111-1111-1111-111111111111).
Refresh token stored at C:\Users\alice\.twig\.refresh-token
```

Headless server using the device code grant against a specific tenant:

```
$ twig auth login --device-code --tenant contoso.onmicrosoft.com
To sign in, use a web browser to open https://microsoft.com/devicelogin
and enter the code A1B2-C3D4 to authenticate.
Signed in as alice@contoso.onmicrosoft.com (tenant 22222222-2222-2222-2222-222222222222).
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
| Sign-in completed and refresh token stored | `0` |
| Interactive auth failed (user cancelled, policy blocked, loopback unavailable) | `1` with a `Sign-in failed:` error and a follow-up hint |
| Sign-in succeeded but writing `~/.twig/.refresh-token` failed | `1` with the underlying file-system error |
| Cancellation via Ctrl-C during the wait for the browser or device code | `1` |

## See also

* [`auth status`](auth-status.md) — verify the token the login just produced.
* [`auth clear`](auth-clear.md) — wipe the store before re-running login.
* [Auth commands group](README.md)
