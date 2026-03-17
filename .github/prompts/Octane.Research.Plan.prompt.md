---
agent: Researcher
description: Phase 3 - Create stakeholder alignment plan and phased implementation plan.
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- Design options and recommendation from Phase 2
- Selected option (if different from recommendation)

If you do not have design options, direct the user to run `/design` first.

## PRIMARY DIRECTIVE

Create a comprehensive implementation plan that includes stakeholder alignment, phased delivery, testing strategy, and rollout plan. This is the final phase before actual implementation begins.

## WORKFLOW STEPS

Present the following steps as **trackable todos**:

### Step 1: Create Stakeholder Alignment Plan

Based on the synthesis and design, identify:

- [ ] Which teams need to be consulted before implementation?
- [ ] What questions need to be answered by each team?
- [ ] What decisions need to be made collaboratively?
- [ ] Draft agenda for alignment meetings
- [ ] Key questions to ask each stakeholder

### Step 2: Define Pre-Implementation Checklist

- [ ] List all required alignment meetings with status
- [ ] Identify design doc requirements
- [ ] Note all prerequisite decisions that must be made
- [ ] Check for any blocking dependencies

### Step 3: Create Phased Implementation Plan

Break work into reviewable, deployable chunks:

- [ ] Phase 1: [Foundation/Setup]
- [ ] Phase 2: [Core Implementation]
- [ ] Phase 3: [Integration/Polish]
- [ ] Each phase should be independently testable

### Step 4: Define Testing Strategy

- [ ] Unit tests to add
- [ ] Integration tests to add
- [ ] How to validate with stakeholders
- [ ] Performance testing needs
- [ ] Edge cases to cover

### Step 5: Create Rollout Plan

- [ ] Deployment approach (feature flags, canary, staged?)
- [ ] Monitoring and alerting to add
- [ ] Rollback plan if issues occur
- [ ] Communication plan for go-live

### Step 6: Define Success Metrics

- [ ] How will we know this works?
- [ ] What telemetry to add?
- [ ] What to monitor after deployment?
- [ ] Definition of done

## OUTPUT FORMAT

Structure your implementation plan using the template:

```markdown
# Implementation Plan: Work Item [ID] - [Title]

## Selected Approach
**[Option Name]**: [One sentence summary]

---

## 1. Stakeholder Alignment Plan

### Required Alignments

| Team/Person | Topic | Questions to Ask | Status |
|-------------|-------|------------------|--------|
| [name] | [topic] | [questions] | ⬜ Not Started |

### Alignment Meeting Agenda

**Meeting 1: [Team Name]**
- Duration: [time]
- Attendees: [names]
- Agenda:
  1. [topic]
  2. [topic]
- Decision needed: [what decision]

### Communication Plan

| Stakeholder | Message | Timing |
|-------------|---------|--------|
| [name] | [what to tell them] | [when] |

---

## 2. Pre-Implementation Checklist

### Must Complete Before Coding
- [ ] Alignment with [team] completed
- [ ] Design doc reviewed by [reviewers]
- [ ] [Prerequisite decision] made
- [ ] [Dependency] available

### Open Questions
| Question | Owner | Due Date |
|----------|-------|----------|
| [question] | [who will answer] | [when] |

---

## 3. Phased Implementation

### Phase 1: [Name] 
**Goal**: [What this phase accomplishes]
**Duration**: [Estimated time]
**Deliverable**: [What's delivered]

Tasks:
- [ ] [Task 1]
- [ ] [Task 2]

Files to modify:
- `[file path]`: [what changes]

PR Scope: [What's in this PR]

---

### Phase 2: [Name]
[Same structure]

---

### Phase 3: [Name]
[Same structure]

---

## 4. Testing Strategy

### Unit Tests
| Test | File | What It Validates |
|------|------|-------------------|
| [test name] | [file] | [what it tests] |

### Integration Tests
| Test | Scope | What It Validates |
|------|-------|-------------------|
| [test name] | [components] | [what it tests] |

### Manual Validation
- [ ] [Validation step with stakeholder]

### Performance Testing
- [ ] [Performance test if needed]

---

## 5. Rollout Plan

### Deployment Strategy
- [ ] Feature flag: [flag name]
- [ ] Canary: [canary plan]
- [ ] Staged rollout: [stages]

### Monitoring & Alerting
| Metric | Threshold | Alert |
|--------|-----------|-------|
| [metric] | [threshold] | [who gets alerted] |

### Rollback Plan
**Trigger**: [What would trigger rollback]
**Steps**:
1. [Rollback step]
2. [Rollback step]

**Time to rollback**: [estimated time]

---

## 6. Success Metrics

### Definition of Done
- [ ] All phases implemented and merged
- [ ] Tests passing
- [ ] Stakeholder validation complete
- [ ] Monitoring in place
- [ ] Documentation updated

### Success Criteria
| Metric | Current | Target | Measurement |
|--------|---------|--------|-------------|
| [metric] | [current value] | [target] | [how to measure] |

### Post-Deployment Validation
- [ ] [Validation step]
- [ ] [Validation step]

---

## Next Steps

1. [ ] Complete stakeholder alignments
2. [ ] Get design doc approved
3. [ ] Begin Phase 1 implementation
```

## FINAL OUTPUT

After completing the implementation plan, inform the user:

> Implementation plan complete! 
>
> **Before starting to code:**
> 1. Complete the stakeholder alignments marked in the plan
> 2. Get any required design doc approvals
> 3. Resolve open questions
>
> **When ready to implement:**
> Use the standard Planner/Coder workflow or proceed with manual implementation following the phased plan.
>
> Good luck! 🚀
