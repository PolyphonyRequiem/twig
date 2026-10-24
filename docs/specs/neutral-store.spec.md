# Provider-Neutral Local Store — Functional Specification

> Status: **Draft** — Epic #1168 (ADO `PolyphonyRequiem/Twig`), 2026-10-23.
> Implementation is **experimental and off by default**: it runs only when
> `TWIG_EXPERIMENTAL_NEUTRAL_STORE` is exactly `1`. With the variable unset, every surface behaves
> exactly as it did before this epic. No workspace migration, cutover or default change has been
> authorised, and this spec does not alter the epic's acceptance criteria.

## 1. Approved basis

These decisions were approved by Daniel. Change them only through an explicit superseding decision.

| # | Decision |
|---|---|
| Engine | SQLite, kept until scale or fitness shows it won't work. |
| Layout | Split: a **durable file** (identities, mappings, local values, tombstones, pending changes, journal; written in one durable-file transaction) plus a **rebuildable mirror** of remote state. There is no cross-file crash-atomicity claim. |
| 1 | The local ID is a GUIDv7. ADO numeric IDs stay as the CLI/JSON display alias. |
| 2 | Each local record has an integer local CAS revision, separate from provider tokens. |
| 3 | Field values use closed, source-generated variants (AOT- and trim-safe). |
| 4 | The store keeps current state plus tombstones, with no full revision history. |
| 5 | A clean cutover deletes the mirror and per-worktree caches. Legacy pending changes are removed only after unpublished changes have been surfaced or exported. |
| 6 | When a remote outcome is uncertain, the change stays pending and the provider is re-checked before any retry. |
| 7 | #1168 owns durable-schema version numbering (v13 is #1168, v14 is #1170). |

**Shared with #1167 and #1170**
- **External key:** (`ProviderKind`, `ResourceScope`, `NativeId`).
  - `ProviderKind` is `ado` or `github`. Kinds are lowercase, stable and never reused.
  - For ADO, the scope is the organization/collection instance; the project is mutable metadata.
  - For GitHub, the scope is the normalized API host origin.
  - `NativeId` is opaque and case-preserving.
  - There is no claim of ID continuity across a transfer. A changed native ID is a new external identity and needs an explicit mapping.
- **Provider revision token:** an optional opaque string, stored as last observed. Whether a provider can check conflicts is declared separately from whether a token is present.
- **Relationships:** `LinkKind` serialized through the stable `LinkKindNames` tokens.
  - `ParentChild` and `PredecessorSuccessor` run source→target. `Related` is symmetric.
  - `Artifact` is excluded.
  - Unknown native relations stay unmapped.
  - Locally owned edges live in the durable file. Remote edges stay in the mirror.

## 2. Effective-value reads (implemented, opt-in)

When the store is opted in, `DurableFieldOverlay` gives a read surface the **effective** value:
- A durable local value wins over the mirror's value for the same field.
- The result is a **display-only copy**. Nothing is written back to the mirror or the durable file.
- It shares the mirror projection's single cached scope resolution.

The overlay shows the mirror unchanged when any of these hold:
- the scope is unresolved;
- the item has no durable identity;
- the record is tombstoned;
- there are no local fields.

Scope, transport, configuration and identity-map failures are surfaced, not hidden.

| Surface | Overlaid | Notes |
|---|---|---|
| `twig show <id>` | yes | |
| `twig show --batch` | yes | |
| `twig show --tree` / `twig workspace --tree` | focused item only | Parent chain and children still show mirror values, and sync and sibling counts read the mirror item. |
| `twig show --include-fields` (`exportVersion` 1) | **no — raw by contract** | See §3. |

## 3. Raw stored export stays raw

The `show --include-fields` export (`exportVersion` 1, `docs/commands/context/show-batch.md`)
promises the **entire stored field dictionary as raw stored evidence**. It is not overlaid and not
redefined, with or without the opt-in.

## 4. Deferred: effective-value export mode

An export of effective (local-over-remote) values is **deferred**. It is not to be built until the
following are decided:

1. **Distinguishable raw vs effective values.** A consumer must be able to tell a raw stored value from an effective one. Overlaid values must never appear under the raw contract.
2. **Per-field origin.** Each exported field must say whether it came from a local value or the remote mirror. A local value must never be labelled as native.
3. **Local and mirror revision semantics.** The export must define how the local CAS revision and the mirror's provider revision/token are reported, and what a consumer may infer from them.
4. **Offline scope handling.** The export must define its behaviour when the resource scope cannot be resolved (for example, offline): fall back to raw, refuse, or mark the output as unresolved.

## 5. Open acceptance gaps (acceptance criteria unchanged)

1. **ADO does not yet run entirely on the new store.** Only the reads in §2 are overlaid. Writes, non-focused tree nodes, the export and workspace lists still read the mirror.
2. **A second provider is proven at storage level only.** The GitHub provider still refuses fail-closed until #1167's activation lands.
3. **Existing workspaces are not migrated.** Migration and cutover are deferred and not authorised.
