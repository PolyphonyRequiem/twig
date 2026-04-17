Check the review results:
**Technical Review:** Score {{ technical_reviewer.output.score }}/100
Critical issues ({{ technical_reviewer.output.critical_issues | default([]) | length }}):
{{ technical_reviewer.output.critical_issues | default([]) | tojson }}

Feedback (advisory — do NOT include in combined_feedback):
{{ technical_reviewer.output.feedback | default('') }}

**Readability Review:** Score {{ readability_reviewer.output.score }}/100
Critical issues ({{ readability_reviewer.output.critical_issues | default([]) | length }}):
{{ readability_reviewer.output.critical_issues | default([]) | tojson }}

Feedback (advisory — do NOT include in combined_feedback):
{{ readability_reviewer.output.feedback | default('') }}

**Plan:** {{ architect.output.issue_count }} issues, {{ architect.output.pr_group_count }} PR groups, ~{{ architect.output.total_estimated_loc }} LoC
**Revision count so far:** {{ architect.output.plan_revision_count | default(0) }}

Gating rules (apply in order):
1. `blocking_issue_count` = sum of `len(critical_issues)` across both reviewers.
2. `both_pass` = (`blocking_issue_count == 0`) AND (`tech_score >= 80`) AND (`read_score >= 80`).
   - The score floor of 80 is a safety net for catastrophic drafts; it is NOT the normal gate.
   - The normal gate is `blocking_issue_count == 0`. Reviewers are calibrated so that 92–98 is the expected range for executable plans; do NOT re-derive a score gate from these numbers.
3. A plan is "trivial" if it has ≤2 issues and ≤200 estimated LoC.
4. `skip_plan_review` input: {{ workflow.input.skip_plan_review | default(false) }}.
5. Mirror `architect.output.plan_revision_count` into your `plan_revision_count` output.
6. If `plan_revision_count >= 2`, the loop is capped. Proceed to `plan_approval` (or `work_tree_seeder` if `skip_approval`) regardless of `blocking_issue_count`. Include any unresolved blocking issues in `combined_feedback` so the human gate can decide.

When looping back (i.e. `both_pass` is false AND the cap has not been reached), set `combined_feedback` to ONLY the `critical_issues` arrays from both reviewers, grouped by reviewer, verbatim. Do NOT include non-critical feedback, stylistic suggestions, "areas for enhancement", or the holistic `feedback` fields — those are intentionally advisory and the architect must not chase them.
