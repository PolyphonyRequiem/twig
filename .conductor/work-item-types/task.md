# Task — Work Item Type Definition (Basic Process)

## Definition

A Task is a single atomic unit of work that can be completed in a single focused session, typically ranging from minutes to a few hours. It lives under an Issue and its description must be self-contained enough that the implementer can start working without reading sibling Tasks, the parent Issue, or plan documents. Tasks are the primary unit of individual assignment and the level at which code changes, test additions, and configuration updates are tracked.

## Purpose

A Task answers: **"What exactly do I need to do, and how do I know I'm done?"** The description must contain or reference everything anticipated to be needed for full context. The implementer (human or agent) should not have to fall back to a plan file unless they encounter unanticipated ambiguity during execution. If a Task description requires reading the parent Issue to understand, it is insufficiently described.

## Audience

| Role | How They Use Tasks |
|------|-------------------|
| **Contributor** | Primary implementer. Reads the Task and executes. Should not need to ask clarifying questions. |
| **AI Agent** | Implements automatable Tasks. Reads the description as its complete instruction set. |
| **Project Owner** | Tracks Task completion for progress. Reviews implementation during code review. Rarely reads Task descriptions. |

## Ownership

- **Assigned To:** Individual contributor or AI agent
- **Driver:** The assignee — Tasks are self-directed once assigned
- **Reviewer:** Peer or Project Owner reviews the implementation (code review)
- **Author:** Project Owner or contributor writes the description during planning; agent may generate initial descriptions

## In Scope for a Task

- A single code change (add a class, modify a method, update a config file)
- Writing or updating tests for a specific change
- Configuration changes (csproj, Directory.Build.props, GitHub Actions)
- Documentation updates tied to a specific change
- Completable in one focused session (minutes to a few hours)

## Out of Scope for a Task

- Work requiring multiple sessions or context switches (break into multiple Tasks)
- Design decisions (those belong in the Issue description or plan document)
- Cross-cutting changes spanning unrelated areas (that's multiple Tasks or a separate Issue)
- Work with unclear scope ("investigate X" — clarify scope first, then create a Task)

## Naming Conventions

- Start with a verb: "Add", "Update", "Remove", "Extract", "Verify", "Configure"
- Be specific: "Add ResultType to SyncCommand" not "Add result type"
- Keep under 60 characters

**Good examples:**
- "Add BatchExecutionEngine to Infrastructure layer"
- "Extract DI registrations into extension methods"
- "Update xUnit tests for new error handling path"
- "Remove deprecated FlowGitService references"

**Bad examples:**
- "Do the thing"
- "Task 1"
- "Fix it"
- "Investigate and maybe implement something"

## Description Template

See: `templates/task-template.md`

## Language Guidelines

- **What to Change:** Be exhaustive. Name every file, class, method, config key. The implementer should be able to find every touchpoint from this section alone.
- **How to Change:** Step-by-step for non-obvious changes. For straightforward changes (add a using, rename a variable), a brief statement suffices. For complex changes (refactoring a service registration chain), spell out the steps.
- **Acceptance Criteria:** Binary pass/fail. "Build passes" and "tests pass" are baseline. Add task-specific criteria that verify the change works.
- **Context:** Include anything the implementer might need to look up: related code paths, gotchas from prior attempts, links to documentation. Better to over-include than under-include.

## Self-Containment Principle

**The Task description IS the implementation brief.** It must contain or directly reference (via links) everything the implementer needs:

- File paths where changes are needed
- Class/method signatures to add or modify
- Expected behavior after the change
- How to verify the change works
- Any setup steps (environment, tools, dependencies)

If an implementer (human or agent) has to read the parent Issue description or a plan document to understand what to do — the Task is insufficiently described. Plan documents are a fallback for unanticipated ambiguity only, not a required prerequisite for Task execution.

## Relationship to Plan Documents

Tasks do NOT have their own plan document. The Task description carries all context. The parent Issue's plan document contains the broader design rationale if the implementer needs to understand "why" beyond what's in the Task description.
