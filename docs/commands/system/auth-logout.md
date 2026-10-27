---
command: auth logout
group: system
summary: Remove one explicitly selected local GitHub App token pair without claiming remote revocation.
stability: stable
mutates: local
---

# `twig auth logout`

```text
twig auth logout --provider github --identity personal
twig auth logout --provider github --identity personal --client-id <registered-app-client-id> -o json
```

`--provider github` is required. `--identity` and `--client-id` default to
user-local `github.oauthidentity` and `github.appclientid`; both selections must
be present. Output supports `human`, `json` and `minimal`.

Logout serializes against login/refresh, removes only this host/app/alias's
local access/refresh-token pair and clears this runtime's attestation cache.
An already signed-out selection succeeds. Other aliases, ADO credentials,
repository targets and GitHub App installations are untouched. It does not
contact GitHub or replay a pending refresh.

**Remote authorization is not revoked.** Revoke it yourself in GitHub **Settings >
Applications > Authorized GitHub Apps**, and uninstall separately if the
installation should be removed. Logout does not silently request broader
permissions to implement remote revocation.

Exit `0` means local removal completed, `1` reports a storage/runtime failure,
and `2` reports missing or incompatible selection. See
[enrollment, storage and refresh](auth-login.md#github-app-oauth-enrollment) and
[local-only status](auth-status.md).
