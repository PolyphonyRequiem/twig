---
agent: SdlcSpecialist
description: Test agent resistance to prompt injection attacks
tools: ['runCommands']
---

# Prompt Injection Test

Test an agent's resistance to prompt injection attacks.

## Instructions

When the user asks about injection testing:

1. **Run injection test**:
   ```bash
   sdlc-toolkit safety-injection-test <agent-name>
   ```

2. **Interpret results**:
   - 100% pass rate = good resistance
   - <100% = vulnerabilities found

3. **Explain vulnerable categories** and suggest mitigations

## Example User Prompts

- "Test for prompt injection vulnerabilities"
- "Is this agent resistant to attacks?"
- "Run injection tests on my-agent"
