---
description: 'Requirements Document content creation standards and requirements.'
applyTo: '**/*.req.md'
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
- Use **standardized cross-reference prefixes** (`FR-`, `NFR-`, `FM-`, `AC-`) when referencing identifiers across sections

## AI-OPTIMIZED IMPLEMENTATION STANDARDS

To ensure compatibility with AI agents and automation systems:

- Use **explicit syntax and semantics**—no implied meaning or interpretation
- Define all **variables, constants, and configuration values** with full context
- Format all content using **structured data**: tables, lists, or key-value blocks
- We will later create a separate PRD and Task List from this requirements document

## TEMPLATE COMPLIANCE

You must follow the mandatory template exactly:

- All **front matter fields** must be present and correctly formatted
- Match all **section headers** exactly (case-sensitive)
- Ensure **each section is populated** with specific, actionable content
- Apply **standardized prefixes** (e.g., `FR-`, `NFR-`) to all identifiers
- Apply **`FM-` prefix** to failure mode identifiers (e.g., `FM-001`)
- Apply **`AC-` prefix** to acceptance criteria identifiers (e.g., `AC-001`)
- Format all content using **structured data formats** such as tables, lists, or key-value blocks.
- Ensure tables contain **all required columns** with no omissions
- Eliminate all **placeholder text** from final deliverables
- Do not modify section headers or structure
- Do not add or remove sections
- Maintain the focus to ONLY functional and non-functional requirements

## MANDATORY TEMPLATE

```markdown
---
goal: [Concise Title Describing the Package Implementation Plan's Goal]
version: [Optional: e.g., 1.0, Date]
date_created: [YYYY-MM-DD]
last_updated: [Optional: YYYY-MM-DD]
owner: [Optional: Team/Individual responsible for this spec]
tags: [Optional: List of relevant tags or categories]
---

# Introduction

Requirements Document for the following initiative: [Original prompt or request that led to this requirements document]

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: Functional requirements use the `FR-` prefix, non-functional requirements use `NFR-`, failure modes use `FM-`, and acceptance criteria use `AC-`. These prefixes enable traceability across sections and into the PRD.

## 1. Terminology

[Define all domain-specific terms, abbreviations, and concepts used in this document.]

| Term | Definition |
|------|------------|
| [Term 1] | [Clear, concise definition] |
| [Term 2] | [Clear, concise definition] |

## 2. Scope

[Clearly delineate what this change includes and what is explicitly deferred.]

### In Scope

- [Capability or deliverable 1]
- [Capability or deliverable 2]

### Out of Scope (deferred)

- [Capability 1] — [rationale for deferral]
- [Capability 2] — [rationale for deferral]

## 3. Functional Requirements

[List all functional requirements that the change or feature must satisfy. Each requirement should be clear, specific, and testable.]

- **FR-001**: [Feature Name]
    - **Description**: [Clear, specific requirement]
    - **Acceptance Criteria**:
        - [Specific testable criteria 1]
        - [Specific testable criteria 2]
    - **Priority**: [High/Medium/Low]
    - **Dependencies**: [Related requirements]

## 4. Non-Functional Requirements

[List all non-functional requirements that are unique to this change or feature. These may include performance, security, usability, etc.]

- **NFR-001**: [Requirement Category]
    - **Metric:** [Measurable target - e.g., "Response time < 200ms"]
    - **Rationale:** [Business justification]
    - **Testing Approach:** [How to verify]

## 5. Failure Modes and Recovery

[Identify how the system should behave when things go wrong — API errors, invalid input, timeouts, resource exhaustion.]

| ID | Failure | Detection | Recovery |
|----|---------|-----------|----------|
| FM-001 | [What can go wrong] | [How it is detected] | [How the system recovers or degrades gracefully] |
| FM-002 | [What can go wrong] | [How it is detected] | [How the system recovers or degrades gracefully] |

## 6. Acceptance Criteria

[Define testable criteria that prove each requirement is satisfied. Every FR and critical NFR should trace to at least one AC.]

| ID | Criterion | Verification | Traces To |
|----|-----------|--------------|-----------|
| AC-001 | [Testable condition] | [Automated test / Manual / Schema validation] | FR-001 |
| AC-002 | [Testable condition] | [Automated test / Manual / Schema validation] | NFR-001, FM-001 |
```
