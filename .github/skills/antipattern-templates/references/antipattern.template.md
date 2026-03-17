# Anti-Pattern: [Clear and descriptive Title]

## Scope

[regex or glob filters to what kind of files the anti-pattern applies to]

## Measurable Impact

[Describe what is the negative impact the anti pattern can cause if it gets checked in and why it is important to catch it. Connect it to measurable metrics]

## Detection Instructions

[
    Create clear, unambiguous and precise detection instructions that tells an agent how to find the described code pattern in a codebase or code snippet. Feel free to ask the user on details about the anti-pattern so you can craft better detection instructons

    - **MANDATORY**: Use exact syntax format: `**It is not a violation**` and `**It is a violation**`
    - **REQUIRED ORDER**: List all `**It is not a violation**` cases BEFORE `**It is a violation**` cases
    - **SPECIFICITY**: Include exact code patterns, function names, and scenarios
    - **AVOID**: Vague terms like "inappropriate", "bad", "should not" - use specific technical criteria
]

## Negative example
[
    Generate a negative example of an anti-pattern violation using code snippets. Add comments to point the problematic lines and why it is an issue.
]

## Positive example
[
    Generate a positive example of an anti-pattern violation using code snippets. Explain how this solution solves the violation
]
