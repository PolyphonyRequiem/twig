# system commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`auth status`](auth-status.md) | Inspect local authentication state without refreshing. Use --provider github for a selected GitHub App identity. |
| [`auth login`](auth-login.md) | Enroll an explicit identity. ADO uses loopback PKCE by default; --provider github uses Twig's GitHub App device flow without a shipped secret. |
| [`auth identities`](auth-identities.md) | List every registered identity (AAD and PAT) with safe principal metadata. Credential blobs are never surfaced; AAD rows show tenant/objectId/authorityHost, PAT rows show adoPrincipalId/adoAuthority. |
| [`auth pat`](auth-pat.md) | Enroll or renew a Personal Access Token identity. The PAT is read from a non-echoing Spectre prompt on a TTY, or from stdin when --stdin is set or stdin is redirected; it is never accepted as an argument. The service performs authoritative read-only principal attestation before touching any stored credential. |
| [`connection bind`](connection-bind.md) | Bind an endpoint (org/project) to a registered identity. Use --default to request this as the initial default; the service refuses to switch an existing different default. |
| [`connection list`](connection-list.md) | List bindings for an endpoint (org/project). Falls back to the workspace's checked-in coordinates when both --org and --project are omitted. |
| [`connection status`](connection-status.md) | Show the identity + binding the current attached worktree resolves to. Fails with setup guidance when there is no attachment or the binding is ambiguous; no fallback. |
| [`connection writes`](connection-writes.md) | Inspect native remote-write intents, immutable acknowledgments and uncertainty blockers for the attached checkout. Read-only; never replay, expire or discard an unknown write. |
| [`version`](version.md) | Show the current version. |
