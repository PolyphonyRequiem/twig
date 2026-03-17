---
description: Correlate findings across control plane and data plane to build a complete incident timeline
model: Claude Opus 4.6 (copilot)
---

## CONTEXT

The user will provide investigation context such as:
- IcM incident number or alert details
- Time range when the issue was observed
- Symptoms: deployment failures + API errors, provisioning + latency spikes
- Resource name or correlation ID spanning both planes

This prompt is for correlating **BOTH** control plane and data plane findings. Start with whatever context is provided.

## PRIMARY DIRECTIVE

Build a complete incident timeline by correlating control plane operations with data plane telemetry. Identify temporal relationships between deployments, configuration changes, and service degradation.

## TOOLKIT REFERENCES

Use queries from both toolkits in this scenario folder:

**Control Plane** (`templates/control-plane-toolkit.md`):
- STEP 1: ARM operation failures (1.1 Summary, 1.2 Deletion, 1.3 Provisioning)
- STEP 2: RPaaS communication (2.1 Request Tracking, 2.3 Failure Correlation)
- STEP 4: Deep dive (4.1 Resource Timeline, 4.3 Correlation ID Lookup)

**Data Plane** (`templates/data-plane-toolkit-geneva.md`):
- STEP 1: Latency scope (1.1 Primary DRI Metric, 1.2 Spike Detection)
- STEP 2: Error correlation (2.1 Error Correlation with High Latency)
- STEP 4: Deep dive (4.1 Trace ID Lookup, 4.2 Correlation ID Lookup)

**Service-Specific Extensions** (`templates/data-plane-toolkit-geneva.*.md` if available):
- Check for service-specific query patterns (e.g., internal service breakdown, dependency analysis)
- These extend the generic toolkit with domain-specific queries

## EXECUTION

Use **Azure MCP Server for Azure Data Explorer** (`kusto_query` tool) to execute queries:

**Control Plane:**
- Cluster: `${config:incident.kusto.clusters.rpaas.cluster}`
- Database: `${config:incident.kusto.clusters.rpaas.database}`

**Data Plane:**
- Cluster: `${config:incident.kusto.clusters.telemetry.cluster}`
- Database: `${config:incident.kusto.clusters.telemetry.database}`

## CROSS-CLUSTER CORRELATION

**Important:** Control plane and data plane have different `correlationId` values.

| Scope | Tables | Tracks |
|-------|--------|--------|
| **ARM correlationId** | RPaaS HttpIncomingRequests, HttpOutgoingRequests | ARM -> RPaaS -> UserRP (control plane) |
| **API correlationId** | Data Plane Log | Client -> API -> Backend (data plane) |

### Recommended: Use `clientRequestId` for Cross-Cluster Correlation

The `clientRequestId` from RPaaS matches the `x-ms-client-request-id` header in data plane logs.

1. **Find clientRequestId:** Run `control-plane-toolkit.md` query 4.4 (Cross-Cluster Correlation)
2. **Find matching request:** Run `data-plane-toolkit-geneva.md` query 4.3 with the clientRequestId

### Correlation Method Comparison

| Method | Uniqueness | Use Case |
|--------|------------|----------|
| **`clientRequestId`** | Unique per request | Precise correlation (recommended) |
| Timestamp + Path + Region | May have collisions | Quick check when traffic is low |
| `correlationId` | Different IDs per plane | Within-plane only |

### Within-Plane Correlation

Use toolkit query 4.3 (control-plane) or 4.2 (data-plane) for correlationId lookups within the same cluster.

## WORKFLOW STEPS

Present the following steps as **trackable todos**:

1. **Establish Timeline Window**
   - Determine incident start time from IcM/alert or user report
   - Expand window 30-60 minutes before first symptom for root cause
   - Note any correlation IDs that span both planes

2. **Check Control Plane Activity**
   - Run control-plane-toolkit query 1.1 (ARM Operation Failure Summary)
   - If deployments suspected, run 1.3 (Provisioning State Failures)
   - Run query 4.1 (Resource Operation Timeline) for specific resource
   - Note timestamps and correlation IDs of each control plane event

3. **Check Data Plane Impact**
   - Run data-plane-toolkit query 1.1 (Primary DRI Metric) for latency timeline
   - Run query 1.2 (Incident Spike Detection) to find degradation start
   - Run query 2.1 (Error Correlation) to check error patterns
   - Note timestamps of latency spikes and error increases

4. **Identify Correlation Points**
   - Overlay control plane and data plane timelines
   - Look for temporal alignment:
     - Control plane change → Data plane degradation (cause)
     - Data plane degradation → Control plane retry (effect)
     - Simultaneous issues (common root cause)
   - Calculate lag time between events

5. **Build Incident Timeline**
   - Create unified chronological view
   - Mark each event with source (Control/Data Plane)
   - Indicate impact level at each point
   - Identify the trigger event

6. **Determine Causality**
   - Evaluate correlation vs causation
   - Consider external factors (platform issues, dependencies)
   - Check if similar pattern in other subscriptions/regions
   - Assess confidence level in root cause

7. **Document Findings**
   - Create structured incident summary
   - List contributing factors
   - Recommend mitigation and prevention

## OUTPUT FORMAT

Present findings as:

### Incident Correlation Report

**Incident:** [ID or description]
**Time Window:** [Start] to [End]
**Subscription:** [ID]

### Timeline

```
[T-30m] ─── Control Plane: Deployment initiated
            │
[T-15m] ─── Control Plane: Deployment completed ✓
            │
[T-10m] ─── Data Plane: P95 latency spike detected
            │              └─ 2x normal baseline
[T-5m]  ─── Data Plane: Error rate elevated
            │              └─ 5% (threshold: ${config:incident.kusto.thresholds.failure_rate_warning}%)
[T-0]   ─── Customer: Incident reported
```

### Correlation Analysis

**Primary Cause:** [Deployment / Configuration / External / Unknown]

**Evidence:**
1. [Correlation point 1 with timestamps]
2. [Correlation point 2 with timestamps]

**Confidence:** [High / Medium / Low]

### Impact Assessment

| Metric | Before | During | After |
|--------|--------|--------|-------|
| P95 Latency | ... | ... | ... |
| Error Rate | ... | ... | ... |
| Throughput | ... | ... | ... |

### Recommended Actions

**Immediate:**
1. [Rollback / Mitigation step]

**Follow-up:**
1. [RCA investigation]
2. [Prevention measures]

## IcM MCP INTEGRATION

> **LIMITATIONS:** IcM MCP cannot do time-range or region-based searches

**What IcM MCP CAN do:**
- `search_incidents_by_owning_team_id` - Search by team ID (DeID: 111744)
- `get_incident_details_by_id` - Get details for known incident
- `get_similar_incidents` - Find related incidents (requires incident ID)
- `get_impacted_services_regions_clouds` - Get affected regions

**What IcM MCP CANNOT do:**
- Time-range queries ("incidents between 17:00-18:00 UTC")
- Region-specific queries ("all East US incidents")
- Keyword/text searches ("AI model timeout")
- Cross-team correlation

**Workarounds:**
1. Use IcM portal directly for time-based searches
2. Check Azure Service Health for regional incidents
3. Query upstream team's Kusto directly if accessible

## IMPORTANT

- Correlation does not imply causation - look for supporting evidence
- Consider external factors (Azure platform issues, dependencies)
- Check if similar patterns occurred in other subscriptions/regions
- Document confidence level in root cause determination
- Refer to TROUBLESHOOTING TIPS in both toolkits for common patterns

