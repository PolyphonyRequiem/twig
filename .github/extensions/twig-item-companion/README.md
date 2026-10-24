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
