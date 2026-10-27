---
command: provider bind github
group: configuration
summary: Select a freshly confirmed GitHub repository identity locally.
stability: stable
mutates: local
---

# `twig provider bind github`

## Behavior

Bind an expected [discovery observation](provider-discover-github.md) to this
worktree. Twig repeats the fixed lookup and compares both opaque IDs exactly,
then commits a durable target row before CAS-replacing the local pointer.
This selects local repository identity; it grants no remote permissions.

## Usage

```text
twig provider bind github --host github.com --repository owner/repo --expected-owner-id O_id --expected-repository-id R_id --expected-attachment-revision 0
twig provider bind github --host github.com --repository new/repo --expected-owner-id O_new --expected-repository-id R_new --expected-attachment-revision 4 --replace-binding <current-binding-id> -o json
```

Required options are `--host`, `--repository`, `--expected-owner-id`,
`--expected-repository-id` and `--expected-attachment-revision`. IDs are opaque,
case-sensitive values from discovery. The revision is mandatory even for the same
identity: stale values and A-to-B-to-A selection changes cannot bypass CAS.
An absent attachment requires `0`; detached pointers require their current
tombstone revision. There is no force option.

`--replace-binding` must equal the current attached binding ID when identity
changes, including owner transfers, reused slugs or a different API host with the
same opaque IDs. Old rows and existing item data are retained. Same-identity
rebinding reuses the durable row without silently refreshing locator/display
metadata; renames do not redefine identity.

The user-local `github.account` must explicitly select an existing GitHub CLI
account for the default `gh` source, or the exact credential UserName for
`github.credentialsource=windows-credential-manager` (see [config](config.md)).
The latter reads only `twig:github:<host>/<owner/repository>` through CredReadW
on Windows and never falls back to `gh`. Tokens stay in memory; no refresh, storage, new scopes, active-account
switch or remote write occurs. A token may have broader grants than Twig's fixed
request admission permits.

`-o` / `--output` accepts `human` (default), `json` or `minimal`, with the same
columns as discovery plus the resulting binding ID and attachment revision.
Its `bindingValidated` value is `true` after the normal binding-store checks and
successful local commit; discovery's pointer-only observation is never treated as
binding authority.

## Exit codes

- `0` — the local selection was committed.
- `1` — identity, CAS, storage or credential failure: the fresh lookup does not
  match the expected IDs, the attachment revision or `--replace-binding` is
  stale, the lookup or credential is unavailable, or the local commit fails.
  A late pointer-write failure may leave a recoverable unreferenced row;
  rediscover before retrying.
- `2` — invalid usage: a missing `--host`, `--repository`, expected ID or
  attachment revision, a negative revision, a malformed `--replace-binding`, or
  an output format other than `human`, `json` or `minimal`.

## Scope

This command changes neither `provider`, `github.host`, `github.repository`, nor
ADO bindings. Choose normal-read configuration explicitly with `twig config`
after binding. It does not initialize/migrate item stores, seeds or context.
Twig-owned OAuth sign-in and complete GitHub integration remain required before
1.0; this command alone does not complete those requirements.

The internal captured-read-session API can explicitly observe the authenticated
actor and read issue snapshots using one captured in-memory credential. It retains
the selected API endpoint, repository identity, attachment fingerprint/revision
and binding revision, and rechecks selection around reads. This is not invoked by
binding and does not establish storage-generation admission, durable proposal
handoff, atomic provider concurrency or future write authority. Account/login
names remain locators, not stable actor identity.

Ordinary bound reads allow a confirmed same-identity locator/display metadata
refresh without changing repository selection. Captured sessions are stricter:
their captured binding revision must still match before further HTTP reads.
Repository identity and attachment changes remain refusal conditions for both.
