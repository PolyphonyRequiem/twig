---
command: init
group: getting-started
summary: Initialize one fresh, natively bound Core store at the current git-worktree root.
stability: stable
mutates: local
---

# `twig init`

Initialization has one path: a **new Core SQLite store**, pinned to the observed
native connection and actor, with `.twig/core-workspace.json` recording its actual
absolute path and instance. The default database is `.twig/core.db`. It never
opens the old item cache, synchronizes process/field/item data, selects a reference
profile, edits Git configuration or installs companions. It ensures a local
`/.twig/` ignore rule, appending only when needed and retaining existing content.
An already effective final `.twig/` rule leaves `.gitignore` byte-identical.
Custom database files outside `.twig` are **not automatically ignored**: protect
their database and SQLite sidecars separately. Init reports that risk explicitly.

Run at a fresh Git worktree root. Any existing `.twig` directory (even empty,
partial or legacy), selected database file or SQLite sidecar is refused. There
is no migration, adoption, reset, archival or old-state adapter. If old state
is no longer needed, preserving or discarding it is a separate manual decision.
Init never deletes it or clears uncertain remote-write evidence.

If `twig connection status` reports `layout-marker-missing`, the error names the
local `.twig` directory whose attachment layout is missing or unsupported. This
is not evidence of a missing identity or connection binding. Inspect and back up
the directory and check the Twig version first. To initialize a fresh workspace,
preserving or moving aside existing local state is a separate manual decision;
then run `twig init <org> <project>` at the worktree root. Status does not repair,
delete, reauthenticate or rebind, and init refuses existing state.

## Synopsis

```sh
twig init <org> <project>
twig init --org <org> --project <project> --binding <registered-binding-id> -o json
twig init --provider github --host github.com --repository owner/repo --account selected
twig init <org> <project> --core-store <new-absolute-file>
```

## Native authority

For ADO, enroll an identity and register a binding before initialization:

```sh
twig auth login --identity work
twig connection bind --org contoso --project Fabrikam --identity work --default
twig init contoso Fabrikam
```

PAT enrollment is also supported through `twig auth pat`. Init uses the exact
registered endpoint default, or `--binding` to pin an explicitly selected
registered binding to this fresh checkout without changing the central default.
Unknown, empty or cross-endpoint bindings refuse before checkout writes or
metadata HTTP. There is no ambient-token, last-login or Azure CLI fallback.

The existing native binding operation lease, principal attestation and generation
checks remain authoritative. Init reads actual collection/project/principal
metadata, creates the new store and records its native connection pin. It writes
only the worktree attachment/registry metadata required by that binding owner;
it does not materialize profile, primary-scope or claim business state.

For GitHub, `--account` names an existing `gh` account. The bounded credential
source runs `gh auth token --hostname <host> --user <account>` with ambient token
variables removed. The active `gh` account never supplies authority. Init observes
the native actor and immutable owner/repository IDs, attaches that actual target
and pins the new store through the existing native host. It does not enroll
credentials, query issues, discover process rules or send a remote mutation.

## Arguments and options

| Option | Meaning |
| --- | --- |
| `<org> <project>` or `--org` / `--project` | Explicit ADO coordinates. Conflicting positional/named values refuse. Not GitHub coordinates. |
| `--provider ado\|github` | Explicit provider; defaults to ADO, or the existing manifest's declared provider. Never inferred from Git remotes. |
| `--host` / `--repository` | Explicit canonical GitHub host and `owner/name`, or matching existing manifest coordinates. |
| `--account` | Required explicit selected `gh` account for GitHub initialization. |
| `--binding` | Exact registered ADO binding to pin in this new checkout; otherwise use the endpoint default. |
| `--core-store` | Optional **new absolute file** override. Its parent directory must already exist. Default `.twig/core.db` needs no pre-created `.twig` directory. |
| `--team` / `--git-project` | Optional ADO plain configuration, without discovery or cache prefill. Existing manifest values must match. |
| `-o`, `--output` | `human`, `json`, `json-full`, `json-compact`, `minimal`. `ids` is refused because initialization has no work-item IDs. |
| `--force`, `--reinitialize`, `--sprint`, `--area`, `--profile` | **Retired**; explicitly refused as usage errors before writes. No silently ignored options. |

An existing `twig.json` is a declaration, not an old database: matching native
coordinates may be used, but its bytes are not rewritten. Existing policy bytes
are also preserved; `ConfiguredPolicyProjectionSource` continues enforcing
declared mechanical policy during application. Init does not resurrect reference
profile compatibility or materialize policy records.

## Results and interruption

All provider, coordinate, account, path and existing-state preflight precedes
workspace writes or work HTTP. Native credential/metadata failure may leave
partial initialization evidence. Created files are deliberately retained,
and another init refuses that directory rather than silently reusing it.

Success emits exactly one result, including `coreInitialized: true`, the actual
`databasePath`, and a message. JSON aliases emit one parseable JSON object;
human/minimal output is a static rendering of the same truthful result. Errors
are format-aware on stderr. No old progress stdout is captured and rewritten.

| Exit | Meaning |
| --- | --- |
| `0` | New store created, native connection pinned, marker recorded. |
| `1` | Existing/partial state or native identity/storage refusal; evidence preserved. |
| `2` | Invalid coordinates/provider/account/path, unsupported `ids`, conflicting or retired options. |

Optional telemetry uses static command metadata and duration only, never native
identities, accounts, coordinates, paths or work content. Telemetry failure cannot
change initialization's result.

This closes fresh initialization only. The rest of the no-marker command catalog,
UI cutover and old Domain/source removal are separate work.
