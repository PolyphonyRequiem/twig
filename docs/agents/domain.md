# Domain context for agents

Read root `CONTEXT.md` for current vocabulary and
`docs/architecture/overview.md` / `clean-core-contract.md` for supported
composition. Current source decides behavior and naming.

Relevant ADRs, `wayfinder/`, `wayfinder-1.0/`, historical specs, project plans and
research explain decisions and captured evidence. They may describe removed
Domain/Shared models or unsupported integration routes. Do not treat old source
paths, negative seed identities, plan aliases, package APIs or delivery recipes
as current requirements.

Qualify connection, Bench, local context, authoritative provider identity and
cached observation. A Bench is user-wide saved intent, not ownership or a sync
unit. A seed is an unpublished Core UUID-backed draft, not a remote item.

Before a structural change, read only the decisions relevant to its actual
boundary. Surface meaningful conflicts with a still-applicable ruling, but do
not revive retired machinery or invent migrations for disposable local data.
Keep current behavior and archived design claims explicitly distinct.
