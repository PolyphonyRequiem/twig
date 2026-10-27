---
command: config
group: configuration
summary: Read or set a configuration value.
stability: stable
mutates: local
---

# `twig config`

Read or write a single key on the split Twig configuration. Repo coordinates
land in `twig.json`; only explicitly chosen workspace preferences are persisted
in `.twig/config`, which is absent when there are none. Unset local values
inherit home-wide preferences where available and otherwise use built-in
defaults. The `--global` option is limited to `display.icons` and stores its
home-wide value in `~/.twig/display.json`, never credentials or tracker state.

Normal CLI authentication uses an explicit connection binding, not a configuration
PAT or auth-method selector. Enroll or renew credentials with [`auth login` or
`auth pat`](../system/auth-login.md); inspect the effective selection with
[`auth status`](../system/auth-status.md). Legacy auth configuration is migration
input, not an override for an attached binding.

## Synopsis

```
twig config <key> [<value>] [--global] [--unset] [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`<key>`|yes|Dot-path configuration key (e.g. `organization`, `git.project`, `display.icons`). The full accepted set is enumerated in `src/Twig/Commands/ConfigCommand.cs:115-147`.|
|`<value>`|no|Value to set. Omit for read mode.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`human` \| `json` \| `minimal`|`human`|Output format.|
|`--global`|flag|`false`|Read or write the home-wide `display.icons` default only.|
|`--unset`|flag|`false`|Clear the selected workspace or global icon preference; omit `<value>`.|

## Behavior

- Normal read mode returns the effective value; a workspace icon override wins over the
  global default, which wins over the built-in `auto` default. An explicit workspace
  `auto` is an override, not an absent setting.
- Normal write mode calls `TwigConfiguration.SetValue` and saves workspace configuration
  through `SaveSplitAsync`. A global write changes only `~/.twig/display.json`, and
  `--unset` removes the selected icon override to inherit the next layer. `--global`
  rejects every key except `display.icons`; neither mode stores global credentials.
- Workspace saves preserve explicit values, even when they equal today's
  built-in default. A local config with no explicit preferences is removed;
  loading it thereafter uses global settings and built-in defaults.
- Workspace display writes refresh the Oh My Posh prompt-state cache. Global changes
  take effect the next time Twig loads configuration, even outside a workspace.
- Empty or whitespace-only keys exit `2` with a usage error
  (`src/Twig/Commands/ConfigCommand.cs:30-34`).
- Machine formats emit records under the tags `configValue` (read) and `configSet` (write)
  keyed by `key`, `value`, and (write only) `message` — see
  `src/Twig/Commands/ConfigCommand.cs:83-107`.

### Work-item provider

`provider` is a repo-scoped setting in `twig.json`. When it is absent, Twig keeps
existing behavior and resolves the provider to `ado` without adding a field to
existing manifests. User input is trimmed and case-insensitive; Twig stores only
the canonical tokens `ado` and `github`. A non-canonical or unknown value already
stored in the manifest is rejected rather than guessed.

GitHub reads require a valid provider-target attachment referencing the durable
registry's immutable owner and repository node IDs at the configured API origin.
`github.host` and `github.repository` supply a host and discovery locator, not
identity authority. Existing slug-only configuration is **unbound** and refused
before credential acquisition or HTTP; it is never silently enrolled or promoted
to a binding, and Twig never falls back to ADO.

`github.credentialsource` is a **user-local** selector: absent or `gh` preserves the
existing explicit GitHub CLI account source; `github-app` selects Twig-owned
GitHub App OAuth on github.com. `github.appclientid` (public registered app ID)
and `github.oauthidentity` (local alias) are also user-local, never selectable
from shared manifests. Selecting the source does not sign in, acquire a
credential, grant permissions or enable writes. Unknown selections refuse
without fallback. Enroll and inspect the app identity with
[`auth login --provider github`](../system/auth-login.md#github-app-oauth-enrollment)
and [`auth status --provider github`](../system/auth-status.md).

`github.account` is a **user-local** preference in `.twig/config`, never a shared
repository coordinate in `twig.json`. It explicitly selects a GitHub CLI account
for that host; it does not change another user's selection. The token
source uses `gh auth token --hostname <host> --user <account>` without changing the
CLI's active account or asking it to refresh stored credentials. Token environment overrides are removed only from the child
process; the caller's environment and stored credentials are unchanged. Twig does
not persist the returned token, request new scopes, or infer an authenticated
actor or granted permissions from this account-selection string. No account is
assumed when the setting is missing.

Repository discovery is a fixed read-only observation, not a binding operation.
Use [`provider discover github`](provider-discover-github.md), then
[`provider bind github`](provider-bind-github.md) with the exact observed IDs and
attachment revision. Neither command changes provider configuration. An existing bound repository rename is accepted when both immutable IDs
match. A transfer to a different owner or replacement repository at the same slug
is refused and requires explicit rediscovery/rebinding. Reads do not silently
refresh binding metadata. Twig-owned OAuth sign-in is also required before 1.0;
this GitHub CLI credential source is the first supported source, not its replacement.

For setup, choose an already signed-in GitHub CLI account explicitly with
`twig config github.account <login>`. Run discovery and bind using explicit
`--host` and `--repository` values, then select normal reads separately:
`twig config github.host github.com`, `twig config github.repository owner/repo`,
and `twig config provider github`. These settings do not enroll an account,
grant remote permissions, initialize a GitHub item store, or migrate existing ADO
seeds/context. Fresh GitHub-only initialization and complete read/write integration
are separate delivery requirements, not consequences of binding a target.

Bound reads capture the attachment revision, binding ID, and immutable target
identity, then recheck them after the complete snapshot and relationship fetch.
Detaching, rebinding, or detaching and reattaching during that fetch refuses the
result before a local item identity is written. An explicit metadata-only locator
update does not change target selection and does not invalidate the read.

```
$ twig config provider
ado
$ twig config provider GitHub
Set provider = GitHub
$ twig config provider
github
```

### Icon display

`display.icons` falls back to `auto` when neither the workspace nor the global
display file specifies a value. Explicit `nerd`, `unicode`, or `auto` workspace values
win over the global preference. Existing saved preferences are preserved.

Automatic mode uses Nerd Font glyphs for terminals with documented bundled
support: [Kitty](https://sw.kovidgoyal.net/kitty/faq/#kitty-is-not-able-to-use-my-favorite-font),
[WezTerm](https://wezterm.org/config/fonts.html), and
[Ghostty](https://ghostty.org/docs/config). Detection uses `TERM` and `TERM_PROGRAM`.
Redirected output, `TERM=dumb`, and unrecognized terminals use Unicode.
Generic xterm, Windows Terminal, Konsole, and an SSH connection alone do not
establish Nerd Font support. Fonts installed on the server are not evidence
of the client's selected font. No font installation, terminal query, input
reading, or configuration rewrite is performed.

Use `twig config display.icons nerd --global` to prefer Nerd Font glyphs across
Twig workspaces on this machine. Use `twig config display.icons --unset` in a
workspace with an explicit value to inherit the global setting, or set an explicit
workspace `auto`/`unicode` override. This is a remembered preference, not font
detection: an SSH or attached terminal without those glyphs needs an override.

## Examples

Read the configured organization:

```
$ twig config organization
contoso
```

Set the default area path and see the confirmation payload as JSON:

```
$ twig config defaults.areapath "Contoso\\Team Alpha" -o json
{"kind":"configSet","key":"defaults.areapath","value":"Contoso\\Team Alpha","message":"Set defaults.areapath = Contoso\\Team Alpha"}
```

Set and inspect a machine-wide icon default without changing any workspace config:

```console
$ twig config display.icons nerd --global
Set display.icons = nerd
$ twig config display.icons --global
nerd
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Successful read or write|`0`|
|Unknown key in read mode|`1` with stderr error|
|Unknown key or invalid value in write mode|`1` with stderr error|
|`--global` or `--unset` with unsupported key, or invalid icon value|`1` with stderr error|
|`--unset` combined with a value|`2` with usage error|
|Missing key argument|`2` with usage error|

## See also

- [`config status-fields`](./config-status-fields.md)
- [`migrate-config`](./migrate-config.md)
- [`help`](./help.md)
