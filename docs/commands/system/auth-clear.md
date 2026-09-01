---
command: auth clear
group: system
summary: Wipe the refresh-token store and cached access token, and flush the in-process copy.
stability: stable
mutates: local
---

# `twig auth clear`

Deletes both `~/.twig/.token-cache` (the short-lived access-token file cache)
and `~/.twig/.refresh-token` (the bootstrap refresh-token store), and calls
`IAuthenticationProvider.InvalidateToken()` so the running process cannot
keep using an in-memory copy. Reach for it after switching identities with
`az login`, after twig starts returning 403s that `auth status` blames on
audience or expiry, or when you need to force a fresh bootstrap on the next
ADO call.

## Synopsis

```
twig auth clear [-o <format>]
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
| `-o, --output <format>` | string | `human` | Output format for the outcome records: `human`, `json`, or `minimal`. |

## Behavior

The command runs three side effects, in order
(`src/Twig/Commands/AuthClearCommand.cs:27-41`):

1. Record whether `~/.twig/.token-cache` currently exists.
2. Record whether `~/.twig/.refresh-token` currently exists.
3. Delete both files via `TwigTokenFileCache.TryDelete()` and
   `TwigRefreshTokenStore.TryDelete()`. Both calls are idempotent — a missing
   file is not an error.
4. Call `IAuthenticationProvider.InvalidateToken()` on the current provider.
   This matters when `twig` is being driven by a long-lived host (for example
   `twig-mcp`); without it, the host would keep serving requests with the
   already-loaded token until restart.

For each of the two files the command emits an outcome node in the render
tree: `tokenCacheCleared` / `tokenCacheAbsent` for the access-token cache,
and `refreshStoreCleared` / `refreshStoreAbsent` for the refresh-token store
(`src/Twig/Commands/AuthClearCommand.cs:14-18`). A hint follows: "Next ADO
call will re-bootstrap from the MSAL cache (run 'az login' first if
needed)."

`auth clear` never surfaces the token it is deleting. The paths are printed
so you can confirm the machine is clean; the tokens are not.

## Examples

Clear a fully populated cache and refresh store:

```
$ twig auth clear
Cleared cached token at C:\Users\alice\.twig\.token-cache.
Cleared refresh-token store at C:\Users\alice\.twig\.refresh-token.
Next ADO call will re-bootstrap from the MSAL cache (run 'az login' first if needed).
```

Idempotent on a workstation that was never signed in:

```
$ twig auth clear
No cached token to clear (no file at C:\Users\alice\.twig\.token-cache).
No refresh-token store to clear (no file at C:\Users\alice\.twig\.refresh-token).
Next ADO call will re-bootstrap from the MSAL cache (run 'az login' first if needed).
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
| Either or both files existed and were deleted | `0` |
| Neither file existed | `0` (idempotent) |

`auth clear` has no failure mode that changes the exit code — deletion is
best-effort under `TryDelete`. If a file is locked by another process, the
outcome record still reports the pre-existing state and the file will be
removed by the next successful attempt.

## See also

* [`auth status`](auth-status.md) — diagnose the token before deciding to clear it.
* [`auth login`](auth-login.md) — re-bootstrap after a full clear.
* [System commands group](README.md)
