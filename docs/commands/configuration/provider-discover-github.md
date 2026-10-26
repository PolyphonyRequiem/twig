---
command: provider discover github
group: configuration
summary: Observe immutable GitHub repository identity without binding.
stability: stable
mutates: none
---

# `twig provider discover github`

## Behavior

Observe the immutable owner and repository node IDs using one fixed read-only
GraphQL lookup. Discovery reports the API origin, current locator, attachment
revision and current binding ID, without creating target rows or replacing the
worktree pointer. Unavailable/private/incomplete responses are failures, not proof
of absence. The observation is not remote permission or authenticated-actor evidence.
The local revision and binding ID are pointer observations, not validated binding
authority: discovery parses the existing pointer and checks its worktree fingerprint
without opening, creating or migrating the central registry. JSON reports
`bindingValidated: false`; bind performs the binding-row validation separately.

## Usage

```text
twig config github.account <explicit-gh-login>
twig provider discover github --host github.com --repository owner/repo
twig provider discover github --host github.com --repository owner/repo -o json
```

`--host` is a required bare lowercase DNS name; `--repository` is a required
`owner/name` lookup locator. Neither is inferred from the Git remote or silently
written into configuration. The explicit user-local `github.account` selects an
already signed-in account via `gh auth token --hostname <host> --user <account>`; no active
account default, renewal, new scopes or token persistence is used.

`-o` / `--output` supports `human` (default), `json`, and `minimal`.
JSON emits one source-generated report. Minimal columns are provider, API origin,
owner ID, repository ID, locator, attachment revision, current binding ID and
result binding ID (empty for discovery), and binding-validated boolean (`false`
for discovery). `--help` describes the options.

## Exit codes

- `0` — the observation report was written.
- `1` — discovery, storage or credential failure: the lookup is unavailable,
  private or incomplete, the request is refused, the explicit `github.account`
  credential is unavailable, or the local pointer cannot be read.
- `2` — invalid usage: a missing or malformed `--host` or `--repository`, or an
  output format other than `human`, `json` or `minimal`.

## Binding the observation

Use all the observed values in [`provider bind github`](provider-bind-github.md).
An absent attachment has revision `0`; a detached attachment retains its tombstone
revision. A current binding ID must be supplied with `--replace-binding` when
changing an attached target. Discovery does not change workspace provider/host/
repository configuration, ADO bindings, or local work-item data.
