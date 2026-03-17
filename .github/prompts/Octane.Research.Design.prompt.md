---
agent: Researcher
description: Phase 2 - Generate multiple design options with tradeoffs and recommend an approach.
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- Synthesis from Phase 1

If you do not have synthesis findings, direct the user to run `/synthesize` first.

## PRIMARY DIRECTIVE

Generate **multiple design options** (minimum 2-3) for solving the problem. Each option must include tradeoffs, impact analysis, and alignment requirements. Then provide a recommendation with clear justification.

## WORKFLOW STEPS

Present the following steps as **trackable todos**:

### Step 1: Identify Solution Approaches

Based on the synthesis, brainstorm different ways to solve the problem:

- [ ] Option 1: Most straightforward approach
- [ ] Option 2: Alternative approach (different tradeoffs)
- [ ] Option 3: More comprehensive approach (if applicable)

Consider:
- Where in the code path should the change live?
- Can this be done incrementally or does it require a big bang?
- Are there feature flag opportunities?
- What's the minimal viable change vs the ideal change?

### Step 2: Analyze Each Option

For each option, evaluate:

- [ ] Technical complexity
- [ ] Risk level
- [ ] Impact on existing contracts
- [ ] Alignment requirements
- [ ] Time to implement
- [ ] Reversibility

### Step 3: Create Comparison Matrix

- [ ] Build a side-by-side comparison of all options
- [ ] Highlight key differentiators
- [ ] Note which option best fits different priorities

### Step 4: Make Recommendation

- [ ] Select the recommended option
- [ ] Justify why it's the best choice given constraints
- [ ] Note any conditions that would change the recommendation

## OUTPUT FORMAT

Structure your design options using the template:

```markdown
# Design Options: Work Item [ID] - [Title]

## Problem Recap
[One paragraph summary of what we're solving]

---

## Option 1: [Name]

### Where Change Lives
[Specific files/components that would be modified]

### Approach
[Detailed description of the approach]

### Implementation Sketch
```
[Pseudo-code or high-level algorithm]
```

### Pros
- ✅ [advantage]
- ✅ [advantage]

### Cons
- ❌ [disadvantage]
- ❌ [disadvantage]

### Impact on Existing Contracts
- [Contract]: [Breaking/Non-breaking] - [details]

### Alignment Required
- [ ] [Team/Person]: [What needs to be aligned]

### Estimated Effort
[T-shirt size or days, with justification]

### Rollout Risk
[Low/Medium/High] - [why]

---

## Option 2: [Name]

[Same structure as Option 1]

---

## Option 3: [Name] (if applicable)

[Same structure as Option 1]

---

## Comparison Matrix

| Dimension | Option 1 | Option 2 | Option 3 |
|-----------|----------|----------|----------|
| Technical Complexity | ⭐⭐☆ | ⭐⭐⭐ | ⭐☆☆ |
| Risk Level | Low | Medium | High |
| Alignment Effort | None | 1 team | 3 teams |
| Time to Implement | 3 days | 5 days | 10 days |
| Reversibility | Easy | Medium | Hard |
| Addresses Root Cause | Partial | Full | Full |

---

## Recommendation

### Selected Option
**Option [N]: [Name]**

### Justification
[Why this option is the best choice given the constraints, stakeholders, and priorities]

### Conditions That Would Change This
- If [condition], then Option [X] would be better because [reason]

### Trade-offs Accepted
- [What we're giving up by choosing this option]
```

## NEXT STEPS

After completing design options, inform the user:

> Design options complete. To create an implementation plan, use:
> `@Researcher /plan`
>
> Or if you need to discuss with stakeholders first, share the design options document.
