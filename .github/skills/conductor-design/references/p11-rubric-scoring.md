# P11: Rubric-Based Scoring

All quality assessments — plan reviews, code reviews, acceptance checks — must use
a **structured scoring rubric** with named dimensions and explicit weights. Prefer
rubrics grounded in academic standards (IEEE 1016, ISO/IEC 25010) where applicable.

## Rubric Structure

- **Dimensions** — named quality aspects (e.g., Correctness, Feasibility, Clarity)
- **Weights** — percentage importance per dimension (must sum to 100%)
- **Scale** — 1-5 per dimension (1=Poor, 2=Needs Improvement, 3=Satisfactory, 4=Strong, 5=Excellent)
- **Composite score** — weighted sum mapped to 0-100
- **Critical threshold** — any dimension scored ≤ 2 is a blocking issue

## Rationale

- Single 0-100 scores are opaque — reviewers can't explain what failed
- Rubrics make feedback actionable (dimension X failed → fix X)
- Weights encode organizational priorities explicitly
- Academic grounding reduces subjective drift across agent sessions

## Standard Rubrics by Review Type

### Plan Technical Review (IEEE 1016 / ISO 25010 informed)

| Dimension | Weight | Measures |
|-----------|--------|----------|
| Correctness | 30% | Addresses requirements, no contradictions with codebase |
| Feasibility | 25% | Implementable given project constraints |
| Completeness | 20% | All affected components identified |
| Testability | 15% | Clear test strategy, verifiable acceptance criteria |
| Risk awareness | 10% | Breaking changes, edge cases surfaced |

### Plan Readability Review

| Dimension | Weight | Measures |
|-----------|--------|----------|
| Clarity | 30% | Unambiguous, no vague language |
| Actionability | 25% | Concrete enough for agent execution |
| Structure | 20% | Logical organization, consistent formatting |
| Traceability | 15% | Requirements → Issues → Tasks → PGs mapping clear |
| Scoping | 10% | Boundaries explicit — in/out/deferred |

### Code Review (implementation phase)

| Dimension | Weight | Measures |
|-----------|--------|----------|
| Correctness | 30% | Logic is right, handles edge cases |
| Safety | 25% | No regressions, no broken invariants, AOT/trim safe |
| Completeness | 20% | All acceptance criteria addressed, tests written |
| Conventions | 15% | Follows project patterns, naming, structure |
| Reviewability | 10% | Changes are minimal, well-scoped, clear commit messages |

### User Acceptance (implementation phase, P6 ≥95% confidence)

| Dimension | Weight | Measures |
|-----------|--------|----------|
| Functional correctness | 35% | Feature works as specified |
| UX coherence | 25% | Output formatting, help text, error messages are clear |
| Non-regression | 20% | Existing features still work |
| Documentation | 10% | Help text, README, command examples updated |
| Edge cases | 10% | Graceful handling of unusual inputs |

## Implications

- Reviewer prompts must include the rubric and instruct dimension-by-dimension scoring
- Review router uses composite scores and critical issue counts for gating
- Rubrics are versioned in the conductor-design skill and referenced by prompts
- Custom rubrics can be added for specialized reviews (security, accessibility, etc.)
