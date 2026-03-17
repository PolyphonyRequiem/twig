# Agent SRE — Setup Guide

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| Python | 3.11+ | [python.org](https://www.python.org/) |
| Agent SRE | Latest | `pip install git+https://github.com/azure-core/agent-sre.git` |

## Installation

```bash
pip install git+https://github.com/azure-core/agent-sre.git
```

Or for development:

```bash
git clone https://github.com/azure-core/agent-sre.git
cd agent-sre
pip install -e .
```

## Starting the Dashboard

```bash
# Start the observability dashboard (default port 7429)
python -m agent_viewer start

# Or use the launcher script (Windows)
.\Start-Dashboard.ps1
```

The dashboard auto-imports data from `~/.copilot/session-store.db` (Copilot CLI sessions).

## MCP Server

The MCP server exposes observability data to AI tools via stdio JSON-RPC:

```bash
# Run standalone
python -m agent_viewer mcp-server
```

Add to `.vscode/mcp.json` for VS Code integration:

```json
{
  "servers": {
    "agent-sre": {
      "type": "stdio",
      "command": "python",
      "args": ["-m", "agent_viewer", "mcp-server"]
    }
  }
}
```

## Available MCP Tools

| Tool | Description |
|------|-------------|
| `get_insights` | Session success rate, hallucination %, DORA metrics |
| `get_cost_breakdown` | Total cost, per-session, per-PR, daily trend |
| `get_activity` | Recent Copilot turns with tool usage |
| `get_sessions` | Session list with duration and correction rate |
| `get_health_policies` | Policy compliance status |
| `get_decision_points` | Corrections, clarifications, direction changes |
| `get_repositories` | All repos with Copilot activity |

## Verification

```bash
# Check the API
curl http://localhost:7429/api/v1/insights

# Check the dashboard
open http://localhost:7429
```
