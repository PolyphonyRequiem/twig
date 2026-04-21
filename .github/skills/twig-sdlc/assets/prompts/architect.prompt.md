Create a solution design and implementation plan.
**Work Item:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Type:** {{ intake.output.item_type }}
**Description:** {{ intake.output.epic_description }}
{% if intake.output.existing_issues | length > 0 %}
**Existing child Issues (reuse these — do NOT create duplicates):**
{% for issue in intake.output.existing_issues %}
- #{{ issue.id }}: {{ issue.title }}{% if issue.description is defined and issue.description %} — {{ issue.description }}{% endif %}
{% endfor %}
Incorporate existing Issues into the plan. Define Tasks under each Issue
even when the Issue already exists — Tasks are the unit of implementation.
{% endif %}
{% if review_router is defined and review_router.output and not review_router.output.both_pass %}
**Blocking issues flagged by reviewers** (tech={{ review_router.output.tech_score }}, read={{ review_router.output.read_score }}, blocking={{ review_router.output.blocking_issue_count }}, revision {{ (architect.output.plan_revision_count | default(0)) + 1 }} of 2 max):
{{ review_router.output.combined_feedback }}

Revise the plan at `{{ architect.output.plan_path }}` to address **only these blocking
issues**. Do not make stylistic changes, restructure unaffected sections, or act on
non-blocking suggestions — those are intentionally excluded upstream. Minimal, surgical
edits only. If a blocking issue is based on a misreading of the plan, note that in
`revision_notes` rather than changing the plan.
{% endif %}
{% if plan_approval is defined and plan_approval.output and plan_approval.output.selected == 'revise' %}
**User Revision Request:**
{{ plan_approval.output.feedback | default('No specific feedback provided.') }}
Revise the existing plan at `{{ architect.output.plan_path }}` to address user feedback.
{% endif %}
{% if open_questions_gate is defined and open_questions_gate.output and open_questions_gate.output.selected == 'provide_input' %}
**User Input on Open Questions:**
{{ open_questions_gate.output.feedback | default('No specific input provided.') }}
Incorporate user answers into the design. Resolve addressed questions, update
affected decisions, and re-evaluate remaining open questions.
{% endif %}

---

## Phase 1: Research

Before drafting, perform thorough research:

### Codebase Analysis
- Explore relevant portions of the codebase deeply
- Identify existing components, patterns, abstractions, and conventions
- Understand the current architecture and how data flows
- Map out dependencies between components
- Note any technical debt or constraints that affect the design

### Call-Site Audit
If the change modifies cross-cutting behavior (shared services, base classes,
extension methods, serialization, or interfaces used by multiple callers),
inventory ALL existing call sites in a table: file, method, current usage, impact.
Include this table in the Background section. This prevents missed call sites
from causing bugs during implementation.

---

## Phase 2: Design & Plan Document

Write a `.plan.md` document with the following sections:

### Executive Summary
One paragraph describing the proposal, its motivation, and expected outcome.

### Background
- Current state of the system and relevant architecture
- Context that motivates this design (why now, what changed)
- Prior art or related work in the codebase
- Call-site audit table (if applicable from Phase 1)

### Problem Statement
Clearly define the problem(s) this design addresses. Be specific about
pain points, limitations, or gaps in the current system.

### Goals and Non-Goals
**Goals** — specific, measurable outcomes this design aims to achieve.
**Non-Goals** — explicit exclusions to keep scope focused.

### Requirements
Functional and non-functional requirements.

### Proposed Design
- **Architecture Overview** — high-level component diagram and how pieces fit together
- **Key Components** — each major component with its responsibilities and interfaces
- **Data Flow** — how data moves through the system for key operations
- **Design Decisions** — key decisions made and the rationale behind each

### Alternatives Considered *(optional)*
For each significant design decision, describe alternatives evaluated with
pros/cons and why the chosen approach was selected. Include when the design
involves non-obvious choices between viable approaches. Skip if straightforward.

### Dependencies
- External dependencies (libraries, services, infrastructure)
- Internal dependencies (other components, teams, systems)
- Sequencing constraints (what must happen before this design can proceed)

### Impact Analysis *(optional)*
Components and areas of the codebase affected, backward compatibility,
performance implications, operational impact. Include when the design touches
multiple components or has compatibility/performance implications. Skip for
isolated changes.

### Security Considerations *(optional)*
Authentication, authorization, access control, data protection implications.
Include when the design affects security boundaries, handles sensitive data,
or changes the attack surface. Skip if not applicable.

### Risks and Mitigations *(optional)*
Table with: Risk, Likelihood (Low/Medium/High), Impact (Low/Medium/High), Mitigation.
Include when the design carries meaningful technical, operational, or schedule risks.
Skip for low-risk changes.

### Open Questions
Items requiring further discussion, investigation, or decision from stakeholders.

### Files Affected

#### New Files
| File Path | Purpose |
|-----------|---------|

#### Modified Files
| File Path | Changes |
|-----------|---------|

#### Deleted Files *(optional)*
| File Path | Reason |
|-----------|--------|

### ADO Work Item Structure
- If input is an Epic: define Issues under it, and Tasks under each Issue
- If input is an Issue: define Tasks under it directly
- **Every Issue MUST have Tasks** — break each Issue into 2-6 concrete,
  independently committable Tasks. Each Task specifies file paths,
  change descriptions, and effort estimates. No Issue should be a single
  monolithic work item.
- Acceptance criteria per Issue
- For each Issue, provide:
  - **Goal**: What this Issue achieves
  - **Prerequisites**: Dependencies on other Issues
  - **Tasks**: Table with Task ID, Description, Files, Effort Estimate, and Status (TO DO)
  - **Acceptance Criteria**: Checkboxes for completion

### PR Groups (separate section)
PR groups cluster Tasks/Issues for reviewable PRs — NOT a 1:1 mapping to the ADO
hierarchy. A PR group may contain:
- Tasks from a single Issue
- Tasks spanning multiple Issues
- An entire Issue's Tasks
Size each PR group for reviewability (≤2000 LoC, ≤50 files).
Classify each as **deep** (few files, complex) or **wide** (many files, mechanical).
Define execution order between PR groups using successor links.
**Naming:** Use `PG-1`, `PG-2`, etc. (not `PR-1`) to avoid confusion with GitHub PR numbers.

### References *(optional)*
Links to relevant documentation, prior art, RFCs, or external resources.

---

Be thorough and specific. Ground every claim in evidence from the codebase or research.
Avoid vague or aspirational language — if something is uncertain, call it out as an open question.

## Saving the Document
Save the plan to `docs/projects/<slug>.plan.md`
{% if workflow.input.prompt %}
Derive the plan topic from: {{ workflow.input.prompt }}
{% endif %}

### Incremental Writing (critical for large plans)
Do NOT attempt to write the entire plan in a single tool call. Large writes risk
context exhaustion and session timeouts. Instead, write incrementally:
1. **Create** the file with the header, Executive Summary, and Background sections
2. **Edit/append** the Problem Statement, Goals, and Proposed Design
3. **Edit/append** the ADO Work Item Structure and PR Groups
4. **Edit/append** the remaining sections (Files Affected, Open Questions, References)

Each write should be ≤8KB of content. If a section (e.g., Proposed Design) is very
large, split it across multiple appends. Verify the file exists after the first
create before continuing with edits.

## Revision Notes
{% if review_router is defined or plan_approval is defined or open_questions_gate is defined %}
After revising the document, provide a concise summary of what you changed and why
in your `revision_notes` output. Structure as a bullet list, e.g.:
- Corrected API surface per technical reviewer feedback
- Rewrote Executive Summary for clarity per readability feedback
- Added missing risk mitigation for database migration
This helps reviewers understand what changed.

Set `plan_revision_count` to {{ (architect.output.plan_revision_count | default(0)) + 1 }}
(i.e. previous count plus one). The loop is capped at 2 revisions — after the second
revision, routing will proceed to the human plan_approval gate regardless of remaining
blocking issues.
{% else %}
This is the first draft. Set `revision_notes` to "Initial draft." and
`plan_revision_count` to 0.
{% endif %}

## Open Questions Evaluation
After writing the document, evaluate the Open Questions section:
- If ANY open questions are **Moderate**, **Major**, or **Critical**,
  set `has_blocking_questions` to true and provide a formatted
  `open_questions_summary` listing the blocking questions with severity.
- If all questions are Low or there are none, set `has_blocking_questions`
  to false and `open_questions_summary` to "No blocking open questions."
