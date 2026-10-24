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
