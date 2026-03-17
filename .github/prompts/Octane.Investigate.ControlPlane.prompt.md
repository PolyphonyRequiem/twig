---
description: Investigate control plane (ARM/RPaaS) operations for deployment failures, provisioning issues, and resource management problems
model: Claude Opus 4.6 (copilot)
---

## CONTEXT

The user will provide investigation context such as:
- IcM incident number or alert details
- Error messages or status codes (504, 500, 409, etc.)
- Resource name or resource ID
- Correlation ID or operation ID
- Time range or "when did this happen"

Start with whatever context is provided and gather more as needed.

## PRIMARY DIRECTIVE

Investigate control plane operations using the KQL toolkit. Guide the user through a structured investigation workflow to identify deployment failures, ARM operation issues, and resource provisioning problems.

## TOOLKIT REFERENCE

Use the queries from `templates/control-plane-toolkit.md` in this scenario folder. The toolkit contains:

- **STEP 1: FAILURES** - ARM operation failure summary, deletion failures, provisioning failures
- **STEP 2: RPAAS** - RPaaS to UserRP communication analysis
- **STEP 3: LATENCY** - Control plane operation timing and timeout detection
- **STEP 4: DEEP DIVE** - Resource-specific timeline, correlation ID lookup

## EXECUTION

Use **Azure MCP Server for Azure Data Explorer** (`kusto_query` tool) to execute queries:
- Cluster: `${config:incident.kusto.clusters.rpaas.cluster}`
- Database: `${config:incident.kusto.clusters.rpaas.database}`

## WORKFLOW STEPS

Present the following steps as **trackable todos**:

1. **Establish Context**
   - Determine time range from IcM/alert or user input
   - Note any correlation IDs, resource names, or error codes
   - Identify the symptom: deletion stuck? creation failed? timeout?

2. **Identify Operation Failures (STEP 1)**
   - Run toolkit query 1.1 (ARM Operation Failure Summary) for overview
   - Based on symptom, run 1.2 (Deletion) or 1.3 (Provisioning) failures
   - Update time range in queries to match incident window

3. **Analyze RPaaS Communication (STEP 2)**
   - Run toolkit query 2.1 (RPaaS to UserRP Request Tracking)
   - If slow operations suspected, run 2.2 (Latency Analysis)
   - Run 2.3 (Failure Correlation) to get error details

4. **Check for Timeouts (STEP 3)**
   - Run toolkit query 3.2 (Operation Timeout Detection) for 504 errors
   - Run 3.3 (Regional Hotspots) to check if region-specific
   - Compare P95/P99 latency against thresholds

5. **Deep Dive on Specific Resource (STEP 4)**
   - If resource name known, run 4.1 (Resource Operation Timeline)
   - Cross-reference with 4.2 (RPaaS Operations) using correlation ID
   - Run 4.3 for specific correlation ID lookup

6. **Summarize Findings**
   - List failed operations with error codes and patterns
   - Identify root cause (timeout, RPaaS failure, regional issue)
   - Recommend next steps or escalation path

## OUTPUT FORMAT

Present findings as:

### Investigation Summary

**Scope:** [Subscription, resource group, time range]

**Key Findings:**
| Operation | Count | Status | Error Pattern |
|-----------|-------|--------|---------------|
| ... | ... | ... | ... |

**Root Cause Hypothesis:**
[Based on patterns observed]

**Recommended Actions:**
1. [Immediate action]
2. [Follow-up investigation]
3. [Escalation if needed]

## IMPORTANT

- Always adjust time ranges in queries to match the incident window
- Use correlation IDs to trace operations across systems
- Check both sync and async operation status for long-running deployments
- Refer to TROUBLESHOOTING TIPS section in the toolkit for common patterns

