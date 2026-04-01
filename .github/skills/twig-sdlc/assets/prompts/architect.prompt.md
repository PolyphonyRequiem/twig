Create a solution design and implementation plan.
**Work Item:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Type:** {{ intake.output.item_type }}
**Description:** {{ intake.output.epic_description }}
{% if intake.output.existing_issues | length > 0 %}
**Existing child Issues (reuse these — do NOT create duplicates):**
{% for issue in intake.output.existing_issues %}
- #{{ issue.id }}: {{ issue.title }} — {{ issue.description }}
{% endfor %}
Incorporate existing Issues into the plan. Define Tasks under each Issue
even when the Issue already exists — Tasks are the unit of implementation.
{% endif %}
{% if review_router is defined and review_router.output and not review_router.output.both_pass %}
**Review Feedback (tech={{ review_router.output.tech_score }}, read={{ review_router.output.read_score }}):**
{{ review_router.output.combined_feedback }}
Revise the existing plan at `{{ architect.output.plan_path }}` to address this feedback.
{% endif %}
{% if plan_approval is defined and plan_approval.output and plan_approval.output.selected == 'revise' %}
**User Revision Request:**
{{ plan_approval.output.feedback }}
Revise the existing plan at `{{ architect.output.plan_path }}` to address user feedback.
{% endif %}
{% if open_questions_gate is defined and open_questions_gate.output and open_questions_gate.output.selected == 'provide_input' %}
**User Input on Open Questions:**
{{ open_questions_gate.output.feedback }}
Incorporate user answers into the design. Resolve addressed questions, update
affected decisions, and re-evaluate remaining open questions.
{% endif %}
## Instructions
1. **Research the codebase** — explore relevant files, understand patterns and conventions
2. **Call-site audit** — if the change modifies cross-cutting behavior (shared services,
   base classes, extension methods, serialization, or interfaces used by multiple callers),
   inventory ALL existing call sites in a table: file, method, current usage, impact.
   Include this table in the Background section of the plan. This prevents missed call
   sites from causing bugs during implementation.
3. **Write a .plan.md document** with:
   - Executive summary
   - Background and current architecture
   - Design decisions with trade-offs
   - **ADO Work Item Structure:**
     - If input is an Epic: define Issues under it, and Tasks under each Issue
     - If input is an Issue: define Tasks under it directly
     - **Every Issue MUST have Tasks** — break each Issue into 2-6 concrete,
       independently committable Tasks. Each Task specifies file paths,
       change descriptions, and effort estimates. No Issue should be a single
       monolithic work item.
     - Acceptance criteria per Issue
   - **PR Groups (separate section):**
     PR groups cluster Tasks/Issues for reviewable PRs. A PR group may contain:
     - Tasks from a single Issue
     - Tasks spanning multiple Issues
     - An entire Issue's Tasks
     Size each PR group for reviewability (≤2000 LoC, ≤50 files).
     Classify each as **deep** (few files, complex) or **wide** (many files, mechanical).
     Define execution order between PR groups using successor links.
   - Risk assessment
4. **Save the plan** to `docs/projects/<slug>.plan.md`
{% if workflow.input.prompt %}
Derive the plan topic from: {{ workflow.input.prompt }}
{% endif %}
## Open Questions Evaluation
After writing the document, evaluate the Open Questions section:
- If ANY open questions are **Moderate**, **Major**, or **Critical**,
  set `has_blocking_questions` to true and provide a formatted
  `open_questions_summary` listing the blocking questions with severity.
- If all questions are Low or there are none, set `has_blocking_questions`
  to false and `open_questions_summary` to "No blocking open questions."
