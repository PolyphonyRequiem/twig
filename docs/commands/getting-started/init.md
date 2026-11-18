---
command: init
group: getting-started
summary: Initialize one fresh, natively bound Core store at the current git-worktree root.
stability: stable
mutates: local
---

# `twig init`

## Behavior

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
is not evidence of a missing identity or connection binding. Check the Twig version and explicitly identified application-data location.
Disposable developer-local state can be discarded separately before fresh init;
never delete source, credentials, or remote-write evidence as a retry shortcut.
Then run `twig init <org> <project>` at the worktree root. Status does not repair,
delete, reauthenticate or rebind, and init refuses existing state.

## Synopsis

```sh
twig init
twig init <org> <project>
twig init --org <org> --project <project> --binding <registered-binding-id> -o json
twig init --provider github --host github.com --repository owner/repo --account selected
twig init --provider twig --database personal
twig init <org> <project> --core-store <new-absolute-file>
```

## Native authority

Connections are registered through the local daemon by portable provider dimensions,
separately from user-scoped credentials. For an existing `twig.json`, `twig init`
resolves those dimensions to this device's registration and user binding. A different
user on a different device enrolls and binds their own credential without changing
the manifest. Local attachment, binding and observed-principal IDs are never copied
between devices or written into `twig.json`.

For ADO, register a connection, enroll an identity and bind it before initialization:

```sh
twig connection add --provider ado --org contoso --project Fabrikam
twig auth login --identity work
twig connection bind --provider ado --org contoso --project Fabrikam --identity work
twig init contoso Fabrikam
```

PAT enrollment is also supported through `twig auth pat`. Init uses the credential
bound to the matching registered connection, not an endpoint default or last login.
The low-level explicit `--binding` setup override can still pin an existing binding
to a fresh checkout without changing any central selection.
Unknown, empty or cross-endpoint bindings refuse before checkout writes or
metadata HTTP. There is no ambient-token, last-login or Azure CLI fallback.

The existing native binding operation lease, principal attestation and generation
checks remain authoritative. Init reads actual collection/project/principal
metadata, creates the new store and records its native connection pin. It writes
only the worktree attachment/registry metadata required by that binding owner;
it does not materialize profile, primary-scope or claim business state.

For GitHub, register the host/repository with `connection add --provider github`,
then bind your existing `gh` account with the same dimensions and
`connection bind --account <account>`. `init` resolves this local binding; an
explicit `--account` is a fresh-setup override. The bounded credential
source runs `gh auth token --hostname <host> --user <account>` with ambient token
variables removed. The active `gh` account never supplies authority. Init observes
the native actor and immutable owner/repository IDs, attaches that actual target
and pins the new store through the existing native host. It does not enroll
credentials, query issues, discover process rules or send a remote mutation.

For Twig-native work, `--database <name>` explicitly creates or selects an
owner-local named database. Each database has its own durable source UUID, items,
revisions and authority receipts. Multiple fresh repository workspaces can use
the same name; different names isolate reads and writes even when item numbers
coincide. Workspace mirrors, drafts and proposal journals remain separate.

Names contain 1-64 lowercase letters, digits or internal hyphens, not paths.
The name is declared as `nativeDatabase` in `twig.json`; a matching existing
manifest can supply it without `--database` after a matching
`connection add --provider twig --database <name>`. There is no implicit default,
singleton fallback, adoption or migration. Ordinary workspace reopening refuses
a missing or replaced database or a changed selection. `--core-store` selects
the separate client database, not the native authority.

## Arguments and options

| Option | Meaning |
| --- | --- |
| `<org> <project>` or `--org` / `--project` | Explicit ADO coordinates, or omit both to use twig.json unchanged. Conflicting positional/named values refuse. |
| `--provider ado\|github\|twig` | Explicit provider; defaults to ADO, or the existing manifest's declared provider. Never inferred from Git remotes. |
| `--database` | Explicit named authority for `--provider twig`, or the matching manifest's `nativeDatabase`. Required without a declaration; no default. Creates the named database if absent during fresh init only. |
| `--host` / `--repository` | Explicit canonical GitHub host and `owner/name`, or matching existing manifest coordinates. |
| `--account` | Explicit fresh-setup gh account override; normally omitted in favor of the local connection's user binding. |
| `--binding` | Explicit fresh-setup ADO binding override; normally omitted in favor of the local connection's user binding. |
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
`databasePath`, and a message. Twig-native initialization also reports
`nativeDatabase`, distinguishing the selected authority from the client store.
JSON aliases emit one parseable JSON object;
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

See [getting started](../../getting-started.md) for a complete first read and Bench.

## Second-device acceptance check

On a different device, a different contributor should clone the same commit, start
their installed daemon, and leave the repository's `twig.json` untouched. Register
the provider dimensions from that manifest with `connection add`, authenticate as
their own principal, and use `connection bind` with their own identity/account.
`connection list` should show the registration even outside the checkout.

At the fresh checkout root, run `twig connection status`, `twig init`, and then
`twig show <an-item-the-contributor-can-read> --refresh`. Verify the observed
principal is that contributor, and `git diff --exit-code -- twig.json` succeeds.
Missing grants must fail rather than trying the original contributor's account.
This live check needs actual independent devices and provider credentials; the
isolated-home automated regressions are not a substitute for it.
