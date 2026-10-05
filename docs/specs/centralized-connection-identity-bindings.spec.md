---
tracked_in: [1101, 1103]
epic: 1100
status: proposed
---

# Centralized connection configuration and identity bindings

The product/ownership contract was confirmed by the owner in Grilling #1101 on
2026-09-30. This is a complete proposed implementation Spec, not authorization to
implement it or change the machine's current credentials. Its source baseline is
`d5bf7e62`; the separate auth-isolation experiment is `06746e13`.

**Current delivery scope (owner decision, 2026-10-03):** MCP is withdrawn pending
a separate redesign; Task #1112 is canceled rather than completed. Other host
integration work is parked. The MCP requirements below remain historical design
context and are not acceptance gates for the current CLI delivery.

## Problem Statement

Twig currently conflates a valid Azure DevOps token with the account intended for
a connection. A globally saved personal refresh token can win over a correctly
signed-in corporate Azure CLI account. Repository-specific launchers and auth
environment variables can isolate some processes, but leave every caller to get
account selection right and do not solve multi-connection MCP lifetime.

The diagnostic reproduction used a corporate current-iteration endpoint: the
wrong-account request was throttled, while the same read through the intended
corporate identity succeeded. Detailed corporate endpoint/timestamp/usage evidence
is retained in the private tracker record, not this public design document. Those
measurements do not attribute budget spending; they establish wrong identity
selection and a successful read through the intended identity.

Another manifestation occurred while filing the Epic: a global display-name
default produced an assignee that the connected board did not recognize. A
binding must provide the actual authenticated principal for self/ownership
behavior, not merely select a bearer token or reuse a person's display name.

## Solution

Keep the repository as the anchor for normal connection operations. Its portable
endpoint, shared policy and project defaults remain checked in. Extend the
existing system-local registry with named authentication identities, explicit
identity bindings, binding defaults and user preferences. A
worktree can pin a different binding locally without committing user identity or
credential details.

All supported hosts use one deep Connection Binding module to resolve an attached
worktree, select its effective binding, construct its authenticated runtime and
enforce its lifetime. The module owns ambiguity, principal verification,
configuration provenance, operation leases, unfinished-work checks and safe
read-cache transitions. Callers do not reproduce an auth-selection ladder.

An endpoint may have multiple explicit bindings. An unpinned worktree uses its
binding default, never the last logged-in account. Changing effective
identity requires clean unfinished-work state and an explicit reconnect of live
connections. After a permitted transition, identity-dependent read data is
invalidated and refilled under the newly selected account. AAD accounts and PATs
remain supported; new service-principal grant flows are excluded.

## User Stories

1. As a corporate developer, I want my attached Microsoft repository to select its corporate binding, so that a personal login cannot hijack work requests.
2. As a personal-project developer, I want corporate work to leave my personal binding untouched, so that both connections remain usable.
3. As a repository maintainer, I want endpoint and shared policy declarations to remain portable, so that a checkout has reproducible project intent across users and machines.
4. As a teammate, I want to attach the same repository using my own identity, so that another person's credentials or local auth paths are not part of the repository.
5. As a user, I want to register and inspect identities centrally, so that I do not configure credentials separately in every repository.
6. As a user, I want an explicit binding default for an endpoint, so that ordinary commands need no repeated identity selector.
7. As a user, I want an untracked worktree-local binding pin, so that a specialized checkout can deliberately use another authorized identity.
8. As a user, I want multiple bindings for the same organization/project, so that legitimate alternate-account use does not depend on overwriting a global login.
9. As an agent, I want normal work operations outside a valid attachment to fail before work-item HTTP, so that directory context cannot silently choose a project or account.
10. As a new user, I want help, installation utilities and explicit registry/auth bootstrap administration to work before attachment, so that the attachment requirement does not create an onboarding cycle.
11. As a script author, I want missing, unknown or ambiguous selectors to produce structured errors, so that I never inherit another call's last-used connection or identity.
12. As an MCP user, I want one server to operate personal and corporate attached connections concurrently, so that one process-wide auth provider cannot cross-contaminate them.
13. As an MCP user, I want two attached worktrees using different bindings for the same endpoint to remain distinguishable, so that an endpoint-only selector cannot silently pick a checkout.
14. As a user, I want identities identified by stable principal evidence rather than display names, so that similarly named people and changing account labels do not change authority.
15. As a user, I want a wrong-account cached AAD token rejected before normal ADO requests, so that a valid audience alone cannot make it acceptable.
16. As a user, I want missing or wrong tenant/principal claims to fail closed, so that incomplete legacy metadata cannot bypass a binding.
17. As a user, I want token refresh for the same registered principal to remain automatic, so that ordinary expiry does not require a reconnect or affect other identities.
18. As a PAT user, I want the actual authorized ADO principal verified, so that lack of JWT claims does not mean either unsupported PATs or unchecked authority.
19. As a PAT user, I want replaced credential material re-attested, so that an old principal proof cannot be reused for a different PAT at the same storage location.
20. As a user, I want status to explain endpoint, effective binding, actual principal and selection/configuration provenance, so that I can diagnose account-routing errors without seeing secret material.
21. As a user, I want ambiguous or absent defaults to require setup, so that successful login to an unrelated account cannot resolve the ambiguity.
22. As a repository maintainer, I want central user preferences unable to silently weaken checked-in policy, so that personalization does not change shared work rules.
23. As a user, I want explicit configuration values preserved even when they match defaults, so that saving another preference does not change configuration intent.
24. As a user, I want a binding change blocked while unpublished edits, notes or seeds exist, so that a different actor cannot publish or erase unfinished work.
25. As a user, I want every unresolved proposal and open publish intent considered, so that an older unknown outcome cannot hide behind a newer completed proposal.
26. As a user, I want explicit native reconciliation to distinguish settled historical attempts from unknown outcomes, so that preserved history does not block identity changes forever.
27. As a user, I want identity changes never to discard, flush or replay work automatically, so that switching is not a hidden publication or deletion operation.
28. As a user, I want live connections to stop with an actionable reconnect error after effective binding changes, so that they never silently adopt another actor.
29. As a user, I want in-flight outcomes to settle before reattachment, so that an identity transition cannot race a remote write or a stale cache fill.
30. As a user, I want successful identity transitions to clear only identity-dependent read data, so that the new account cannot see a former account's cached items as its own.
31. As a user, I want credentials, attachment/policy and historical journals preserved through a switch, so that cache invalidation is not loss of authority or work history.
32. As a user, I want concurrent identities to use separate attached worktrees, so that one checkout never has competing actors writing its local state.
33. As a user, I want actual bound-principal identity to drive self filters, claim checks and default assignee values, so that global display-name preferences do not impersonate another account.
34. As a user, I want migration to preserve existing credentials and require explicit connection binding where intent is unknown, so that an old global login is not silently assigned to all repositories.
35. As a user, I want migration with incomplete identity evidence or legacy pending work to refuse safely, so that uncertainty does not become data loss or a guessed account.
36. As a user, I want selected-identity credential cleanup to leave other identities alone, so that fixing one login does not break unrelated work.
37. As a user, I want concurrent default/pin changes to be CAS-guarded, so that one management operation does not overwrite another's selection.
38. As a user, I want genuine server throttling to report its resource, retry guidance and bound identity, so that a generic rate-limit message does not conceal routing mistakes.
39. As a user, I want a server-imposed pause for one principal not to stall another principal's connection, so that auth isolation also preserves independent operation.
40. As a user, I want cached/offline inspection clearly attributed to the admitted binding, so that stale or unverifiable data is never presented as a fresh authorization result.
41. As a host integrator, I want CLI, MCP, TUI and Herdr to consume the same resolution and enforcement module, so that no host-specific launcher has to recreate the security contract.
42. As an implementer, I want consumer-level proof with real temporary stores and an identity-aware transport, so that provider-type assertions and successful DI construction cannot pass while account crossover remains.
43. As a user, I want migration activation to wait for legacy unguarded hosts to close, so that an old provider cannot issue requests or warm a newly admitted cache generation.
44. As a user, I want native retirement to prevent subsequent application of the retired intent, so that retaining its original file and Confirmed state cannot enable replay after rebinding.

## Implementation Decisions

### Domain and ownership

- Preserve Connection as the declared organization/project endpoint and preserve its existing opaque connection reference. Identity does not become part of that endpoint key. Multiple bindings associate one endpoint with explicit authentication identities and credentials.
- Authentication identity is a registered principal, not a display label, credential, process/reference profile or last-login slot. Its stable identifier is opaque; admitted principal evidence is immutable. Replacing the principal creates a new identity/binding rather than mutating the old authority in place.
- AAD evidence includes the expected issuer/tenant and principal object identifier. PAT evidence uses the authoritative ADO principal identifier in its authority namespace. Record the method-specific evidence required to prove that principal; do not pretend PATs have tenant/object JWT claims.
- Human names are unique management aliases only. Aliases, labels and email renderings never identify authority; renaming a label does not change identity or invalidate read data.
- A binding carries its connection reference, identity reference, credential reference, supported method and monotonic revision. The identity reference is the registry's opaque authentication-identity identifier, not an email/display name or a raw token claim. A credential reference is not the secret or a user-supplied filesystem path. Registry metadata must never contain bearer tokens, PATs or refresh tokens.
- Central records own identity/binding definitions, binding defaults and user-specific settings. Checked-in configuration owns the portable endpoint, shared policy and project defaults. Attachment state owns the optional checkout-local binding pin and admitted effective-binding/read-cache provenance.
- A binding pin is distinct from a Bench pin. A reference-profile pin remains a process/policy declaration, not an authentication selector. Do not reuse the existing profile field for auth.
- Extend the existing system registry and attachment CAS interfaces; do not introduce a second independently authoritative connection registry. Preserve connection-reference, worktree-fingerprint and claim-ownership checks.

### One resolution and runtime seam

- The Connection Binding module resolves a validated attached worktree into one immutable effective endpoint, identity binding, frozen principal requirements, effective configuration and operation-scoped runtime lease. Principal requirements carry expected issuer/tenant/principal object identifier for AAD, or expected ADO principal identifier in its authority namespace for PAT; credentials/cache evidence must match those requirements. The module exposes inspection and deliberate management transitions through the same authority.
- Extend the existing configuration split/provenance logic, system registry, attachment store and shared composition registration. Use the current token provider/store/guard abstractions as adapters; incorporate the useful auth-isolation work without preserving its one-identity-per-process selection constraint.
- Normal connection/work operations require a valid repository attachment. Help/version/installation utilities and explicit identity/registry bootstrap, initialization and migration administration are management exceptions, not permission to perform work-item operations without attachment.
- Resolve the checked-in endpoint first. Validate attachment fingerprint and connection reference, then select a binding pin or binding default. Verify that the selected binding belongs to that declared connection. No missing/ambiguous selection falls back to environment, Azure CLI active account, Git remote, a global token store or MCP's last active workspace.
- Preserve portable policy and explicit repo defaults. Apply central user defaults only to user-owned or unspecified permitted settings; attach provenance to each effective value. Existing explicit-value preservation remains part of the contract. Reject a central setting that attempts to change the declared endpoint or weaken shared policy.
- For normal self/ownership behavior, use the actually verified bound principal and its canonical ADO identity metadata. A global display-name preference is not an authorizer, claim owner, self-query principal or default Assigned To value.
- Admit identity-dependent cached data only when its binding/principal provenance matches the current admitted worktree runtime. Offline data remains labeled cached/stale; an offline inspection never claims a fresh remote permission check.

### Credential acquisition and validation

- AAD providers and access/refresh caches are bound to a registered identity, ADO resource and credential reference. Runtime acquisition never reads a sibling identity or implicitly re-bootstraps from the machine MSAL active account. Import/bootstrap is an explicit enrollment/migration action.
- Validate audience plus expected tenant/principal on admitted AAD cache entries and every newly minted token. A wrong-principal refresh-store entry or access token fails closed with safe expected/observed identifiers. Do not erase another identity's files or repair a mismatch by choosing another account.
- An unusable audience/expired access token may be refreshed only through the selected identity's verified refresh store. Refresh-token rotation retains that principal. Missing identity evidence requires explicit enrollment/repair; it is not a wildcard.
- JWT inspection is consistency checking, not a claim that local decoding verifies a token signature. Enrollment/mint provenance comes from authenticated token issuance; ADO still validates the presented credential. An auth rejection never enables an alternate-account fallback.
- PAT activation must obtain authoritative principal evidence through a narrowly scoped read-only identity-attestation request before normal work-item/Git/process operations. The identity probe itself is the exception to the pre-work-request validation gate. If principal attestation fails, do not admit the binding.
- PAT proof is tied to exact credential material, target authority and expected principal. Credential replacement or a mismatching proof invalidates admission and requires fresh attestation; it cannot reuse a proof merely because the file/reference/alias is unchanged.
- Existing AAD and PAT credential backends remain supported with their user-private access controls. Separate all credential material from registry/config metadata, isolate it by opaque credential references and enforce restrictive platform file permissions for supported file backends. Do not add new service-principal grant flows or a new platform-vault framework to this effort.
- The earlier storage document's OS-credential-store wording must not be reported as an as-built vault: current stores are user-private files. This Spec centralizes references and isolation without claiming a vault already exists. A stronger/new vault backend is a separately approved storage change, not an implicit fallback or an implementation claim here.
- Preserve existing auth login/status/clear behavior where its meaning remains sound, but make its target explicit: named identity for management outside an attachment, or the admitted binding inside an attachment. Clearing access-cache state does not silently unbind, change principal or delete other credentials.
- Legacy auth environment selectors and per-repo raw auth credentials are migration inputs only, not a parallel runtime authority. Provide actionable migration errors and update supported launchers/configuration; do not leave compatibility shims that bypass binding resolution.

### Management operations and caller cutover

The new management surface must support these complete user intents, with human
and structured machine output and discoverable help:

- Register/login an AAD identity with optional explicit tenant selection; show the actual principal before binding it. Re-login of an existing identity must match that identity and preserve its old credential on mismatch.
- Register a PAT using a non-echoing input channel or standard input, attest its actual principal for the target authority, and store only an opaque credential reference in binding metadata. Do not require a secret in argv or checked-in config.
- List and inspect identities, connections, bindings, defaults and effective selection/provenance; inspect an attached worktree's local pin and admitted cache owner.
- Create an explicit endpoint/identity binding, select a binding default, set a binding pin locally or remove that pin. Binding-default changes target one connection; binding-pin changes target one validated checkout.
- Clear the selected identity's access cache or repair its credentials without selecting another principal. Refuse deleting a binding/identity still referenced by a binding default, attachment or unfinished work; require explicit safe unbinding first.
- Preview and apply migration, and deliberately reconnect an affected live attached connection. Expose blockers and native reconciliation actions rather than a force/discard/publish shortcut.

Reuse existing command groups and domain interfaces where they fit. Exact CLI
verb spelling is part of implementation help/schema review, not a second
selection mechanism. All supported callers must cut over:

- Shared CLI/TUI composition must obtain auth and self identity from the resolved binding rather than the old global provider/user slot.
- Every ADO network client, including work items, Git, iterations, profile identity lookup and process-description reads, must receive the resolved binding's provider and operation guard. HTTP connection pooling may remain shared; authentication headers may not become process-wide defaults.
- MCP composition must not choose one process-wide auth method/provider or let later DI registration replace a correctly bound provider. Cache runtimes by the validated attachment plus binding snapshot, not just organization/project.
- MCP routing must identify the attached worktree unambiguously. A connection-only selector is insufficient when eligible attachments/bindings differ; return structured ambiguity, never pick last active. Keep tool semantics otherwise narrow and migrate every affected schema/caller together.
- All MCP tool families, CLI/TUI composition roots and host launch paths must use that same resolution seam. On binding-changed, Herdr surfaces the structured reconnect error and marks the old rendered data as unavailable/stale under its original binding. Only an explicit user reconnect/acknowledgment relaunches the resolved CLI subprocess in the affected pane with a fresh admitted runtime; there is no automatic in-place account swap or auth-selection environment.
- Server throttling is reported against the actual selected principal/authority, with safe original resource/message, Retry-After and correlation evidence where supplied. Honor server pauses within the correct principal/authority budget rather than pausing another identity. Do not add write-retry automation or rotate identities to evade throttling.

### Live lifetime, switch safety and unfinished work

- Maintain a binding-selection/revision domain separate from claim-tuple epochs and reference-profile versions. Existing claim epochs are not an auth invalidation signal. A snapshot captures binding-default or binding-pin selection revision and the principal/method/credential-reference revision. Normal same-principal secret renewal has its own admission-proof/material revision; it does not change identity-selection generation.
- A process-wide registry snapshot is not sufficient. At operation admission, check authoritative current selection and revisions, including fresh repo/attachment intent. If the effective binding changes, the old live connection returns binding-changed/reconnect-required before another operation; it does not replace its provider in place.
- Normal same-principal AAD access/refresh-token renewal, a freshly verified same-principal PAT renewal within the unchanged binding, and label changes do not trigger an identity switch or erase read/durable data. Same-principal repair remains possible with pending work, avoiding an expired-credential deadlock; changed PAT material must be re-attested and all new remote requests use the repaired credential. Cached output stays labeled cached/stale rather than claiming fresh permissions. Selecting a different binding or explicitly changing its method/credential reference is a management transition subject to reconnect, unfinished-work gates and cold-cache admission; it is not routine token renewal. A mismatching principal cannot be repaired into the existing identity.
- Use operation-scoped binding leases and the existing worktree/system CAS conventions. Long-lived hosts do not hold a permanent lease merely because they are open. A transition cannot race an active operation, cache fill or remote write. In-flight outcomes must settle; expired leases do not prove an uncertain remote write absent.
- A binding-default change must inspect every registered unpinned attachment whose effective identity would change. Pinned/unaffected attachments are not reset. Failure to inspect an affected attachment safely is a blocker. Acquire the affected worktree operation gates and bind every eligibility check to its observed revision; do not partially admit clean worktrees while silently abandoning blocked ones.
- Atomicity is logical admission, not a pretend physical transaction spanning system registry, attachment documents and all mirror stores. The native management authority records a recoverable transition intent with original/desired selections, expected revisions and per-worktree preparation/reset progress. It fences affected worktrees while the transition is unfinished, commits the authoritative binding-default selection/admission generation in one system transaction, and uses revision-checked local pin/mirror completion before a new runtime is usable. Interrupted recovery resumes that exact intent or restores its prior admitted selection with correctly stamped read data. No caller may use a partly reset generation, and no model-owned flag or generic daemon coordinates the transition.
- A local pin change/removal uses that attachment's revision and the same eligibility checks. No force mode may skip identity, pending, lease or journal safety.
- Unfinished work includes all staged field changes/notes, unpublished seeds, open publish intents and all unreconciled proposal operations, including older and partially applied digests. Enumerate them through durable native interfaces; do not use only the latest unresolved proposal or just dirty mirror rows.
- Existing active-claim/holder and worktree-fingerprint guards remain mandatory. Rebinding cannot implicitly release/remint a claim or rewrite its holder. An incompatible reserved/active claim must be settled through its ordinary authorized lifecycle before the new identity can take over that worktree.
- Publication, discard and reconciliation are separate authorized actions. Switching never performs any of them automatically and never interprets a preview as consent to publish.
- Historical native journal states remain immutable. Extend native proposal/publish reconciliation with append-only, evidence-backed outcome receipts where needed to distinguish an unresolved operation from a historical attempt whose outcome was established or whose intent was safely superseded by a Verified replacement.
- Such receipts are owned by the native proposal/publish authority, not a binding-manager boolean or a model-authored sidecar. They bind the original digest/op/intent and expected effect to verified readback or a Verified replacement mapping, the observed outcome/revision and the actual authorizer/rationale. An uncertain operation with an active lease or missing/mismatching evidence stays blocked.
- Never rewrite Failed/Indeterminate rows as Verified, invent Cancelled/withdrawn lifecycle states, remove original files, or treat a newer successful proposal as reconciliation of unrelated older work. A never-applied Planned/Confirmed proposal may be explicitly retired only after native evidence proves no operation ran and records that deliberate retirement without pretending it succeeded.
- Native apply/resume admission must consume retirement/supersession receipts, not just the binding-switch guard. Receipt creation and the native operation admission CAS share the durable execution fence: either the operation wins admission and retirement refuses, or retirement wins and that digest/operation cannot later transition into Applying. Re-preview, re-import, stale saved authorization and a still-current expected ADO revision cannot bypass the fence. Original journal states/files remain unchanged, and native diagnostics report deliberate retirement separately from successful execution.
- Capture the originating attachment, binding/principal requirements and relevant selection revision as native authorization/journal metadata, not additional Plan JSON input fields. Apply/resume must validate that context against the admitted runtime before execution. A changed or unknown legacy origin cannot silently acquire the current actor; resolve it through explicit native evidence/retirement or author a fresh immutable proposal with fresh operation identities and current preview/authorization.
- The binding guard consumes these native receipts plus the durable ledgers. It must permit a settled historical attempt while continuing to refuse genuine unknown/in-flight work. This receipt/read-model capability is a prerequisite to safe rebind, not an optional follow-up.

### Read-cache transition and migration

- On a permitted identity transition, invalidate the disposable identity-dependent mirror, including work-item values/relationships, self-derived views and authorization-sensitive metadata. Do not retain a second per-binding read cache in that checkout. Refill through the new bound runtime; an unreadable active item is reported unavailable, never supplied from the former identity's cache.
- Preserve credentials, portable config, attachment/policy, primary-scope/claim history and all durable pending/publish/journal data. Existing mirror-versus-durable separation is the foundation; do not delete the attachment tree or rebuild the durable store to achieve a cold cache.
- Store an admitted binding/principal/read-cache generation marker so a crash or stale provider cannot return/write old-identity values as new-identity data. If a transition is interrupted, readers fail closed until the transaction is completed or restored. A partially reset cache is not silently treated as ready.
- Migration is versioned, previewable and idempotent, using additive durable/system migrations and existing attachment CAS/fingerprint checks. Unsupported versions, ambiguous mappings or legacy work that cannot be preserved fail with an actionable reason before destructive changes.
- Import existing credential material without selecting it for unrelated connections. Validate actual principal evidence; an old global token's ADO audience, cached display name or Azure CLI active account does not establish intended connection ownership. Explicitly confirm/setup binding defaults when historical intent is unknown.
- Preserve existing authentication until a verified replacement reference and migration mapping exist. Old artifacts may remain as protected recovery history, but are not an active fallback. Do not silently flush work, weaken policy, alter endpoint references or change the machine's unrelated credentials.
- Update supported CLI/MCP/TUI/Herdr hosts and launchers together. Verified quiescence of pre-cutover hosts/providers on affected attachments is a precondition to migration activation and later effective-binding admission. Inventory supported live hosts and affected store handles/capabilities; an empty new lease ledger, startup schema bump, owner assertion or report-only warning is not proof that an unguarded cached provider is absent. If safe quiescence cannot be established, refuse with owner-directed close/restart guidance; do not kill processes or allow a force bypass.
- Under that quiescence gate, seal/retire the legacy store entry points and use a fenced current mirror entry point/generation so an old binary cannot reopen and warm the newly admitted mirror. Preserve the separate durable store and historical data; do not archive retained per-binding read values as a second active cache. Legacy reopen must fail before supported old-host work-request/store access, while current clients require the admitted capability/generation. A file marker cannot retroactively guard an already-open old provider; closure is required first. This storage fence and the native recoverable transition authority are prerequisites, not optional deployment advice. Registry, attachment, mirror and durable-store version domains remain separate.

## Testing Decisions

The owner approved consumer-level verification, not provider-type/DI-wiring
assertions. The primary seam is real command/tool invocation through the shared
Connection Binding module, using actual temporary registry/attachment/mirror/
durable stores and an identity-aware local ADO transport. The transport exposes
different readable items to distinct principals; it does not echo forwarded
fields as its assertion. Host smoke runs exercise the same seam.

Existing token-provider profile-isolation tests supply low-level injection
precedent; MCP MultiWorkspaceIsolation and ConnectionResolver behavior supply
multi-target precedent. Worktree attachment/system-registry CAS, durable pending,
publish-intent and PlanLifecycle behavior supply recovery/transition precedent.
Reuse their fixtures/adapters where sound. A composition graph building or a
factory returning a particular implementation is not proof of authority.

Keep focused tests only for plausible behavior failures and uncertain
race/recovery edges. Match existing deterministic/isolated conventions. Never
assert source text, wiring, incidental default values or mocked field forwarding.
Existing tests that require last-active/last-login fallback must be removed or
replaced with the new ambiguity behavior, not hidden behind a compatibility alias.

### Required behavioral proof

1. **Original failure class:** a valid-Audience personal token is present while an attached corporate binding is selected. Real CLI read/sync and MCP read refuse the wrong principal before work-item/process/Git HTTP; explicitly correct corporate enrollment succeeds. Show that the red case fails on the unfixed implementation or equivalent pre-fix composition.
2. **Mixed runtime:** one MCP host concurrently reads a corporate-only item and a personal item through two attached worktrees; each receives only its intended principal's data. Repeated and interleaved calls do not reuse another provider, cached result or last-active selector.
3. **Same endpoint, different attachments:** two worktrees with different explicit bindings for the same endpoint cannot be selected by an ambiguous endpoint-only target. Explicit attachment targets work independently.
4. **Principal proof:** wrong/missing tenant/object evidence, malformed/wrong-audience tokens and wrong refresh-store principals have real diagnostic outcomes. AAD refresh and verified same-principal renewal stay within the selected identity without reconnect or losing pending/read state; no sibling/machine cache fallback occurs.
5. **PAT proof:** a wrong-account PAT and a replaced PAT at the same reference cannot reuse old admission evidence. Same display name with a different authoritative principal is rejected. A verified intended PAT can perform the admitted read.
6. **Configuration:** a binding pin beats the binding default; no pin uses the binding default; absent/unknown/wrong-endpoint/ambiguous binding fails. Portable policy cannot be weakened by a central override; explicit values survive saves; status reports the right provenance without secrets.
7. **Unfinished work:** each of edits, notes, seeds, open intents and older unresolved proposal operations blocks identity change. A newer Verified digest does not hide an unrelated older unknown outcome. The attempted switch leaves work visible under its original actor and publishes/discards nothing.
8. **Reconciliation:** native verified replacement/readback receipts release the appropriate historical blocker while preserving original journal states/files. Unproved/incorrectly matched/leased operations remain blocked. Retire a never-applied Confirmed row whose expected ADO revision is still current, then attempt its saved original apply/resume after rebinding: native admission refuses before a remote mutation. Race retirement against Confirmed-to-Applying CAS and prove exactly one winner; re-preview/import cannot revive the retired operation. Retirement is not reported as successful application.
9. **Transition races:** stale binding-default/binding-pin CAS, a live read/cache fill and Applying/Applied/unknown writes cannot cross an identity transition. Crash between central selection commit and attachment/mirror completion; reads remain fail-closed and recovery completes/restores the recorded native management intent without new selection plus old-readable data, partial unsafe admission or loss of durable state.
10. **Live host:** changing effective identity causes an already-open MCP/Herdr connection's next operation to request explicit reconnect, without silent provider replacement. Herdr shows the error and requires explicit user acknowledgment/relaunch of its CLI subprocess. Same-identity token renewal does not require that reconnect. An unaffected binding pin remains unaffected by a binding-default change.
11. **Cold cache:** after a clean permitted switch, old identity-only item values are unavailable until fetched with the new binding; denied items never appear from the former cache. Credentials, attachment/policy and historical journals remain inspectable.
12. **Migration:** representative legacy global AAD/PAT inputs and split layouts import idempotently with verified principal provenance; ambiguous/unsupported/non-preservable cases refuse without data loss or unintended binding. Keep a pre-cutover host/provider and store open: activation/admission refuses until it is explicitly closed and quiescence is verified, then succeeds with durable/history data preserved. Reopening the retired legacy store path cannot warm the admitted current generation or resume supported legacy work HTTP. Stored secrets and logs/config metadata obey existing privacy controls.
13. **Throttling:** a real fixture 429 reports safe resource/retry/bound-principal evidence. A principal-specific pause cannot block another principal. No alternate-account retry is introduced.
14. **Management/bootstrap:** help, version, installation utilities and explicit bootstrap administration work without attachment; normal work-item operations still fail before target HTTP there. Cleanup targets only the chosen identity.

Run a throwaway end-to-end CLI/MCP smoke against the implementation and a live
Herdr Tree/Table/reconnect smoke. With separately authorized real bindings, repeat
read-only corporate and personal-project reads/syncs without altering the global
identity or creating tracker artifacts. Live credentials stay outside fixtures,
logs and source. This Spec's diagnostic measurements are motivation, not a claim
that the unimplemented feature has passed those smoke scenarios.

## Out of Scope

- Connection/work operations independent of repository attachment.
- A global current-account/current-connection fallback or ambient last-login selection.
- Moving portable endpoint/shared policy entirely into hidden machine-local config.
- Concurrent identities operating against the same checkout or multiple per-binding read caches inside it.
- New service-principal grant flows, a new platform-vault/encryption framework or a new daemon/sync scheduler.
- Automatically publishing/discarding work, overriding claim ownership, weakening native authorization/CAS/exact-digest checks, rewriting journal history, or introducing new Plan JSON operation kinds to perform identity setup.
- Retrying writes or rotating credentials/identities to bypass genuine service throttling.
- Implementing before owner review of this proposed Spec, switching the machine's current credentials, changing live host topology, or creating unrelated tracker work.

## Further Notes

The confirmed ownership choices from Grilling #1101 are:

1. Repository attachment remains required for normal connection operations.
2. An endpoint supports multiple explicit identity bindings.
3. Portable endpoint and shared policy remain repository-owned.
4. Use a central binding default with an optional untracked checkout-local binding pin.
5. Refuse identity changes with unpublished edits or unresolved proposals; concurrent identities use separate attached worktrees.
6. Effective identity changes require explicit reconnect; never silently switch live accounts.
7. Support current AAD and PAT methods, not new service-principal grant flows.
8. Invalidate/refill identity-dependent read cache after an allowed switch, preserving credentials, attachment/policy and history.

Epic #1100 owns the effort. Task #736 and Spec #728 establish the existing
repo/worktree/system ownership and durable-work constraints. This Spec extends
central identity/configuration management without reversing the portable endpoint
and shared-policy ownership decision.

The auth-isolation experiment supplies guarded stores and tenant/principal checks,
but its explicit process profile refuses PATs and fixes one identity per process.
Reuse useful adapters, not that selection model or unsupported assumption.

Implementation is complete only when every supported caller has cut over and the
observable proof scenarios pass. A new registry table, a working login command,
separate credential folders or a successfully built DI graph alone is not this
feature. This Spec is ready for decomposition; its ready-for-agent tag does not
substitute for the owner's separate implementation authorization.
