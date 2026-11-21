# Repository guidance

## Developer-local data is disposable

Until the developer explicitly says otherwise, **always assume developer-local
Twig data can be discarded**. This includes local databases, caches, Bench
definitions, drafts, local history and runtime metadata. Do not ask whether this
application data must be preserved as a prerequisite to development or delivery.

When a local schema or model changes, prefer a reset and supported fresh
initialization. **Do not implement or investigate local-data migrations**,
backward persistence compatibility, historical binary/cohort checks, provenance
recovery or database-rewind machinery merely to preserve old developer data.
Only pursue migration work when the developer explicitly requests it. Existing
old data is not an implicit request for compatibility work.

Scope resets to explicitly identified Twig application data. This policy does
not permit deleting source code, uncommitted work, upstream ADO/GitHub items,
credentials, certificates, Startup entries or unrelated machine configuration.
Keep the normal provider, authentication, policy, digest and journal checks for
new operations. A local reset is not proof of any remote operation's outcome and
must never be used to justify replaying an uncertain write.

## Agent skills

`.github/copilot-instructions.md` is obsolete and is not current agent guidance.

### Issue tracker

GitHub Issues in `PolyphonyRequiem/twig`, preferably through Twig to dogfood
supported operations, otherwise built-in GitHub tools. See
`docs/agents/issue-tracker.md`.

### Triage labels

Use the five default triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: root `GLOSSARY.md` and `docs/adr/`. See `docs/agents/domain.md`.
