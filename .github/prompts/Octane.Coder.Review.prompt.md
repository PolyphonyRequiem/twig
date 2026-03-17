---
agent: Coder
description: Review implementation changes against PRD requirements and generate a comprehensive validation report
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo', 'code-search/*']
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `PRD` (string, required): A link to a Product Requirements Document file (e.g. `.prd.md`) that contains the requirements and implementation plan to validate against.
- `Scope` (string, required): The scope of changes to review. This can be:
  - A commit hash or range (e.g., `abc123` or `abc123..def456`)
  - A branch name (e.g., `feature/embedded-artifacts`)
  - A list of specific files that were modified
  - The string "workspace" to review all current changes in the workspace

If you do not have a `PRD` or `Scope`, you cannot proceed. You must stop and ask for these inputs. Do not provide an example request. Just specify that the inputs are required.

## PRIMARY DIRECTIVE

Conduct a comprehensive review of the implementation changes specified in `${input:Scope}` against ALL requirements, goals, and specifications defined in the `${input:PRD}` document. Generate a detailed validation report that:
- **Verifies complete implementation** of all requirements and tasks
- **Identifies gaps, deviations, or missing implementations**
- **Validates quality standards** including tests, documentation, and best practices
- **Provides actionable recommendations** for addressing any findings

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Load and Parse PRD**
   - Read the complete PRD document from `${input:PRD}`
   - Extract all requirements (REQ-, SEC-, CON-, GUD-, PAT- prefixed items)
   - Extract all EPICs and their associated tasks (ITEM- prefixed)
   - Identify success criteria, constraints, and quality standards
   - Note risk classifications and mitigation strategies

2. **Analyze Scope of Changes**
   - Identify all files modified within `${input:Scope}`
   - If scope is a branch/PR: get diff against base branch
   - If scope is commits: analyze all changes in commit range
   - If scope is "workspace": review all uncommitted changes
   - Generate a comprehensive list of all modified files and their change types (added/modified/deleted)

3. **Deep Implementation Review**
   - Use the #runSubagent tool to invoke a sub-agent that will:
     - Use the `code-search/*` tools to perform deep code analysis on all changed files
     - Map each change to the corresponding PRD requirements, EPICs, and tasks
     - Create a traceability matrix linking PRD items to actual changes
     - Verify each implemented feature matches PRD specifications exactly
     - Check that removed/deleted components listed in PRD are actually removed
     - Validate that architectural decisions align with the Solution Architecture section
     - Confirm file modifications match the Files section (FILE- items)
     - Verify no unintended side effects or breaking changes
     - Identify any changes that don't map to PRD requirements (scope creep)
     - Respond with a structured JSON or markdown summary of all findings

4. **Requirements & Quality Validation**
   - Use the #runSubagent tool to invoke a sub-agent that will:
     - Validate each requirement category against the implementation:
       - **Functional Requirements (REQ-)**: Verify feature implementation completeness
       - **Security Requirements (SEC-)**: Validate security controls are in place
       - **Constraints (CON-)**: Check performance, size, compatibility limits
       - **Guidelines (GUD-)**: Verify adherence to best practices
       - **Patterns (PAT-)**: Confirm design patterns are correctly applied
     - Verify all tests specified in Quality & Testing section are implemented
     - Check test coverage meets requirements
     - Validate documentation updates match Documentation section
     - Confirm build/deployment changes align with Deployment section
     - Review error handling and edge cases
     - Respond with a structured compliance report for each requirement

5. **Generate Validation Report**
   - Synthesize findings from sub-agent reviews into a comprehensive `.review.md` report
   - Perform gap analysis: identify all PRD requirements NOT met by the implementation
   - List all EPIC tasks that are incomplete or missing
   - Assess risk of identified issues
   - Provide prioritized recommendations for remediation
   - Calculate metrics on implementation completeness
   - Save the report following the file naming convention

## REVIEW CRITERIA

Evaluate implementation against these standards:

### Completeness
- All EPICs are fully implemented
- All tasks (ITEM-) are completed as specified
- All requirements (REQ-, SEC-, CON-, etc.) are satisfied
- All specified files are modified/created/deleted as planned

### Correctness
- Implementation matches PRD specifications exactly
- No logic errors or bugs introduced
- Proper error handling implemented
- Edge cases addressed

### Quality
- Code follows specified patterns and guidelines
- Tests provide adequate coverage
- Documentation is complete and accurate
- Performance meets specified constraints

### Compliance
- Security requirements are met
- Backward compatibility maintained (if required)
- Platform compatibility verified
- Deployment strategy followed

## REVIEW BEST PRACTICES

- Be **precise and specific** — reference exact file names, line numbers, and code elements
- Provide **actionable feedback** — don't just identify problems, suggest solutions
- Use **objective metrics** — quantify completeness, coverage, and compliance
- Include **positive findings** — acknowledge what was done well
- Maintain **traceability** — link every finding back to specific PRD requirements
- Be **exhaustive** — review EVERY requirement, don't skip any

## FILE NAMING CONVENTION

- Save the review report in the same directory as the PRD
- Use the naming pattern: `[prd-name].review.md`
- Example: If PRD is `embedded-artifacts.prd.md`, save as `embedded-artifacts.review.md`

## MANDATORY TEMPLATE

```markdown
---
prd: [Path to PRD document being validated]
scope: [Description of changes reviewed]
date_reviewed: [YYYY-MM-DD]
reviewer: GitHub Copilot
compliance_status: [COMPLIANT | PARTIALLY_COMPLIANT | NON_COMPLIANT]
completion_percentage: [0-100%]
---

# PRD Implementation Review Report

## Executive Summary

[High-level summary of review findings, overall compliance status, and critical issues]

## Scope of Review

**PRD Document**: `${input:PRD}`
**Changes Reviewed**: `${input:Scope}`
**Total Files Modified**: [count]
**Review Date**: [date]

## Requirements Compliance

### Functional Requirements

| Requirement | Status | Implementation | Notes |
|------------|--------|---------------|-------|
| REQ-001 | ✅ PASS / ❌ FAIL / ⚠️ PARTIAL | [File:line where implemented] | [Any deviations or issues] |

### Security Requirements

[Similar table for SEC- items]

### Constraints & Guidelines

[Similar table for CON-, GUD-, PAT- items]

## EPIC Implementation Status

### EPIC-001: [Name]

| Task | Status | Completion | Findings |
|------|--------|------------|----------|
| ITEM-001 | ✅ COMPLETE / ❌ MISSING / ⚠️ PARTIAL | [Details] | [Issues found] |

**EPIC Completion**: [X/Y tasks complete - Z%]

[Repeat for all EPICs]

## Gap Analysis

### Critical Gaps
1. **[Gap Title]**: [Description of missing implementation]
   - PRD Reference: [EPIC/ITEM/REQ number]
   - Impact: [HIGH/MEDIUM/LOW]
   - Recommendation: [Specific action to address]

### Minor Deviations
1. **[Deviation Title]**: [Description of deviation from PRD]
   - Expected: [What PRD specified]
   - Actual: [What was implemented]
   - Recommendation: [How to align]

## Quality Assessment

### Test Coverage
- **Required Tests**: [count from PRD]
- **Implemented Tests**: [count found]
- **Coverage Gap**: [missing test descriptions]

### Documentation
- **Required Updates**: [list from PRD]
- **Completed Updates**: [list of actual updates]
- **Missing Documentation**: [list gaps]

### Performance & Constraints
[Actual vs Required for each constraint]

## Risk Assessment

| Risk | Status | Mitigation | Notes |
|------|--------|------------|-------|
| RISK-001 | ✅ MITIGATED / ⚠️ PARTIAL / ❌ UNADDRESSED | [Implementation details] | [Observations] |

## Recommendations

### Priority 1 - Critical (Must Fix)
1. [Specific action with file/line reference]

### Priority 2 - Important (Should Fix)
1. [Specific action with file/line reference]

### Priority 3 - Minor (Nice to Have)
1. [Specific action with file/line reference]

## Metrics Summary

- **Total Requirements**: [count]
- **Requirements Met**: [count] ([percentage]%)
- **Total Tasks**: [count]
- **Tasks Completed**: [count] ([percentage]%)
- **Files Expected to Modify**: [count]
- **Files Actually Modified**: [count]
- **Test Coverage**: [percentage]%
- **Documentation Completeness**: [percentage]%

## Conclusion

[Summary statement on whether the implementation meets PRD requirements and is ready for deployment, or what must be addressed before approval]

## Appendix

### Files Reviewed
[List all files examined during review]

### Tools Used
- Code search patterns: [list key searches performed]
- Validation methods: [describe verification approaches]
```

## NEXT STEPS

After completing the review, present the following next steps to the user based on the review findings:

**Review complete. Here are your next steps:**

**If gaps or issues were identified:**
1. **Address the gaps** - Implement missing requirements:
   ```
   /Octane.Coder.Implement <path-to-your-prd.md> <EPIC-with-gaps>
   ```
2. **Re-run the review** after fixes are applied:
   ```
   /Octane.Coder.Review <path-to-your-prd.md> <new-commit-range-stagedOrUnstagedFiles>
   ```

**If all requirements are met:**
1. **Merge your changes** - The implementation is complete and validated
2. **Start the next feature** - Begin a new REQ File:
   ```
   /Octane.Planner.Requirements <new-feature-description>
   ```
   Begin a new PRD File:
   ```
   /Octane.Planner.Plan <new-feature-description>
   ```
