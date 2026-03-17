---
agent: AINativeDaemon
description: Start the AI Native Daemon to watch GitHub and/or Azure DevOps repositories for issues and PRs
tools: ['execute']
---

# Start AI Native Daemon

Start the governed agent fleet to continuously watch your repositories.

## Inputs

- repos (string, optional) — Comma-separated GitHub repositories in owner/repo format
- ado_org (string, optional) — Azure DevOps organization name
- ado_project (string, optional) — Azure DevOps project name
- ado_repos (string, optional) — Comma-separated ADO repository names

## Instructions

1. **Ask which repos to watch** if not specified:
   ```
   Which repositories should I watch?
   - GitHub: owner/repo format (e.g., your-org/your-repo)
   - Azure DevOps: set ADO_ORG, ADO_PROJECT, ADO_REPOS env vars
   ```

2. **Set up ADO auth** if ADO repos are specified:
   ```bash
   export AZURE_DEVOPS_PAT=${input:ado_pat}
   export ADO_ORG=${input:ado_org}
   export ADO_PROJECT=${input:ado_project}
   export ADO_REPOS=${input:ado_repos}
   ```

3. **Start the daemon**:
   ```bash
   python -m daemon watch --repos ${input:repos} --dashboard 7070 --fresh
   ```

4. **Confirm it started** by checking the dashboard:
   ```bash
   curl -s http://localhost:7070/api/status | python -c "import sys,json; d=json.load(sys.stdin); print(f'Agents: {len(d.get(\"agents\",{}))}, Repos: {len(d.get(\"repos\",[]))}')"
   ```

5. **Report to user**: number of agents loaded, repos being watched (GitHub + ADO), dashboard URL.

## Expected Output

Confirmation that the daemon started successfully, including the number of agents loaded, repos being watched, and the dashboard URL.

## Example User Prompts

- "Start watching my repos"
- "Monitor azure-core/my-service for issues"
- "Launch the daemon with a fresh state"
- "Watch my ADO repos in msazure/OneES"
