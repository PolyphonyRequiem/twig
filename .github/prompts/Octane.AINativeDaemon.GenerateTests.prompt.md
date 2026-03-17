---
agent: AINativeDaemon
description: Generate test scaffolding for untested code with framework detection
tools: ['execute', 'search', 'code-search/*']
---

# Generate Tests

Find untested functions and generate comprehensive test scaffolding matching repo conventions.

## Inputs

- repo (string, required) — GitHub repository in owner/repo format
- path (string, optional) — Specific file or directory path to target for test generation

## Instructions

1. **Run the test generator**:
   ```bash
   python -m daemon run --agent test-generator --repo ${input:repo}
   ```

2. **Present results**:
   - Which framework was detected (Jest, pytest, xUnit, etc.)
   - Which functions lack coverage
   - Generated test cases grouped by: happy path, edge case, error path

3. **Follow repo conventions** for test file naming and structure.

## Expected Output

Detected test framework, list of untested functions, and generated test scaffolding grouped by happy path, edge case, and error path.

## Example User Prompts

- "Generate tests for untested code"
- "Find coverage gaps in src/utils/"
- "Write tests for the parser module"
