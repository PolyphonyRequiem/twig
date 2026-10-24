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
state (`empty` or `staged`) independently of conflicts or blockers. `freshness` is
explicitly `cached`; this command does not refresh remote data. If unrelated
pending work overlays the item, observed field values are withheld and
`observationCompleteness` reports `blocked-by-local-work`. Missing form values,
revision/field conflicts, refused fields, and unavailable process rules are
reported as blockers or unknown states rather than as apply-ready results.
`eligibility.authorized` is always false; inspection never creates a capture or
grants mutation authority.

The version-1 reconciliation states are `current`, `conflicted`,
`blocked-local-work`, `field-unavailable`, `capability-refused`,
`rules-refused`, and `rules-unknown`. Precedence is conflict, local pending
work, missing observed field, non-persistable field, rejected rules, unknown
rules, then current. `observationCompleteness` is `complete-for-draft-effects`
when every effect has an observed field value, `incomplete-for-draft-effects`
when at least one is unavailable, or `blocked-by-local-work` when pending work
prevents a reliable comparison. `eligibility.rules` is `not-applicable` when no
state transition is staged, otherwise `accepted`, `rejected`, or `unknown`;
accepted process validation remains advisory and the server is final.

`baselineValue`, `observedValue`, and `proposedValue` are nullable string field
values. A JSON `null` with `observedValueAvailable: true` means the native value
is empty/unset; `null` with `observedValueAvailable: false` is only a placeholder
and must not be treated as an observation. `eligibilityReason` is `null` only
when the observed value is available and the native sink allows the field.
Consumers must reject an unrecognized `draftDocumentVersion`; this command
currently emits only version 1.

`draft show` never creates a missing draft: it returns an error and leaves the
store untouched. A new draft starts at generation 0 with baseline and observed
revision equal to the admitted cached revision. A changed field advances the
generation; later observations may advance it while preserving the original
baseline and effects. The baseline is never silently rebased.
