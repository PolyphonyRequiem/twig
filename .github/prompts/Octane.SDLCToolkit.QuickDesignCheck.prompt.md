---
agent: SdlcSpecialist
description: Quick design check focusing on critical issues only
tools: ['runCommands']
---

# Quick Design Check

Fast validation focusing on critical and high-severity issues only.

## Instructions

When the user asks for a quick design check:

1. **Run quick review** (recommended rules only):
   ```bash
   sdlc-toolkit design-review <document> --priority recommended
   ```

2. **Focus on blockers**: Only highlight critical/high issues

3. **Keep it brief**: This is for quick pre-meeting checks

## Example User Prompts

- "Quick check on this design"
- "Any blockers in this spec?"
- "Fast review of design.md"
- "Is this ready for review meeting?"
