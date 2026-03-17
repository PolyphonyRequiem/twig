---
applyTo: "**/templates/*-toolkit*.md"
---

# Kusto Toolkit Instructions

When working with KQL investigation toolkits, follow these guidelines:

## Query Execution

- **Run queries selectively** - Do not execute all queries at once. Start broad, then narrow down.
- **Use Azure MCP Server for Azure Data Explorer** - Execute queries using the `kusto_query` tool with proper cluster and database from config.
- **Respect time ranges** - Always adjust time ranges to match the incident window. Default is last 24 hours.

## Workflow Pattern

Each toolkit defines its own STEP-based workflow in the file header. Follow the numbered STEPs in order:

1. Read the **QUICK START GUIDE** section to understand the toolkit's purpose
2. Follow the **INVESTIGATION WORKFLOW** section for the step sequence
3. Execute queries from each STEP section as needed
4. Refer to **TROUBLESHOOTING TIPS** for common patterns

## Configuration Variables

Toolkits use `${config:...}` variables that resolve from `octane.yaml`:

- `${config:incident.kusto.clusters.rpaas.cluster}` - Control plane Kusto cluster
- `${config:incident.kusto.clusters.telemetry.cluster}` - Data plane Kusto cluster
- `${config:incident.kusto.thresholds.p95_normal_ms}` - Normal P95 threshold

## Interpreting Results

- **Empty results** - Not necessarily bad; may indicate no issues in that area
- **High counts** - Context matters; compare to baseline or threshold values
- **Error patterns** - Look for clustering in time, region, or resource

## Common Pitfalls

- Running queries without adjusting time range
- Skipping the STEP workflow and jumping to conclusions
- Not correlating control plane with data plane findings
- Missing async operation status when investigating deployments
