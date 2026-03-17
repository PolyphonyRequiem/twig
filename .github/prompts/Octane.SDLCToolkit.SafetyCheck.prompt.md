---
agent: SdlcSpecialist
description: Run comprehensive safety evaluation on an agent including prompt injection testing, PII scan, and SBOM generation
tools: ['runCommands', 'search']
---

# Safety Check

Evaluate an agent for safety and compliance before deployment.

## Instructions

When the user asks for a safety check or security audit:

1. **Run comprehensive safety check**:
   ```bash
   sdlc-toolkit safety-check <agent-name>
   ```

2. **With code path for SBOM**:
   ```bash
   sdlc-toolkit safety-check <agent-name> --code ./path/to/agent
   ```

3. **Interpret results**:
   - PASS = Safe to deploy
   - WARN = Review warnings, may proceed
   - FAIL = Must fix blockers first

4. **Provide remediation guidance** for any issues found

## Example User Prompts

- "Is this agent safe to deploy?"
- "Run a safety check on my-agent"
- "Security audit for the auth agent"
- "Check if my-agent is ready for production"
