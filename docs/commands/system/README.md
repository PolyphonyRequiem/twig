# System commands

The `system` group covers commands that manage twig's own installation, its
Azure DevOps sign-in state, and the release channel it upgrades from. None of
these commands touch a workspace's SQLite cache or the ADO board directly;
they operate on the per-user credential stores under `~/.twig/`, the twig
binary itself, and GitHub Releases.

| Command | Summary |
|---------|---------|
| [`twig auth login`](auth-login.md) | Sign in to Azure DevOps interactively (loopback PKCE, or device code) and write a refresh token to `~/.twig/.refresh-token`. |
| [`twig auth status`](auth-status.md) | Inspect the refresh-token store and cached access token: audience, expiry, tenant, and principal. Never prints the token. |
| [`twig auth clear`](auth-clear.md) | Wipe both the cached access token (`~/.twig/.token-cache`) and the refresh-token store (`~/.twig/.refresh-token`), and invalidate the in-process copy. |
| [`twig version`](version.md) | Print the installed twig version. |
| [`twig upgrade`](upgrade.md) | Check GitHub Releases for a newer twig and apply the update, including companion binaries (`twig-mcp`, `twig-tui`). |
| [`twig changelog`](changelog.md) | Display recent release notes from GitHub Releases. |

## Group conventions

* All three `auth` verbs (`login`, `status`, `clear`) support `-o, --output`
  with values `human`, `json`, `minimal`. `auth status` never prints the raw
  token in any format; it renders decoded JWT diagnostics only.
* `upgrade` and `changelog` both hit GitHub's public REST API
  (`api.github.com/repos/<repo>/releases`); no ADO calls are made.
* `version` is intentionally minimal: it does not take an `--output` flag and
  prints exactly one line even though the group-level help implies otherwise.
* None of these commands read or write the workspace SQLite database, so they
  work outside a twig-initialized directory.
