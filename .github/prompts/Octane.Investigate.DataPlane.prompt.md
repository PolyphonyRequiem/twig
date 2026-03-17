---
description: Investigate data plane API latency, exceptions, and throughput issues using service telemetry
model: Claude Opus 4.6 (copilot)
---

## CONTEXT

The user will provide investigation context such as:
- IcM incident number or alert details
- Region experiencing issues (e.g., weu001, eus001)
- Time range or "when did this happen"
- Symptoms: high latency, errors, timeouts
- Trace ID or correlation ID from logs

Start with whatever context is provided and gather more as needed.

## PRIMARY DIRECTIVE

Investigate data plane issues using the KQL toolkit. Guide the user through analyzing API latency, error rates, and throughput to identify performance degradation or service failures.

## TOOLKIT REFERENCE

Use the queries from `templates/data-plane-toolkit-geneva.md` in this scenario folder.

> **Schema:** Geneva Log table with K8s metadata (`env_k8s_namespace`, `env_app_name`, `env_cloud_env`)

The toolkit contains:

- **STEP 1: SCOPE** - Latency overview, incident spike detection, regional hotspots
- **STEP 2: ERRORS** - Error correlation with latency categories
- **STEP 3: INFRASTRUCTURE** - Nginx ingress latency, pod/node hotspots
- **STEP 4: DEEP DIVE** - Trace ID and correlation ID lookup
- **STEP 5: PATTERNS** - Daily/hourly/regional outlier analysis

SERVICE-SPECIFIC: Check for `data-plane-toolkit-geneva.*.md` files for service-specific patterns (e.g., internal service breakdown, dependency failures, batch jobs). These extend the generic toolkit.

## EXECUTION

Use **Azure MCP Server for Azure Data Explorer** (`kusto_query` tool) to execute queries:
- Cluster: `${config:incident.kusto.clusters.telemetry.cluster}`
- Database: `${config:incident.kusto.clusters.telemetry.database}`

## THRESHOLDS

Evaluate metrics against configured thresholds:
- P95 Normal: < `${config:incident.kusto.thresholds.p95_normal_ms}`ms
- P95 Elevated: < `${config:incident.kusto.thresholds.p95_elevated_ms}`ms
- P95 High: < `${config:incident.kusto.thresholds.p95_high_ms}`ms
- P95 Critical: >= `${config:incident.kusto.thresholds.p95_critical_ms}`ms
- Failure Rate Warning: `${config:incident.kusto.thresholds.failure_rate_warning}`%
- Failure Rate Critical: `${config:incident.kusto.thresholds.failure_rate_critical}`%

## WORKFLOW STEPS

Present the following steps as **trackable todos**:

1. **Establish Context**
   - Determine time range from IcM/alert or user input
   - Identify region (env_cloud_env) if known
   - Note any trace IDs, correlation IDs, or error messages

2. **Scope the Incident (STEP 1)**
   - Run toolkit query 1.1 (Primary DRI Metric) for latency overview
   - Run toolkit query 1.2 (Incident Spike Detection) to find high latency periods
   - Run toolkit query 1.3 (Regional Latency Hotspots) to identify affected regions
   - Adjust time range in queries to match incident window

3. **Analyze Errors (STEP 2)**
   - Run toolkit query 2.1 (Error Correlation with High Latency)
   - Check if high latency correlates with error rates
   - Distinguish latency categories: Normal, Elevated, High, Critical

4. **Check Infrastructure (STEP 3)**
   - Run toolkit query 3.1 (Nginx Ingress Latency Analysis)
   - Run toolkit query 3.2 (Pod/Node Latency Hotspots)
   - Look for specific pods or nodes driving high P99

5. **Deep Dive (STEP 4)**
   - If trace ID available, run toolkit query 4.1 (Trace ID Lookup)
   - If correlation ID available, run toolkit query 4.2 (Correlation ID Lookup)
   - Trace the request flow to identify slow component

6. **Pattern Analysis (STEP 5 - if needed)**
   - Run toolkit query 5.1 (Daily Extreme Outlier Comparison) for recurring patterns
   - Run toolkit query 5.3 (Regional Extreme Outlier Comparison) for regional issues
   - Compare against baseline from same time yesterday/last week

7. **Summarize Findings**
   - Quantify latency impact (P95, P99 vs normal)
   - Identify root cause (infrastructure, dependency, regional)
   - Recommend next steps or escalation

## OUTPUT FORMAT

Present findings as:

### Investigation Summary

**Scope:** [Service endpoint, time range, region]

**Latency Analysis:**
| Time Period | P50 | P95 | P99 | Status |
|-------------|-----|-----|-----|--------|
| Current | ... | ... | ... | [Normal/Elevated/High/Critical] |
| Baseline | ... | ... | ... | ... |

**Error Analysis:**
| Error Type | Count | % of Requests |
|------------|-------|---------------|
| ... | ... | ... |

**Throughput:**
- Peak: [requests/min]
- Current: [requests/min]
- Failure Rate: [%] (threshold: ${config:incident.kusto.thresholds.failure_rate_warning}%)

**Root Cause Hypothesis:**
[Based on patterns observed]

**Recommended Actions:**
1. [Immediate mitigation]
2. [Further investigation]
3. [Long-term fix]

## IcM CORRELATION

> When correlating with IcM incidents:

- **DeID Team ID:** 111744 (use with `search_incidents_by_owning_team_id`)
- **IcM MCP Limitation:** Cannot search by time range - use IcM portal for "what happened at 17:40 UTC"
- If you have an incident ID, use `get_incident_details_by_id` for context

## IMPORTANT

- Always compare current metrics to baseline/normal values
- Check if latency issues correlate with throughput spikes
- Look for dependency failures that might cause cascading issues
- Consider regional patterns if investigating multi-region services
- Refer to TROUBLESHOOTING TIPS section in the toolkit for common patterns
