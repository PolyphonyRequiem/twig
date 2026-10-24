# Twig item companion

This project-scoped Copilot canvas is the first read-only UI slice for Twig. It loads
one item through `twig bench detail <id> --document -o json` from the current
session workspace and displays the native version-2 detail document. It does not
read Twig's SQLite files or call an ADO/GitHub API directly.

The canvas intentionally does not edit, save, authorize, apply, post discussion,
upload/download attachments, or infer relationship coverage. The CLI remains the
authority for workspace admission and detail reads. Twig must be available as
`twig` on the extension process's `PATH`.

No dependencies are bundled. The Copilot runtime supplies
`@github/copilot-sdk/extension`. Local checks use Node's built-in test runner:

```powershell
node --test .github/extensions/twig-item-companion/tests/*.test.mjs
```

## Read-only v2 boundary

The adapter consumes the native export from `a5d6f5cf`, not a second item model
or parsed ANSI. It validates agreement between `itemKey.workItemId`, `item.id`,
`document.workItemId`, and the item/document revision before publishing an
observation. Missing identity, contradictory layout provenance, malformed form
structure or unsupported value states refuse the read rather than rendering
empty-looking success. Refresh requires the captured Bench, binding and identity
through CLI flags and checks the captured connection on the returned observation.
No active-item selection or `--sync` is issued.

The checkout/connection/binding/item qualification is the stable item key;
revision is a separate observed baseline. Refresh from r17 to r18 preserves
identity but grants no draft migration or edit authority. Server layout flags,
including `readOnly: false`, never grant editing: the UI explicitly displays
`editing: unavailable`. Layout status/source and field value states remain
distinct. Non-custom pages such as History carry structure only, not their
content. Missing appearance metadata remains unavailable; icon metadata grants
no asset-fetch authority. HTML and field values are rendered with `textContent`,
not interpreted.

Feature scope: this is a narrow item-reading foundation for #1176 and the shared
read boundary needed by #1175. It does not deliver #1175 Bench triage or shared
draft persistence, nor #1174 retained proposal review, assessment or digest-bound
authorization. No staging/edit/apply affordances are introduced.

`tests/fixtures/detail-v2.json` is fictional JSON matching the native v2 wire
shape (including omitted nulls and numeric field states). The integration harness
runs the production reader with that stdout at the process boundary, the real
loopback API, and the client rendering script in a minimal DOM harness. It checks
qualification, r17-to-r18 identity continuity, inert full values, layout/field
provenance and unavailable authority. These offline checks do not qualify
extension discovery, installation, reload, host placement or live authentication.

## Theme

The labelled Theme selector offers Auto, Light and Dark without changing host
attributes, Twig defaults or item state. Auto follows the documented host
`data-color-mode` on root/body; if neither declares Light/Dark, it follows the
system color-scheme preference and reacts to changes. Auto uses host semantic
tokens with appropriate light/dark fallbacks. Explicit modes use a local palette
even if the host changes mode. Keyboard focus covers the selector and disclosure
summaries as well as buttons/inputs.

The override belongs to the current document only and resets to Auto when the
iframe reloads. It is not advertised as a durable user preference: ephemeral
loopback origins cannot reliably provide cross-instance browser storage. No
localStorage, native preferences or new endpoint is used. This styling slice
does not add workbench/proposal-review surfaces or qualify actual host rendering.

## Named-Bench workbench (#1198)

The same provider also declares `twig-workbench`, with `load_bench({ bench })`
and `refresh_bench({})` actions. The separate workbench panel accepts an existing
saved name or decimal ID; its reader invokes only:

```text
twig workspace --bench <name-or-id> --include-browser --browser-version 2 --view tree -o json
```

This contract is pinned to native prerequisite **#1210**, commit
`3ff46fa7565b2231a872082cd34d0e1988b86a7e`. It is not available in older Twig
binaries. An unsupported invocation or schema is a visible refusal, never an
ANSI parser, current-Bench fallback, temporary switch or second query backend.
The UI branch consumes the exported contract; it does not carry that native
branch's ancestry. Real-binary integration awaits delivery of #1210 to main.

Re-reading uses the captured stable Bench ID plus native expected Bench, binding
and identity flags, and checks the complete returned origin against the previous
observation, including connection/attachment evidence. Each CLI call has a
15-second timeout, abort signal, shell disabled and a 4 MiB output bound.
The server serializes reads; failure retains the previous qualified observation.
Its state is panel-local read evidence, not a durable draft or user preference.

Members retain native evaluator order, including known uncached IDs. The
hierarchy retains native nesting and ancestor-context labels; it does not parse
the native styled label. Filtering is local to loaded row text and retains
matching hierarchy ancestors. Counts distinguish loaded/known/uncached members
from rendered hierarchy nodes. Source totals and undiscovered counts remain
unknown, even when the cache is empty. Native omission IDs, projection limits
and reasons are shown; no continuation or complete-source claim is invented.
Saved area/sprint/pin configuration is inert read-only JSON.

All rows are native **raw-mirror** evidence, not composed durable-edit values.
Seeds are labelled local seeds. Pending markers are not edit permission or proof
that displayed values include local changes. Selection shows the qualified raw
summary and restores filter/view/focus on Back or Escape, without `twig set` or
changing shared current-Bench state. Untrusted text is inserted using
`textContent`, never HTML or arbitrary links/assets.

**Explicit-Bench detail (authored, UNVALIDATED against a real binary):** selecting a
loaded member calls `twig bench detail <id> --bench <captured Bench id> --document
-o json` with `--expect-bench/--expect-binding/--expect-identity` (native contract at
twig `06cb33b2`, not yet on main). It is cache-only (never `--sync`), never touches the
shared current Bench, and the reply's `origin` and `benchId` must equal the origin
captured by the v2 browser read, or the detail is refused. Non-member and not-cached
items surface the native error code. The form is Twig's fallback layout
(`layoutStatus: unavailable`), shown as inert text with membership and completeness
reasons; `sourceComplete` is never true and no remote child count is claimed.

**Remaining seam:** when the detail call fails or the native command is absent, the UI
keeps the raw-summary disclosure. **#1198 cannot close until this integration is
validated against the delivered native binary, or Daniel explicitly accepts the seam.**
No editor, Save/capture, proposal review/Apply, provider handoff, install/reload or
host-runtime qualification is included. `tests/bench-detail.test.mjs` uses synthetic
fixtures over a fake process boundary: composition coverage, not native integration.

`tests/fixtures/bench-v2.json` pins the native v2 DTO/projection shape at the
prerequisite SHA. The workbench harness runs the real rendered client script
against the real loopback server and production reader, with only the CLI
process/filesystem boundary faked. Fixture evidence is not real-binary or host
qualification.
