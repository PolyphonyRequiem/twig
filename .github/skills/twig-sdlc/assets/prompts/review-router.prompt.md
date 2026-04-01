Check the review results:
**Technical Review:** Score {{ technical_reviewer.output.score }}/100
{{ technical_reviewer.output.feedback }}
**Readability Review:** Score {{ readability_reviewer.output.score }}/100
{{ readability_reviewer.output.feedback }}
**Plan:** {{ architect.output.issue_count }} issues, {{ architect.output.pr_group_count }} PR groups, ~{{ architect.output.total_estimated_loc }} LoC
Rules:
- Both scores must be ≥ 90 to pass
- A plan is "trivial" if it has ≤2 issues and ≤200 estimated LoC
- skip_plan_review input: {{ workflow.input.skip_plan_review | default(false) }}
Combine both reviewers' feedback into one summary for the architect if looping back.
