# Provider lifecycle qualification

The `ADO shared lifecycle qualification` and `GitHub shared lifecycle
qualification` CI steps exercise production DI, SQLite repositories, provider
adapters, declared-policy loading/evaluation, capability admission and the shared
proposal lifecycle. Only credentials and external work HTTP use synthetic
transports. Every selected test must execute and pass; `tools/verify-trx.py`
rejects missing selectors, skipped cases, incomplete runs and inconsistent
counters. Each command retains the existing external 60-second deadline and
uploads its TRX on Linux, Windows and macOS.

The named `OfflineProviderLifecycle_` cases read an item, author a proposal,
preview it without publication, refuse an incorrect digest, apply the exact
digest, verify independent provider readback and replay without another write.
ADO also reopens its disk store and journal. GitHub proves that provider
observations preserve its independent local identity and field overlay. Both
refuse invalid declared policy. A real GitHub parent-type policy is denied,
permitted, then changed after preview to prove execution rechecks policy before
writing.

The surrounding selected regressions cover native capability shapes, create
fences and unknown outcomes, field and relationship mutation readback, stale
identity/actor/evidence refusal, non-atomic concurrency, lost acknowledgements
and preservation of local edits. These tests are contract evidence, not proof
that a live installation has any particular grant.

## Qualification boundaries

- **Offline provider contract qualification:** required on all three platforms
  for the exact PR head; retained test artifacts are the result, not a sampled
  success line.
- **Live GitHub sandbox runtime qualification:** not run or authorized by this
  work. No live issue creates, edits, relationships or sandbox access occur.
  An earlier single create cannot prove edit or relationship permissions.
- **Broad release-candidate suites:** not run or authorized by this work.
  Routine solution build, focused qualification and offline smoke do not
  constitute a release-candidate verdict. Record every separately authorized
  suite's actual result before declaring release qualification.
- **Protected-store host qualification:** the two real holder/capability cases
  are Windows-only. Non-Windows protected-store qualification is unavailable,
  not passed; this offline provider evidence does not settle the potential
  Linux/macOS handle-inventory portability defect.
- **Compatibility:** the frozen public ADO integer/WIQL interfaces remain native
  compatibility contracts, not provider-neutral protocols. Their removal is
  a separately deferred breaking package migration.

The deliberate broader legacy init test run that exceeded its unchanged
60-second deadline remains a failed attempt. Narrow onboarding or provider
lifecycle success does not retroactively qualify that run.

An initial combined `GitHubNativeRelationshipTests` class run also exceeded its
unchanged 60-second deadline (owned tree stopped, exit 124). The bounded named
qualification groups are not a pass for that aborted full-class run. The first
isolated new policy case correctly failed its fixture precondition because the
synthetic transport returned no issue type; that transport now explicitly
supplies the synthetic type used by the policy case. No production policy or
capability guard was changed.

## Recorded local result

The integrated Windows checkout passed the supported solution build with zero
warnings/errors, then all five strict qualification groups: ADO lifecycle 5,
GitHub core/policy/capabilities 3, authoring 7, authority refusals 8 and
unknown-outcome/concurrency 4 (27 executed cases, none skipped). Each group's
native exit was zero and every selector was reconciled against its TRX. The
required offline smoke reported `TWIG-VERDICT OVERALL: PASSED` in 5.62 seconds.
This result does not replace the exact-head, three-platform CI gate or any
unrun release-candidate/live qualification.
