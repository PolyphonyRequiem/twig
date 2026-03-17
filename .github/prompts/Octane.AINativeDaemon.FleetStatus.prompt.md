---
agent: AINativeDaemon
description: Show agent fleet status, health, and recent activity
tools: ['execute']
---

# Fleet Status

Show the current health and activity of the AI Native Daemon agent fleet.

## Inputs

- endpoint (string, optional) — Dashboard API endpoint, defaults to `http://localhost:7070`

## Instructions

1. **Check daemon status**:
   ```bash
   curl -s http://localhost:7070/api/status
   ```

2. **Present a summary**:
   - Active agents (count and names)
   - Overall success rate
   - SLO compliance status
   - Recent tasks (last 5)
   - Any issues: open circuit breakers, pending approvals, failed tasks

3. **If daemon not running**, offer to start it.

## Expected Output

Fleet health summary including active agent count, success rate, SLO compliance, recent tasks, and any issues requiring attention.

## Example User Prompts

- "How are my agents doing?"
- "Fleet status"
- "Are there any pending approvals?"
- "Show me the dashboard"
