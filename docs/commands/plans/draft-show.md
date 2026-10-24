# `twig draft show`

Inspect the shared recoverable draft and its latest native conflict state:

```text
twig draft show <id> [-o human|json|minimal]
twig draft show <id> --document -o json
```

The command resolves the current qualified item identity through native
admission, then loads the draft shared by native surfaces. When no other local
pending work obscures the current values, it records a changed observed revision
and conflicts using generation CAS. It preserves the original baseline and
proposed values. If pending native work exists, it does not infer field
conflicts from overlaid values; it reports the draft and identifies the local
work as a blocker.

Output supports `human`, structured `json`, and a tab-separated `minimal`
summary. `ids` is not supported. A shown or saved draft is not an authorization
or an apply-ready capture.

`--document -o json` emits `draftDocumentVersion: 1`, a versioned native
inspection envelope for companion/workbench consumers. It includes the stable
qualified item key, draft baseline/observed/current revisions and generation,
typed baseline/observed/proposed values for each draft effect, per-effect native
persistability, reconciliation state, and rules eligibility. In this envelope,
`status` is the reconciliation state and `draftStatus` records the local draft
state. `freshness` is
explicitly `cached`; this command does not refresh remote data. If unrelated
pending work overlays the item, observed field values are withheld and
`observationCompleteness` reports `blocked-by-local-work`. Missing form values,
revision/field conflicts, refused fields, and unavailable process rules are
reported as blockers or unknown states rather than as apply-ready results.
`eligibility.authorized` is always false; inspection never creates a capture or
grants mutation authority.
