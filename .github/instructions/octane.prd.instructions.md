---
description: 'Product Requirement Document (PRD) content creation standards and requirements.'
applyTo: '**/*.prd.md'
---

## CORE REQUIREMENTS

- Use **deterministic, unambiguous language** suitable for automated interpretation
- Ensure all content is **self-contained**, with no reliance on external context or assumptions.
- Include **complete task context** within each description—avoid cross-referencing other sections.
- Structure all outputs for dual consumption: **human-readable and machine-readable**, using consistent formatting.
- Reference **exact file paths, line numbers, and code identifiers** wherever applicable.
- Organize plans into **atomic, discrete phases** with clearly defined tasks
- **Version updates and changelog entries** must only be made when explicitly instructed
- Use **RFC 2119 keywords** (MUST, SHOULD, MAY, etc.) consistently to convey requirement levels
- Use **standardized cross-reference prefixes** (`FR-`, `NFR-`, `FM-`, `AC-`, `RD-`) when referencing identifiers across sections and documents

## AI-OPTIMIZED IMPLEMENTATION STANDARDS

To ensure compatibility with AI agents and automation systems:

- Use **explicit syntax and semantics**—no implied meaning or interpretation
- Define all **variables, constants, and configuration values** with full context
- Format all content using **structured data**: tables, lists, or key-value blocks
- Include **validation criteria** that can be programmatically verified

## TEMPLATE COMPLIANCE

You must follow the mandatory template exactly:

- All **front matter fields** must be present and correctly formatted
- Match all **section headers** exactly (case-sensitive)
- Ensure **each section is populated** with specific, actionable content
- Apply **standardized prefixes** (e.g., `REQ-`, `ITEM-`, `TASK-`) to all identifiers
- Apply **`RD-` prefix** to resolved decision identifiers (e.g., `RD-001`)
- Apply **`FM-` prefix** to failure mode identifiers if not already covered in the `.req.md`
- Format all content using **structured data formats** such as tables, lists, or key-value blocks.
- Ensure tables contain **all required columns** with no omissions
- Eliminate all **placeholder text** from final deliverables

## MANDATORY TEMPLATE

```markdown
---
goal: [Concise Title Describing the Package Implementation Plan's Goal]
version: [Optional: e.g., 1.0, Date]
date_created: [YYYY-MM-DD]
last_updated: [Optional: YYYY-MM-DD]
owner: [Optional: Team/Individual responsible for this spec]
tags: [Optional: List of relevant tags or categories, e.g., `feature`, `upgrade`, `chore`, `architecture`, `migration`, `bug` etc]
---

# Introduction

[Brief description of the solution, the problem it solves, and the expected impact. Keep it concise and accessible to all stakeholders.]

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: This document uses standardized prefixes for traceability — `FR-` (functional requirements), `NFR-` (non-functional requirements), `FM-` (failure modes), `AC-` (acceptance criteria), and `RD-` (resolved decisions). When a `.req.md` exists, identifiers from that document are referenced directly.

## 1. Goals and Non-Goals

[Clearly define the goals of the implementation plan, what it aims to achieve, and what is explicitly out of scope. This helps set expectations and boundaries for the project.]

- **Goal 1**: Description of goal 1
- **Goal 2**: Description of goal 2
- **Non-Goal 1**: Description of what is not included in the scope
- **Non-Goal 2**: Description of what is not included in the scope

### In Scope

- [Capability or deliverable 1]
- [Capability or deliverable 2]

### Out of Scope (deferred)

- [Capability 1] — [rationale for deferral]
- [Capability 2] — [rationale for deferral]

## 2. Terminology

[Define all domain-specific terms, abbreviations, and concepts used in this document. If a `.req.md` exists for this initiative, reference its Terminology section and add only PRD-specific terms here.]

| Term | Definition |
|------|------------|
| [Term 1] | [Clear, concise definition] |
| [Term 2] | [Clear, concise definition] |

## 3. Solution Architecture

[A high-level overview of the solution architecture, including key components, technologies, and how they interact. This should provide a clear understanding of the system design. If it is helpful to include diagrams, please do so in mermaid format.]

## 4. Requirements

**Summary**: [Provide a high-level summary of the requirements and constraints that affect the plan.]

[Explicitly list all requirements & constraints that affect the plan and constrain how it is implemented. Provide a high-level summary then use bullet points or tables for clarity.]
**Items**:
- **REQ-001**: Requirement 1
- **SEC-001**: Security Requirement 1
- **CON-001**: Constraint 1
- **GUD-001**: Guideline 1
- **PAT-001**: Pattern to follow 1
- **[3 LETTERS]-001**: Other Requirement 1

## 5. Risk Classification

**Risk**: [Classify the overall risk level of the plan as 🟢 LOW , 🟡 MEDIUM , or 🔴 HIGH RISK]

**Summary**: [Provide a brief summary of the risk classification, including any specific concerns or considerations that led to this classification.]

[List specific risks or assumptions related to the implementation of the plan]
**Items**:
- **RISK-001**: Risk 1
- **ASSUMPTION-001**: Assumption 1

## 6. Dependencies

**Summary**: [Provide a summary of the dependencies.]

[List any dependencies that need to be addressed, such as libraries, frameworks, or other components that the plan relies on.]
**Items**:
- **DEP-001**: Dependency 1
- **DEP-002**: Dependency 2

## 7. Quality & Testing

**Summary**: [Provide a summary of the testing strategy and quality assurance measures.]

[List the tests that need to be implemented to verify the feature or refactoring task.]
**Items**:
- **TEST-001**: Description of test 1
- **TEST-002**: Description of test 2

### Acceptance Criteria

[Define testable criteria that prove each requirement is satisfied. Every functional requirement and critical non-functional requirement should trace to at least one acceptance criterion.]

| ID | Criterion | Verification | Traces To |
|----|-----------|--------------|-----------|
| AC-001 | [Testable condition] | [Automated test / Manual / Schema validation] | REQ-001, FM-001 |
| AC-002 | [Testable condition] | [Automated test / Manual / Schema validation] | FR-001 |

## 8. Security Considerations

[Identify security-relevant aspects of the implementation. Not every feature has security implications — if none exist, state "No security considerations identified" and briefly explain why.]

- **Data handling**: [What data is sensitive, how it's protected]
- **Input validation**: [How untrusted input is handled]
- **Access control**: [Permission model, least-privilege constraints]
- **Secrets**: [How secrets/credentials are managed]

## 9. Deployment & Rollback

[Describe the deployment strategy, including how the plan will be rolled out, monitored, and rolled back if necessary. Include any specific steps or considerations for deployment]

## 10. Resolved Decisions

[Document key design decisions and their rationale. This section serves as institutional memory — when someone asks "why did we do X?", the answer is here.]

| ID | Decision | Rationale |
|----|----------|-----------|
| RD-001 | [What was decided] | [Why this was chosen] |
| RD-002 | [What was decided] | [Why this was chosen] |

## 11. Alternatives Considered

[For each significant design choice, document what alternatives were evaluated and why they were rejected. This prevents revisiting settled decisions.]

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| [Option A] | [Benefits] | [Drawbacks] | Rejected — [reason] |
| [Option B] | [Benefits] | [Drawbacks] | Selected — [reason] |

## 12. Files

[List the key files that will be affected by the feature or refactoring task.]

- **FILE-001**: Description of file 1
- **FILE-002**: Description of file 2

## 13. Implementation Plan

[Break down the implementation into discrete, atomic epics. Each epic must be independently processable by AI agents or humans without cross-phase dependencies unless explicitly declared. An epic shouldn't be too large (e.g., spanning more than 7 unique files or taking more than 1-2 weeks to complete)]

- EPIC-001: [Describe the goal of this phase, e.g., "Implement feature X", "Refactor module Y", etc.]

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-001 | Detailed description of workitem 1 | Done | ./src/sample_file.py, ./src/another_file.py |
| ITEM-002 | Detailed description of workitem 2 | In Progress | ./src/another_file.py |
| ITEM-003 | Detailed description of workitem 3 | Not Started | ./src/yet_another_file.py |

- EPIC-002: [Describe the goal of this phase, e.g., "Implement feature X", "Refactor module Y", etc.]

| Task | Description | Status | Relevant Files |
|------|-------------|--------|----------------|
| ITEM-004 | Detailed description of workitem 4 | Not Started | ./src/sample_file.py |
| ITEM-005 | Detailed description of workitem 5 | Not Started | ./src/another_file.py |

## 14. Change Log

- [Optional: A log of changes made to the plan, including dates and descriptions of updates. This helps track the evolution of the plan over time.]
```
