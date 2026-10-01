---
command: auth status
group: system
summary: Inspect the effective attached identity binding and configuration provenance without secrets.
stability: stable
mutates: none
---

# `twig auth status`

Reports the effective binding through the same resolver as `twig connection
status`. It does not inspect an ambient global token as if that token were the
selected account. This is integration-branch behavior, not a partial release.

## Synopsis

```text
twig auth status [-o <format>]
twig connection status [-o <format>]
twig auth identities [-o <format>]
twig connection list [--org <organization> --project <project>] [-o <format>]
```

## Behavior

Status validates the worktree attachment, declared endpoint and central binding
default. It reports organization/project/team, method, alias, stable principal
metadata, binding and selection revisions, attachment revision, and configuration
source paths. `display.icons` reports its winning local/global/default source;
local preference source is not claimed as the origin of inherited global icons.
Human and structured output use the same admitted snapshot. Status performs no
work-item request and does not claim fresh remote authorization or locally
verified JWT signatures.

AAD metadata includes tenant/object/issuer requirements. PAT metadata includes
`adoPrincipalId` and `adoAuthority`; PAT output omits AAD-only claims. Names are
management aliases and display labels, never authoritative principal evidence.

Identity listing is explicit bootstrap administration and works before attachment.
Connection listing inspects bindings for paired explicit `--org`/`--project`
coordinates, or the current configuration's endpoint. Neither command selects
an account or changes a default. Registry listings contain principal metadata and
opaque references, never refresh/access tokens or PATs.

Missing, unknown or cross-endpoint selection refuses rather than using a machine
cache, Azure CLI account, global token store or last login. An initial binding can
be created with `twig connection bind --identity <alias> --default`; explicit
coordinates use paired `--org` and `--project`. A different existing default
refuses; safe identity switching is a separate guarded management transition.

## Exit codes and failure modes

Successful inspection/listing returns `0`. Status returns `1` with setup guidance
when attachment or effective selection cannot be resolved. Passing only one of
`--org`/`--project` to connection listing refuses. Missing identity or an attempt
to replace a different initial default refuses without account fallback.

## See also

- [`auth clear`](auth-clear.md) — invalidate only the selected access cache.
- [`auth login`](auth-login.md) — explicitly enroll or renew an identity.
