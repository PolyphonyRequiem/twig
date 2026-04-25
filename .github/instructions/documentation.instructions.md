---
description: 'Living documentation content creation standards and workflow guidelines.'
applyTo: '**/*.doc.md'
---

# Documentation Workflow Instructions

## ENFORCEMENT

Documentation updates MUST follow template structure and formatting conventions defined in `${config:documentation.template}`.

## MANDATORY PROCESS

When documentation workflow is triggered via `/Octane.Doc.*` commands:

1. **Read template first** - Load `${config:documentation.template}` before generating
2. **Identify correct section** - Match content to appropriate section (Design Decisions, Key Insights, etc.)
3. **Format consistently** - Use template conventions (emoji, structure, pseudo-code)
4. **Interview when appropriate** - Offer to capture team knowledge for new documentation
5. **Preserve existing content** - Updates append, don't replace (unless explicit)

## DIAGRAM CONVENTIONS

**Check configuration first:** Read `${config:documentation.include_diagrams}` and `${config:documentation.diagram_syntax}` from octane.yaml.

**If `include_diagrams: true`:**

Use Mermaid diagrams with syntax based on `diagram_syntax` setting:

- **`azure-devops`** (default):
  ```
  ::: mermaid
  sequenceDiagram
      participant A
      A->>B: Message
  :::
  ```

- **`github`**:
  ```
  ```mermaid
  sequenceDiagram
      participant A
      A->>B: Message
  ```
  ```

**If `include_diagrams: false`:**

Use simple text flow diagrams:
```
[Entry Point] → [Stage 1] → [Stage 2] → [Output]
```

## DIAGRAM STRATEGY

**When to offer diagrams:** Complex flows (>5 stages), multi-system interactions, state transitions, or user requests.

**When to skip:** Simple linear flows, already clear in text, would duplicate without adding value.

### C4 Model Abstraction (CRITICAL)

Follow [C4 model](https://c4model.com/) - **never mix abstraction levels in one diagram:**

| Level | Shows | Elements | Lines Mean |
|-------|-------|----------|------------|
| **C2 - Container** | Deployable apps, data stores | APIs, DBs, queues, services | Network calls |
| **C3 - Component** | Internal structure of ONE container | Classes, modules, packages | In-process calls |

**Progressive Disclosure:** For component docs, provide **both views**:
1. **External View (C2)** - Container interactions (what it does)
2. **Internal Implementation (C3)** - Component orchestration (how it works)

**Common Mistakes:**
- ❌ Showing `UserController` (C3) alongside `OrderService` (C2) - makes classes look like separate services
- ❌ Vague labels like "API" - use `[Name]<br/>[Role]` format
- ✅ Separate diagrams for C2 and C3 views

### Diagram Types

| Type | Use When | C2 vs C3 |
|------|----------|----------|
| **Sequence** | Multi-service request/response flows | C2: containers as participants; C3: classes as participants |
| **Flowchart** | Decision logic, branching, state | Either level |
| **Text flow** | Simple linear pipelines | Usually C3 |

**Approach:**
1. Propose diagram structure first
2. Get user approval on scope/detail
3. Generate and iterate

**Key principle:** Always propose before creating. User controls structure and detail.

## CODE DOCUMENTATION RULES

| Include ✅ | Avoid ❌ |
|-----------|----------|
| Pseudo-code algorithms | Actual implementation code |
| Configuration examples | Internal class details |
| Public API signatures | Private method internals |
| Integration patterns | Step-by-step code walkthrough |

**Why:** Real code drifts from docs. Pseudo-code captures intent without coupling.

## SECTION OWNERSHIP

When adding content, place in correct section:

| Content Type | Section |
|--------------|---------|
| Purpose | Overview |
| Data flow diagram | Core Architecture → Flow |
| Class responsibilities | Core Architecture → Key Components |
| Why X instead of Y | Core Architecture → Design Decisions |
| Public interfaces | API Reference |
| How-to guides | Common Scenarios |
| Error resolutions | Common Scenarios → Troubleshooting |
| Performance metrics | Key Insights → ⚡ Performance |
| Edge cases | Key Insights → ⚠️ Gotchas |
| Incident learnings | Key Insights → 🎯 Production Learnings |

## QUALITY CHECKS

Before completing documentation:

- [ ] All required sections present (Overview, Architecture, API, Scenarios, Insights)
- [ ] No placeholder text like "[TODO]" without explanation
- [ ] Diagrams use correct syntax for target platform
- [ ] Design decisions include rationale, not just choice
- [ ] Code examples are pseudo-code or configuration, not implementation
- [ ] "Last Updated" date is current
