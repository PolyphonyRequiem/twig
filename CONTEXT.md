# Twig domain glossary

Use the implementation's names consistently in code, documentation and work
items. Current source is the tiebreaker; a historical plan does not establish
that a feature shipped.

## Connections and views

| Term | Meaning |
| --- | --- |
| **Connection** | A pinned provider source and admitted identity. Repository names, URLs and display names are locators, not substitutes for immutable source/principal identity. |
| **Workspace** | A Git worktree's initialized Core client store and local context. Also the name of the CLI's static selected-Bench view; qualify the meaning when necessary. |
| **Bench** | A named, durable, user-wide saved selection of work. CLI and daemon share its definition. Creating a Bench does not select it or claim its work. |
| **Selector** | Retained membership intent: a single-item pin, hierarchy subtree, or typed query/planning rule. Store the rule, not today's result IDs. |
| **Active item** | A local focus pointer for human convenience, not assignment, ownership or a work claim. Machine operations name their targets explicitly. |
| **Exclusion** | A workspace-view rule outside shared Bench membership. It does not delete a provider item or discard protected drafts. |
| **View profile** | A daemon presentation context with explicit selection and source admission. It is not a second authoritative copy of shared Bench intent. |

Independent selectors contribute by union. Conditions within one planning rule
can narrow each other. Missing sources are unavailable/degraded, not proof of
empty membership; sources with zero members still remain dependencies.

## Items and local intent

| Term | Meaning |
| --- | --- |
| **WorkItem / ItemKey** | A typed Core item and its connection-qualified identity. A native numeric locator or negative CLI alias is not a universal identity. |
| **Observation** | Retained provider data with revision/freshness evidence. A cached observation does not prove current access or current remote values. |
| **Draft** | Typed local intended changes, separate from the provider observation and bound to captured baseline/store evidence. |
| **Seed** | An unpublished creation draft with an original canonical UUIDv7. Its negative display alias is a convenience, not a sequential allocator or predicted provider ID. |
| **Publication mapping** | Permanent original-local-to-provider identity evidence after qualified publication. A matching title cannot establish this mapping. |
| **Planning metadata** | Acquired native areas, iteration paths and the exact team's calendar. Metadata refresh does not replace saved rules or publish items. |

Parent/child means decomposition, predecessor/successor means dependency, and
Related means association. Local-only associations are explicitly distinct from
native provider relationships.

## Proposals and outcomes

| Term | Meaning |
| --- | --- |
| **Proposal** | Immutable typed intended effects bound to connection, schema/store evidence and a canonical digest. |
| **Preview** | Evaluation of current evidence and blockers, with possible local retention. It does not publish or lease remote state. |
| **Apply** | Exact-digest execution under current admission, policy, concurrency and journal checks. |
| **Operation journal** | Core's durable execution authority for original operations, attempts and receipts. Transport sidecars only provide correlated evidence. |
| **Acknowledgement** | The provider's attributed response to an original attempt; not independent verification. |
| **Verification** | Independent readback of intended effects. Applied and Verified are distinct outcomes. |
| **Settlement** | Updating eligible local intent/evidence from original qualified outcomes, never replaying an uncertain write. |
| **Declared policy** | Explicit configured mechanical rules, not inferred business policy or discovery of external repository automation. |
| **Close gate** | A field requirement coupled to a completed-category transition. Discover actual requirements and carry coupled fields with the transition in one qualified operation. |

Multi-operation execution is not one distributed transaction. Verified, failed,
partial, indeterminate and unattempted work remain distinct. A lost response,
local reset or empty search never authorizes replay.

## Implementation ownership

`Twig.Core` owns work-management types and execution contracts.
`Twig.Store.Sqlite` persists client state; provider adapters own native
acquisition and effects. `Twig.Infrastructure` binds native identity, stores,
policy and host APIs. Twig also owns the optional Windows daemon; its separate
canvas package owns presentation assets.

See [current architecture](docs/architecture/overview.md),
[Benches and context](docs/features/workspace-bench-context.md),
[proposals](docs/features/proposals.md), and [daemon operation](docs/daemon.md).
