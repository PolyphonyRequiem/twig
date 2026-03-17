---
agent: AINativeDaemon
description: Run OWASP security scan for secrets, vulnerabilities, and dependency risks
tools: ['execute', 'search']
---

# Security Scan

Run a comprehensive security scan: secrets, OWASP Top 10, dependency CVEs, infrastructure misconfigurations.

## Inputs

- repo (string, required) — GitHub repository in owner/repo format

## Instructions

1. **Run the security scanner**:
   ```bash
   python -m daemon run --agent security-scanner --repo ${input:repo}
   ```

2. **Present findings** by category:
   - 🔴 Secrets (hardcoded credentials, API keys)
   - 🔴 Injection (SQL, XSS, command injection)
   - 🟡 Auth/Authz (missing checks, weak sessions)
   - 🟡 Dependencies (CVEs, unpinned versions)
   - 🟢 Infrastructure (workflow permissions, IaC misconfig)

3. **NEVER echo secret values** — always redact with `****`.

## Expected Output

Security findings categorized by type and severity, with redacted secret values and remediation suggestions.

## Example User Prompts

- "Scan this repo for secrets"
- "Run an OWASP security check"
- "Check for hardcoded credentials"
- "Are there any vulnerable dependencies?"
