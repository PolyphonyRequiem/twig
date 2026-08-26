# Worktree attachment storage — settled design (T1, AB#736)

> Status: settled sub-spec closing AB#736. Downstream tickets AB#738 and AB#739 implement against the contract here without opening a new storage or initialization decision.

## 1. Scope and non-goals

This document resolves the storage and initialization contract for managed
Twig worktrees. It fixes:

- how a managed worktree root is detected;
- which state lives checked-in, which lives worktree-local, and which lives
  system-local;
- exact paths, filenames, schemas, and file formats;
- initialization ordering, atomicity, and the legacy-layout forced-reinit
  path;
- how a linked Git worktree is identified apart from the primary worktree;
- the failure modes managed initialization and every downstream storage read
  must recognize;
- the concrete storage interface implementors of AB#738 (primary scope
  attachment) and AB#739 (local claim mint / reclaim) consume.

Explicit non-goals: the claim record's field schema and lifecycle
(owned by AB#737); the CLI verb names for attach/switch/mint (owned by the
Twig CLI/MCP spec that AB#738 implements against); Change Proposal or
recipe format (owned separately); credential storage (owned by the OS
credential provider); non-Git directory execution (deferred).

The design is process-agnostic: no work-item type, state name, field
reference, or person is hard-coded here. Runtime process metadata reaches
storage only as opaque values inside the records this document defines.

## 2. Ticket contradiction, reconciled

AB#736 flags an apparent contradiction between the settled decisions
"managed initialization is Git-root based" and "storage has a worktree-local
domain, a checked-in domain, and a system-local domain". The reconciliation
is scope, not policy:

- **Git-root-based initialization** applies to the *anchor* Twig recognizes
  when it decides whether a directory can host managed state. The anchor is
  a Git worktree root — either the primary repository's top-level directory
  or the top-level directory of a linked worktree created through
  `git worktree add`. Both are surfaced by `git rev-parse --show-toplevel`
  and both live under one Git common-dir. This decision is already recorded
  ("Managed workspace root", "Worktree-local layout") and remains
  unchanged.
- **Storage domains** describe *where each kind of state persists once
  initialization has anchored somewhere valid*. The checked-in domain
  travels with the Git tree (visible from every worktree that checks out the
  tree). The worktree-local domain is per-checkout, gitignored, and never
  shared across linked worktrees. The system-local domain is per-user,
  per-machine, and never enters any Git tree.

These are orthogonal decisions. The Git-root rule says *where a managed
worktree may be born*; the storage-domain split says *where each fact
persists once one exists*. Nothing in this design reopens the Git-root
rule, the checked-in `twig.json` decision, the worktree-local `.twig/`
layout, the `~/.twig/system.db` decision, or the no-migration cutover.

## 3. Roots, anchors, and identity

### 3.1 Detected roots

Managed operations resolve **exactly** these two roots at process start;
either both succeed for a given command or that command STOPs with a named
failure.

| Handle | Definition | Detection |
|---|---|---|
| `worktreeRoot` | Absolute realpath of the top-level directory of the current Git worktree (primary or linked). | `git rev-parse --show-toplevel`, realpath-canonicalized. Command execution directory may live anywhere inside it. |
| `gitCommonDir` | Absolute realpath of the shared Git object/ref directory that every linked worktree of the same repository shares. | `git rev-parse --git-common-dir`, realpath-canonicalized. |
| `worktreeGitDir` | Absolute realpath of the per-worktree Git directory (either `<worktreeRoot>/.git` for the primary worktree or `<gitCommonDir>/worktrees/<name>` for a linked worktree). | `git rev-parse --git-dir`, realpath-canonicalized. |

Managed initialization refuses when:

- `git rev-parse` is not available (not on `PATH`, not a Git checkout, or
  the invocation directory is outside any repository) — `error:
  not-a-git-worktree`;
- the invocation directory is inside a bare repository (`git rev-parse
  --is-bare-repository = true`) — `error: bare-repository-not-supported`;
- the invocation directory is not the worktree root and the command is a
  managed-init verb (`realpath(cwd) != worktreeRoot`) — `error:
  not-at-worktree-root`. Ordinary (already-initialized) commands accept any
  descendant of `worktreeRoot`.

`worktreeRoot`, `gitCommonDir`, and `worktreeGitDir` are the only Git
identity Twig ever writes to disk. Symbolic links are resolved once at
detection time and the resolved values are compared for equality
downstream; the raw pre-realpath strings are never persisted.

### 3.2 Linked-worktree identity

A **linked** worktree is any Git worktree whose `worktreeGitDir` is not
`<worktreeRoot>/.git`. Equivalently, `git rev-parse
--git-common-dir` differs from `git rev-parse --git-dir` (post-realpath).
Twig captures the following stable per-worktree tuple as the
`worktreeFingerprint`:

```
worktreeFingerprint = {
  gitCommonDir:   <abs realpath of git common dir>,
  worktreeGitDir: <abs realpath of per-worktree git dir>,
  worktreeRoot:   <abs realpath of worktree root>
}
```

Two managed worktrees are the "same worktree" only when all three fields
compare byte-equal. The tuple is used to:

- distinguish a linked worktree of the same repository from the primary
  worktree at attach time (they share `gitCommonDir` but differ on
  `worktreeGitDir` and `worktreeRoot`);
- detect a directory move: if `.twig/attachment.json` records
  `worktreeRoot = /A` but `git rev-parse --show-toplevel` now returns
  `/B`, the anchor has moved. AB#738 treats this as `error:
  attachment-fingerprint-drift` and refuses to attach or claim until the
  human forces reinitialization (§7);
- key the system-local worktree index (§5).

Twig never uses the checkout-relative branch name, the current HEAD, or
the linked-worktree `name` (the directory basename under
`<gitCommonDir>/worktrees/`) as identity. Branch is user-mutable; HEAD
detaches; the linked-worktree name can be renamed by
`git worktree move`.

## 4. Storage tiers

Three tiers, one purpose each, no overlap:

| Tier | Anchor | Sharing | Git-tracked | Purpose |
|---|---|---|---|---|
| Checked-in | `<worktreeRoot>` | Every checkout of the same Git tree | Yes (committed) | Repository-scoped identity: connection binding, selected profile identity + pinned version, defaults, portable policy. |
| Worktree-local | `<worktreeRoot>/.twig/` | This one worktree only | No (gitignored) | Per-checkout state: current primary scope attachment, local cache, plan/journal artifacts, active claim reference, worktree fingerprint. |
| System-local | `~/.twig/` (or the platform's user-config home; §4.3) | This user, this machine | No (never inside any Git tree) | Cross-worktree registry: connection registry, cached process/profile metadata, local claim recovery index, installed integration metadata. |

No tier ever contains a value another tier owns. Where a downstream
component needs a value from another tier (e.g. AB#739 needs the connection
binding when writing a claim record), it resolves through the interfaces in
§9 rather than reaching into another tier's files.

### 4.1 Checked-in tier — `<worktreeRoot>/twig.json`

One file, at the worktree root, committed. Its filename, location, and
purpose are already fixed by the current `TwigPaths` contract
(`RepoConfigPath`) and this design preserves them. The record shape used
by AB#738/#739 storage bindings is:

```json
{
  "$schema": "twig.json/v1",
  "version": 1,
  "connection": {
    "organization": "<opaque string>",
    "project":      "<opaque string>",
    "team":         "<opaque string | null>"
  },
  "profile": {
    "identity": "<opaque profile identity string>",
    "version":  "<opaque profile version string>"
  },
  "defaults": { },
  "policy":   { }
}
```

Every downstream tier keys off `connection.organization` and
`connection.project`. Both are treated as opaque strings by every
storage-domain consumer — sanitization for filesystem use is confined to
§5.

`twig.json` is the single source of truth for the connection binding at
run time. `.twig/attachment.json` and the system store never
independently store `organization` or `project`; they carry a
`connectionRef` computed from these values (§5.1). This preserves
"repository checked-in" ownership of connection identity and eliminates
the older `.twig/<org>/<project>/` layout's duplication.

### 4.2 Worktree-local tier — `<worktreeRoot>/.twig/`

Fixed layout for every managed worktree:

```
<worktreeRoot>/.twig/
├── layout.json          # version + integrity marker for this .twig/ tree
├── attachment.json      # primary scope attachment + claim reference
├── worktree.json        # captured worktreeFingerprint at init time
├── cache/
│   └── twig.db          # SQLite cache (WAL); this worktree's only DB
├── journals/            # Change Proposal + recovery journals
├── ado-plans/           # existing native plan directory (unchanged)
└── tmp/                 # atomic-write staging (never committed)
```

Everything under `.twig/` is gitignored. Managed init MUST ensure
`<worktreeRoot>/.gitignore` contains an entry that excludes `/.twig/`
(the leading slash roots the pattern at the worktree root). If a
`.gitignore` already excludes it via an existing pattern, no change is
made; if not, managed init appends a single line and commits nothing
(the human owns the commit).

The legacy `.twig/<org>/<project>/twig.db` layout is **not** used. The new
layout is per-worktree, not per-connection, because a managed worktree has
exactly one connection binding at a time (via `twig.json`). Attempting to
read the legacy layout at startup triggers §7.

#### 4.2.1 `layout.json`

```json
{
  "$schema": "twig-layout/v1",
  "version": 1,
  "initializedAt": "<RFC3339 UTC timestamp>",
  "createdBy":     "twig-cli/<semver>"
}
```

Purpose: an unambiguous marker that this `.twig/` was created by the new
layout. Its presence distinguishes a valid new-layout worktree from a
half-migrated legacy tree; its absence triggers `error:
layout-marker-missing` on any managed read.

#### 4.2.2 `attachment.json`

```json
{
  "$schema": "twig-attachment/v1",
  "version": 1,
  "connectionRef": "<opaque hash of twig.json connection block; §5.1>",
  "primaryScope": {
    "workItemId":  <positive integer>,
    "workItemUrl": "<opaque url string>",
    "attachedAt":  "<RFC3339 UTC timestamp>"
  },
  "activeClaim": {
    "claimId":  "<opaque claim identifier — schema owned by AB#737>",
    "mintedAt": "<RFC3339 UTC timestamp>"
  }
}
```

`primaryScope` and `activeClaim` are independently nullable (either may be
`null`). A managed worktree may have no primary scope yet (freshly
initialized), a scope without a claim (browsing/authoring only), or a
scope with a claim (§9). The claim record's own fields live in the
system store (§5) and are frozen by AB#737; this file only holds the
reference key.

`workItemUrl` is stored so a stolen or moved `.twig/` can be recognized
against the wrong connection at read time even before the system store
answers — the URL's origin must match the `connection` bound in
`twig.json`; if not, `error: attachment-connection-mismatch`.

#### 4.2.3 `worktree.json`

```json
{
  "$schema": "twig-worktree/v1",
  "version": 1,
  "worktreeFingerprint": { }
}
```

`worktreeFingerprint` contains the §3.2 tuple. Purpose: allow drift
detection without shelling to Git on every read. Refreshed only by
managed initialization or explicit reattach; every ordinary command
re-derives the current tuple with `git rev-parse` and compares
byte-equal to this file. Mismatch → `error: worktree-fingerprint-drift`.

#### 4.2.4 `cache/twig.db`

SQLite database with WAL mode enabled, one DB per worktree,
schema-migrated by existing cache infrastructure. Its filename and role
carry forward from the legacy layout; only its location moves out of
`.twig/<org>/<project>/`. Schema evolution remains the cache module's
concern; this design does not fix the schema.

#### 4.2.5 `journals/` and `ado-plans/`

Existing plan-file discipline continues unchanged. `ado-plans/` remains
the exact plans directory `ado-session`, `do-work`, `ado-publish`, and
`/next` already write. `journals/` is reserved for Change Proposal /
recovery journals as fixed by the discovery decisions; its file layout
is out of AB#736's scope.

#### 4.2.6 `tmp/`

Every atomic write (§6) lands first as `<worktreeRoot>/.twig/tmp/<uuid>`
then `rename(2)`-s into place. `tmp/` is truncated at managed init and
cleared on process exit; any residue on startup is deleted, since
`rename(2)` is atomic and success would have removed the temp.

### 4.3 System-local tier — `~/.twig/`

One user-owned directory, shared across every managed worktree on this
machine. On Linux the location is `$XDG_STATE_HOME/twig` when
`XDG_STATE_HOME` is set; otherwise `~/.twig`. On macOS and Windows the
existing platform-appropriate user directory is honored by the existing
`TwigPaths` bootstrap; this design does not change the platform
selection.

Layout:

```
~/.twig/
├── system.db            # SQLite (WAL); see §4.3.1
├── layout.json          # version marker, same shape as §4.2.1
└── tmp/                 # atomic-write staging
```

#### 4.3.1 `system.db` tables

Schema-managed by existing infrastructure through source-generated
migrations. The tables **AB#736 fixes** as required for AB#738 and
AB#739 are:

| Table | Purpose | Columns (logical) |
|---|---|---|
| `connections` | Connection registry (the set of `{org, project}` pairs this user has bound). | `connectionRef` (PK), `organization`, `project`, `team`, `firstSeenAt`, `lastSeenAt` |
| `worktrees` | Every managed worktree this user has initialized. | `worktreeFingerprint` (PK — canonical JSON of §3.2 tuple), `connectionRef` (FK), `worktreeRoot`, `initializedAt`, `lastSeenAt`, `retiredAt` (nullable) |
| `claims` | Local claim record index. | `claimId` (PK), `connectionRef` (FK), `worktreeFingerprint` (FK), `workItemId`, `state`, `mintedAt`, `endedAt` (nullable), `recordJson` (schema owned by AB#737) |
| `profileCache` | Cached process/profile metadata. | `connectionRef` (PK), `profileIdentity`, `profileVersion`, `payload`, `fetchedAt` |
| `layoutMeta` | Single-row version marker mirroring `layout.json`. | `version`, `initializedAt`, `createdBy` |

Credentials, refresh tokens, and personal-access tokens are **not** in
`system.db`; they remain in the OS credential store. The system store
carries no per-work-item field values other than what AB#737's claim
record schema demands inside `claims.recordJson`.

`system.db` is opened `SHARED` with `busy_timeout = 5000ms`. On a
`SQLITE_BUSY` after retry, the storage layer surfaces `error:
system-store-locked`; the calling verb decides retry policy.

## 5. Cross-tier keys

### 5.1 `connectionRef`

`connectionRef = lowercase-hex(sha256(canonical-json({
  "organization": <connection.organization>,
  "project":      <connection.project>
})))`

Canonical JSON = UTF-8, sorted keys, no whitespace. Team is intentionally
excluded so that changing the default team on a repository does not
invalidate registry rows.

`connectionRef` is a stable opaque key, not a filesystem path. It is the
only cross-tier link between `twig.json` and `system.db`; the older
`.twig/<org>/<project>/` sanitized-path scheme is retired.

### 5.2 `worktreeFingerprint` (system store key)

The system store keys `worktrees.worktreeFingerprint` as the canonical
JSON of the §3.2 tuple (UTF-8, sorted keys, no whitespace). Equality is
byte-equal on that canonical form; there is no fuzzy match on `worktreeRoot`
substrings.

### 5.3 `claimId`

Opaque, high-entropy, URL-safe. The exact identifier shape (ULID vs
UUIDv7 vs some other encoding) is owned by AB#737. Storage treats it as
an opaque string of at least 22 and at most 64 characters; §9's
interfaces do not inspect its contents.

## 6. Atomicity and ordering

### 6.1 File-level atomicity

Every write into `.twig/`, `~/.twig/`, or `twig.json` follows the same
rule: **write to a sibling temp under the correct `tmp/`, `fsync(2)`,
then `rename(2)` into place**. Windows uses `MoveFileExW` with
`MOVEFILE_REPLACE_EXISTING`. Callers never `open(2, O_TRUNC)` the target
file directly.

`rename(2)` is the observable success boundary. A crash between temp
write and rename leaves the previous version intact; a crash between
rename and further writes leaves the new version intact.

### 6.2 SQLite atomicity

`system.db` and `cache/twig.db` are WAL-mode. Every mutating verb runs
inside a single `BEGIN IMMEDIATE`/`COMMIT` transaction. The `layoutMeta`
row is inserted inside the initialization transaction so the DB is never
"created but not marked".

### 6.3 Managed-init ordering

`twig init` on an empty `<worktreeRoot>` runs the following steps in
order. Every step is idempotent on retry; a partial init is safe to
re-run and yields the same end state.

1. **Detect and validate roots** (§3.1). Any failure stops before any
   filesystem write.
2. **Ensure the system store exists.** Create `~/.twig/` if missing,
   write `layout.json` atomically if absent, open `system.db`, and run
   pending migrations inside a single transaction. Insert
   `connections`/`worktrees` rows for this run at the end of the whole
   init transaction (step 10), not here.
3. **Refuse legacy layouts** (§7) or accept a `--reinitialize` archive
   step. If the check trips, `.twig/` is left untouched and the run
   stops.
4. **Create `<worktreeRoot>/.twig/`** with mode `0700`, and inside it
   create `tmp/`, `cache/`, `journals/`, `ado-plans/` empty directories.
5. **Write `.twig/layout.json`** atomically. This marker is the
   observable "new layout" flag; absence here is a partial init and
   step 10 will roll it back.
6. **Write `.twig/worktree.json`** atomically with the §3.2 tuple.
7. **Write `.twig/attachment.json`** atomically with `primaryScope =
   null`, `activeClaim = null`, and `connectionRef` derived from the
   `twig.json` about to be written in step 8. If `twig.json` already
   exists at the worktree root and its connection is bound, use those
   values; otherwise use the values the invoker supplied on the
   command line.
8. **Write `<worktreeRoot>/twig.json`** atomically if it does not already
   exist. If it does, verify the existing file parses and its
   `connectionRef` equals the one written in step 7; a mismatch is
   `error: checked-in-connection-mismatch` and every `.twig/` file
   written in steps 4–7 is deleted before returning.
9. **Update `<worktreeRoot>/.gitignore`** (append `/.twig/` if absent).
10. **Register with the system store** in one transaction: insert or
    update `connections`, insert `worktrees` (or reactivate a
    `retiredAt`-non-null row for the same fingerprint), update
    `layoutMeta.lastSeenAt`. Commit.

If any step 4–10 fails, the process removes every file it created in
this run, including the `.twig/` directory itself when this run created
it, and returns the underlying error. Step 9's `.gitignore` edit is
observable and intentional; on failure after step 9 the `.gitignore`
change is left in place (it is harmless without `.twig/` present).

`twig init --reinitialize` (§7) inserts an "archive legacy" step between
3 and 4; every other step is unchanged.

### 6.4 Runtime read ordering

Every command that touches storage resolves the tiers in this order:

1. Detect roots (§3.1). On failure, STOP.
2. Read `<worktreeRoot>/twig.json`. Compute `connectionRef`.
3. Read `<worktreeRoot>/.twig/layout.json`. If missing, STOP with
   `error: layout-marker-missing` (managed-init hint).
4. Read `<worktreeRoot>/.twig/worktree.json`. Recompute the §3.2 tuple
   from the live `git rev-parse` and compare byte-equal.
5. Read `<worktreeRoot>/.twig/attachment.json`. Compare its
   `connectionRef` to step 2.
6. Open `~/.twig/system.db`; verify the `worktrees` row for this
   fingerprint exists and its `connectionRef` matches step 2.

Any mismatch surfaces a **named** error (§8) rather than silently
falling through. `system.db` is opened lazily — a targeted command that
does not need the registry (e.g. a stateless read of the cached DB) may
skip step 6, but every claim-touching and attach-touching command MUST
run all six.

## 7. Legacy layout — forced reinitialization

Legacy layout = `<worktreeRoot>/.twig/` exists but `layout.json` is
absent, or `<worktreeRoot>/.twig/<any>/<any>/twig.db` exists at the old
`org/project` path. On any managed read (§6.4 step 3) or on managed init
(§6.3 step 3), the presence of legacy layout triggers **hard refusal**
by default.

Named error: `error: legacy-layout-present`. The message names the
detected legacy paths and hints `twig init --reinitialize`.

`twig init --reinitialize` on a legacy tree:

1. Runs §6.3 steps 1–3, then before step 4:
2. Renames the entire existing `.twig/` tree to
   `<worktreeRoot>/.twig-legacy-<RFC3339-UTC-timestamp>/` via
   `rename(2)`. Nothing inside is inspected or copied; the discovery
   decision *"No migration from the current worktree
   `.twig/{org}/{project}/twig.db` layout is required"* remains
   authoritative.
3. Runs §6.3 steps 4–10 against the now-empty worktree root.

If step 2's rename fails (permissions, cross-device link, path in use by
another process), the run stops with `error: legacy-archive-failed` and
no new `.twig/` is created. The human owns the retry.

The system store handles the same case as follows: any `worktrees` row
whose `worktreeFingerprint` matches the current worktree but whose
`connectionRef` disagrees with the freshly-computed one is marked
`retiredAt = now` inside the init transaction and a new row is inserted.
`claims.recordJson` is preserved as history; the claim's `state` column
is updated by AB#737's rules, not by this reinit path.

## 8. Failure modes

Every storage error surfaces as a **named** error with a short opaque
identifier the calling verb can route on. This section enumerates every
identifier this design introduces; §9's interfaces raise exactly these.

| Identifier | Trigger |
|---|---|
| `not-a-git-worktree` | `git rev-parse --show-toplevel` fails or returns empty. |
| `bare-repository-not-supported` | `git rev-parse --is-bare-repository = true`. |
| `not-at-worktree-root` | Managed-init verb invoked in a descendant directory. |
| `layout-marker-missing` | `.twig/layout.json` absent while `.twig/` exists. |
| `worktree-fingerprint-drift` | Live §3.2 tuple != `worktree.json`. |
| `attachment-connection-mismatch` | `attachment.json.connectionRef` != current `twig.json` connectionRef. |
| `attachment-fingerprint-drift` | Attempt to attach/claim while `worktreeRoot` in `attachment.json` disagrees with `worktree.json` (i.e. `.twig/` moved). |
| `checked-in-config-invalid` | `twig.json` unparseable or missing required fields. |
| `checked-in-connection-mismatch` | Existing `twig.json` connection disagrees with values supplied to `twig init`. |
| `legacy-layout-present` | Legacy `.twig/` detected without `--reinitialize`. |
| `legacy-archive-failed` | Legacy directory could not be renamed to `.twig-legacy-<ts>`. |
| `system-store-locked` | `system.db` returned `SQLITE_BUSY` after the 5s retry window. |
| `system-store-schema-mismatch` | `layoutMeta.version` in `system.db` does not match this binary's expected version. |
| `worktree-not-registered` | `system.db.worktrees` has no row for the current fingerprint on a claim-touching command. |
| `worktree-retired` | The `worktrees` row for this fingerprint has non-null `retiredAt`. |
| `atomic-write-failed` | Temp write, `fsync`, or `rename` failed at the OS level. |

Every identifier is stable across releases; adding a new one is a
schema change to this document. Storage never returns an unnamed error
to AB#738 / AB#739.

## 9. Storage interface consumed by AB#738 and AB#739

The design surface implementors of the downstream tickets bind against
is described here in language-neutral form. Names below map 1:1 to the
storage-domain services those tickets register; exact type names and
namespaces are the implementer's concern.

### 9.1 `IWorktreeAnchor`

```
Detect() -> WorktreeAnchor         # runs §3.1 + §3.2; may raise any §8 root/detect error
```

`WorktreeAnchor` carries `worktreeRoot`, `gitCommonDir`, `worktreeGitDir`,
and `worktreeFingerprint`. It is immutable per process.

### 9.2 `ICheckedInConfigStore`

```
TryRead(worktreeRoot) -> CheckedInConfig?          # returns null if twig.json absent
Read(worktreeRoot)    -> CheckedInConfig           # raises checked-in-config-invalid on parse failure
Write(worktreeRoot, CheckedInConfig) -> void       # atomic §6.1
ConnectionRef(config) -> string                    # §5.1
```

Consumers: AB#738 (attach verb reads the connection binding to name the
correct system-store row); AB#739 (claim mint reads the connection
binding to key the claim record).

### 9.3 `IWorktreeLocalStore`

```
Initialize(worktreeAnchor, connectionRef) -> void
   # writes .twig/layout.json, worktree.json, empty attachment.json (§6.3 steps 4-7)

ReadAttachment(worktreeAnchor) -> Attachment
   # runs §6.4 steps 3-5; raises named errors

WriteAttachment(worktreeAnchor, Attachment) -> void
   # atomic §6.1; verifies connectionRef unchanged; raises attachment-connection-mismatch

CachePath(worktreeAnchor) -> string
   # <worktreeRoot>/.twig/cache/twig.db

JournalsRoot(worktreeAnchor) -> string
   # <worktreeRoot>/.twig/journals/
```

`Attachment` is exactly the shape in §4.2.2. `primaryScope` and
`activeClaim` are independently nullable; consumers set one field without
disturbing the other.

Consumers: AB#738 sets `primaryScope`; AB#739 sets `activeClaim`. Both
tickets bind `Attachment` through this store; neither reads or writes
`attachment.json` directly.

### 9.4 `ISystemStore`

```
Registry:
   UpsertConnection(connectionRef, organization, project, team) -> void
   UpsertWorktree(worktreeFingerprint, connectionRef, worktreeRoot) -> void
   FindWorktree(worktreeFingerprint) -> WorktreeRow?
   RetireWorktree(worktreeFingerprint) -> void
   FindConnectionByRef(connectionRef) -> ConnectionRow?

Claims:
   Insert(claimId, connectionRef, worktreeFingerprint, workItemId, recordJson) -> void
   UpdateState(claimId, newState, endedAtOrNull, recordJson) -> void
   Find(claimId) -> ClaimRow?
   FindReserved(connectionRef, workItemId) -> ClaimRow?
      # returns the row for the given (connectionRef, workItemId) whose
      # state is in the reserved set { pending, active }, or null.
      # "pending" is AB#739's pre-projection reservation row (§4 of
      # AB#737); "active" is a fully minted claim. Reservation and
      # active are the two states that MUST remain unique per
      # (connectionRef, workItemId), so the enforcement lookup covers
      # both in one call. Downstream callers never widen this set to
      # released / superseded / retired rows.

ProfileCache:
   Read(connectionRef) -> ProfileCacheRow?
   Write(connectionRef, profileIdentity, profileVersion, payload) -> void
```

All methods are atomic (§6.2) and raise `system-store-locked` /
`system-store-schema-mismatch` on the corresponding failure. `Insert`
raises `worktree-not-registered` if the referenced fingerprint is
missing; the caller MUST register the worktree first via
`UpsertWorktree`.

`Claims.recordJson` is passed through opaquely — its inner schema is
owned by AB#737 (companion sub-spec). This design fixes only that
`recordJson` is a JSON string of at most 64 KiB and that inserting a
row does not inspect its contents beyond schema validation the AB#737
layer runs independently.

Consumers: AB#738 calls `UpsertWorktree` after a successful attach.
AB#739 calls `FindReserved` to enforce the local-duplicate rule
(discovery decision "Local duplicate claim rule") — a non-null result
refuses a new mint whether the incumbent is a pending reservation or a
fully active claim — then `Insert` / `UpdateState` to mint / retire.

### 9.5 Initialization contract for downstream tickets

Every AB#738 / AB#739 command that reads or writes storage runs, in
order:

1. `worktreeAnchor := IWorktreeAnchor.Detect()`
2. `config := ICheckedInConfigStore.Read(worktreeAnchor.worktreeRoot)`
3. `connectionRef := ICheckedInConfigStore.ConnectionRef(config)`
4. `IWorktreeLocalStore.ReadAttachment(worktreeAnchor)` (verifies
   layout marker, fingerprint, and connection ref)
5. `ISystemStore.FindWorktree(worktreeAnchor.worktreeFingerprint)` —
   MUST return non-null and non-retired; otherwise raise
   `worktree-not-registered` / `worktree-retired`.

Only after step 5 succeeds may the ticket's own logic run. This
sequence is the contract; the storage layer refuses to serve
half-initialized states.

## 10. What this design does not decide

Anchored here so downstream implementers do not confuse settled from
open:

- **Claim record shape and lifecycle.** Everything inside
  `Claims.recordJson`, including the exact state enum, holder identity
  capture, and transition rules — AB#737.
- **CLI verb names, argument surface, and MCP tool surface.** AB#738's
  own contract. This design fixes the *storage* verbs the CLI drives.
- **Cache schema.** Owned by the cache module; unchanged by this
  design beyond its location.
- **Journal file format.** Owned by the Change Proposal design.
- **Credential storage.** OS credential store; unchanged.
- **Non-Git directory execution.** Explicitly out of scope; managed
  init refuses (§3.1).
- **Cross-machine / cloud claim coordination.** Deferred by discovery.
