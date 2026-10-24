# Review-model capability advisory (add-link)

Status: Draft (AB#1191). Additive within review `modelVersion` 1.

An `add-link` operation in a change-proposal review carries an optional `capability` member:

```json
"capability": { "relationship": "parent-child", "declared": "native", "availability": "unknown", "reason": "not-probed" }
```

## Meaning

- `relationship`: `parent-child`, `predecessor-successor`, `related`, or `unknown` for an unrecognised relation.
- `declared`: what the configured provider's static capability catalog declares: `native`, `emulated`, `unsupported`, or `unknown` (no descriptor, or unknown relation).
- `availability` / `reason`: always `unknown` / `not-probed` in this slice. Nothing was probed, so nothing is claimed about permissions or runtime state.

This is a **declaration, not an observation**. A previewed edge has not been written or read back, so the review never carries an `origin` and never says `provider-native`. Origin is reported only after provider readback.

## Boundaries

- Outside canonical effects, consequences and the proposal digest. Digests and request bytes are unchanged.
- Resolved from configuration plus the static catalog; no provider is constructed and no network or credential is touched.
- Only `add-link` operations carry it. Readers must tolerate its absence (older journal rows) and ignore unknown members.
- The required-provider fail-closed behaviour and the GitHub write gate are unchanged. No emulation is activated.
