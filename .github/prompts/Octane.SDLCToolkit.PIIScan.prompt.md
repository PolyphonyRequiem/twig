---
agent: SdlcSpecialist
description: Scan for Personally Identifiable Information
tools: ['runCommands']
---

# PII Scan

Scan code, data, or outputs for Personally Identifiable Information.

## Instructions

When the user asks to find PII:

1. **Scan a directory**:
   ```bash
   sdlc-toolkit safety-pii-scan ./path/to/scan
   ```

2. **Interpret findings**:
   - List PII types found
   - Show risk levels
   - Provide locations

3. **Recommend remediation** for any PII found

## Example User Prompts

- "Scan for PII in this directory"
- "Check for personal data"
- "Find any data leaks"
- "Is there sensitive data in this code?"
