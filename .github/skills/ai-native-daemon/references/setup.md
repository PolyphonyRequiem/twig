# AI Native Daemon — Setup Guide

## Prerequisites

| Tool | Version | Install | Required For |
|------|---------|---------|-------------|
| Python | 3.11+ | [python.org](https://www.python.org/) | All |
| GitHub CLI | Latest | [cli.github.com](https://cli.github.com/) | GitHub repos |
| GitHub Copilot CLI | Latest | `gh extension install github/gh-copilot` | LLM runtime |
| GitHub Copilot | Subscription | [github.com/features/copilot](https://github.com/features/copilot) | LLM runtime |
| Azure CLI | Latest | [docs.microsoft.com](https://learn.microsoft.com/cli/azure/install-azure-cli) | ADO repos (if no PAT) |
| ADO PAT | N/A | [dev.azure.com](https://dev.azure.com/) → Personal Access Tokens | ADO repos (recommended) |

## Installation

```bash
# Clone and install
git clone https://github.com/azure-core/ai-native-team.git
cd ai-native-team
pip install -e .
```

## Configuration

```bash
cp daemon/config.default.yaml ~/.ai-native/config.yaml
```

Edit `~/.ai-native/config.yaml`:

### GitHub Only

```yaml
llm:
  provider: copilot-cli
  model: claude-sonnet-4.6

github:
  repos:
    - your-org/your-repo
  poll_interval_seconds: 60

governance:
  policy_mode: strict
```

### Azure DevOps Only

```yaml
llm:
  provider: copilot-cli
  model: claude-sonnet-4.6

azure_devops:
  org: your-ado-org
  project: your-project
  repos:
    - repo-a
    - repo-b
  poll_interval_seconds: 90

governance:
  policy_mode: strict
```

### Mixed (GitHub + ADO)

```yaml
llm:
  provider: copilot-cli
  model: claude-sonnet-4.6

github:
  repos:
    - azure-core/ai-native-team
  poll_interval_seconds: 60

azure_devops:
  org: msazure
  project: OneES
  repos:
    - my-service
  poll_interval_seconds: 90

governance:
  policy_mode: strict
```

## Starting the Daemon

```bash
# GitHub repos
python -m daemon watch --repos your-org/your-repo --dashboard 7070

# ADO repos (via env vars)
export AZURE_DEVOPS_PAT=your-pat-here
export ADO_ORG=your-org ADO_PROJECT=your-project ADO_REPOS=repo-a,repo-b
python -m daemon watch --dashboard 7070

# Fresh start (clear all previous state)
python -m daemon watch --repos your-org/your-repo --dashboard 7070 --fresh
```

## ADO Webhook Setup (Optional)

For real-time ADO events instead of polling, configure an ADO Service Hook:

1. Go to **Project Settings → Service Hooks** in Azure DevOps
2. Create a new subscription with **Web Hooks** as the service
3. Set the URL to `http://your-host:7070/api/webhook/ado`
4. Select events: Pull request created/updated/merged, Work item created/updated

## Verification

Open http://localhost:7070 to see the live dashboard.

```bash
# Check API
curl http://localhost:7070/api/status
```
