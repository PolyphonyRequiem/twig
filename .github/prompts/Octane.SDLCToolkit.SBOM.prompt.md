---
agent: SdlcSpecialist
description: Generate Software Bill of Materials
tools: ['runCommands']
---

# SBOM Generation

Generate a Software Bill of Materials for dependency tracking.

## Instructions

When the user asks for SBOM or dependency analysis:

1. **Generate SBOM**:
   ```bash
   sdlc-toolkit safety-sbom ./project-path
   ```

2. **For JSON output** (automation):
   ```bash
   sdlc-toolkit safety-sbom ./project --format json
   ```

3. **Interpret results**:
   - Total dependencies
   - Vulnerable packages
   - Ecosystem breakdown

## Example User Prompts

- "Generate SBOM for this project"
- "List all dependencies"
- "Check for vulnerable packages"
- "What does this project depend on?"
