---
agent: SdlcSpecialist
description: List available design validation rules
tools: ['runCommands']
---

# Design Validation Rules

Show the available validation rules for design review.

## Instructions

When the user asks about validation rules:

1. **List all rules**:
   ```bash
   sdlc-toolkit design-rules
   ```

2. **Filter by category**:
   ```bash
   sdlc-toolkit design-rules --category security
   sdlc-toolkit design-rules --category reliability
   sdlc-toolkit design-rules --category architecture
   ```

3. **Filter by priority**:
   ```bash
   sdlc-toolkit design-rules --priority recommended
   ```

## Example User Prompts

- "What rules do you check?"
- "Show me the security rules"
- "List all validation rules"
- "What do you look for in a design?"
