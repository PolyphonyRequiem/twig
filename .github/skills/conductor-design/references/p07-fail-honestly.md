# P7: Fail Honestly, Don't Auto-Approve

Verification agents must report actual state. If verification fails, the workflow
must either retry the failed work or terminate with a clear failure report.

**Never auto-approve after N attempts.** If a verifier cannot confirm that work is
complete after a reasonable number of retries, the workflow should stop and surface
the failure — not force-pass to avoid loops. The number of retries should be generous
(10+) to account for transient failures, but the final answer must be honest.

## Implications

- `pr_finalizer` must not set `verified: true` when PGs have no merged PRs
- Close-out must not tag versions when children are incomplete
- Retry loops should have high bounds but hard stops with failure reporting
