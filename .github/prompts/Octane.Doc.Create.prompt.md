---
description: Generate comprehensive living documentation for existing code
argument-hint: The name of the code to document (e.g., Service.Authentication, UserRepository)
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `Name` (string, required): The name of the code to document (e.g., "Service.Authentication", "UserRepository", "PaymentGateway")

If you do not have a `Name`, you cannot proceed. Ask the user to specify what they want to document.

## PRIMARY DIRECTIVE

Generate comprehensive **living documentation** for `${input:Name}`. The documentation must:
- Follow the template structure exactly
- Capture architecture, design decisions, and key insights
- Be machine-readable for future AI consumption
- Contain no placeholder content - all sections must have real information or be marked [TODO]
- Include enterprise context (design docs, decision history, ownership) when available via WorkIQ

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Locate Source**  
   Use `code-search/*` tools or workspace search to find the source files. Identify:
   - Main entry points and classes
   - Related interfaces and types
   - Configuration files
   - Test files

2. **Gather Enterprise Context**  
   Use `work-iq/*` tools to search for relevant enterprise knowledge:
   - Design reviews and tech specs related to `${input:Name}` (search SharePoint, OneDrive)
   - Architecture decision discussions in Teams threads and email chains
   - Meeting notes where `${input:Name}` design was discussed
   - People who authored or contributed to design documents (ownership discovery)
   
   This step enriches the documentation with "why" context that code alone cannot provide.

3. **Analyze Architecture**  
   Examine the codebase to understand:
   - Purpose and responsibilities
   - Dependencies (internal and external)
   - Data flow and key operations
   - Integration points with other systems

4. **Extract Design Decisions**  
   Combine code patterns with enterprise context to identify intentional choices:
   - Why certain libraries or frameworks were chosen (check design docs via WorkIQ)
   - Architectural patterns in use (repository, factory, etc.)
   - Error handling strategies
   - Performance optimizations
   - Trade-offs discussed in meetings or email threads (via WorkIQ)

5. **Document API Surface**  
   Identify and document:
   - Public interfaces and contracts
   - Configuration options
   - Extension points

6. **Identify Common Scenarios**  
   Based on code analysis and test files:
   - Primary use cases
   - Error scenarios and handling
   - Edge cases

7. **Generate Documentation**  
   Create the documentation file following the template at `${config:documentation.template}`:
   - Use pseudo-code instead of actual implementation code
   - Mark unknown sections with [TODO: need team input]
   - Include links to design docs and decision sources discovered via WorkIQ
   - Add an ownership/contacts section if people were discovered
   
   **Diagram Configuration:**
   - If `${config:documentation.include_diagrams}` is `true`, include Mermaid diagrams for complex flows
   - Use `${config:documentation.diagram_syntax}` for diagram fencing:
     - `azure-devops`: Use `::: mermaid` opening and `:::` closing
     - `github`: Use standard ``` ```mermaid ``` code blocks
   - If `include_diagrams` is `false`, use simple text flow diagrams instead:
     ```
     [Entry] → [Stage 1] → [Stage 2] → [Output]
     ```

8. **Offer Interview**  
   After generating documentation, ask:
   > "Documentation generated at `${config:documentation.output_path}/${input:Name}.doc.md`. 
   > Would you like me to interview you for team insights? This captures design rationale, gotchas, and production learnings that can't be found in code."

## CODE GRAPH TOOLS

Use `code-search/*` tools for codebase analysis.

## OUTPUT LOCATION

- File Path: `${config:documentation.output_path}/${input:Name}.doc.md`

## DOCUMENTATION PRINCIPLES

1. **Capture "Why" not just "What"** - Code shows what, docs explain why
2. **Use pseudo-code** - Real code drifts, pseudo-code captures intent
3. **Mark unknowns explicitly** - [TODO] is better than guessing
4. **Prioritize design decisions** - These are hardest to reconstruct later
5. **Include failure modes** - What breaks and how to fix it

## TEMPLATE REFERENCE

Follow the structure defined in: `${config:documentation.template}`

The template contains all required sections, formatting rules, and examples. Read it before generating documentation.
