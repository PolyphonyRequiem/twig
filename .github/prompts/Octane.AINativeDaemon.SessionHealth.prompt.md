---
agent: AINativeDaemon
description: Check Copilot session health, SLO compliance, cost breakdown, and observability insights
tools: ['agent-sre/*']
---

# Session Health

Analyze Copilot session health, SLO compliance, and cost metrics using the Agent SRE observability tools.

## Inputs

- days (integer, optional) — Number of days to analyze, defaults to `7`
- repository (string, optional) — Filter by repository (owner/repo)

## Instructions

1. **Get session insights**:
   Use `agent-sre/get_insights` to retrieve an overview of session health including success rate,
   hallucination rate, cost per outcome, flow score, and DORA metrics.

2. **Get cost breakdown**:
   Use `agent-sre/get_cost_breakdown` to analyze token spend, model costs, and budget utilization.

3. **Check SLO compliance**:
   Use `agent-sre/get_health_policies` to verify that sessions meet defined service-level objectives.

4. **Present a summary**:
   - Overall success rate and trend
   - SLO compliance status (pass/fail per objective)
   - Cost per outcome and budget burn rate
   - Top repositories by activity
   - Any issues needing attention (SLO breaches, cost spikes, high hallucination rate)

5. **If issues are detected**, suggest corrective actions:
   - SLO breach → review failing prompts, adjust thresholds, or escalate
   - Cost spike → identify high-cost sessions, review model selection
   - Hallucination rate → review prompt quality, add grounding context

## Expected Output

Session health report including success rate, SLO compliance, cost breakdown, and actionable recommendations.

## Example User Prompts

- "How healthy are our Copilot sessions?"
- "Check SLO compliance for the last 30 days"
- "What's our cost per outcome this week?"
- "Session health for azure-core/ai-native-team"
