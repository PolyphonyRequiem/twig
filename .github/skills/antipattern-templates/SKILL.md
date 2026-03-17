---
name: antipattern-templates
description: Reference templates for authoring anti-pattern documents and violation reports. Use these templates when generating new anti-pattern guidelines or formatting review results.
---

# Anti-Pattern Templates Skill

Reference templates for authoring anti-pattern documents and formatting violation reports used by the Gatekeeper pipeline.

- [`antipattern.template.md`](references/antipattern.template.md) — Template for authoring new anti-pattern documents
- [`antipattern.report.template.md`](references/antipattern.report.template.md) — Template for formatting violation reports

## When to Use

Use this skill when generating new anti-pattern documents via the `Octane.Gatekeeper.Generator` prompt, or when formatting code review violation reports.

## Steps

### 1. Authoring a New Anti-Pattern Document

1. Read [`antipattern.template.md`](references/antipattern.template.md) to understand all required sections and formatting conventions
2. Fill in each section (Scope, Measurable Impact, Detection Instructions, Negative Example, Positive Example) with specific, actionable content
3. Ensure detection instructions use the exact syntax `**It is not a violation**` and `**It is a violation**`, with non-violation cases listed before violation cases
4. Save the completed document as a `.md` file in the guidelines directory

### 2. Formatting a Violation Report

1. Read [`antipattern.report.template.md`](references/antipattern.report.template.md) for the required report structure
2. For each violation, populate the Location, Detection, Suggested Fix, and Risk sections with precise file paths, line numbers, and code snippets
3. Include a Summary table at the top with total violation and affected file counts
