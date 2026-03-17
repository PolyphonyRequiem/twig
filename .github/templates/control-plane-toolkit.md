# Control Plane Investigation Toolkit

> **Schema:** RPaaS HttpIncomingRequests / HttpOutgoingRequests tables

## Purpose

Investigate Resource Provider (RP) and Azure Resource Manager (ARM) operations for resource provisioning, deletion, and management issues.

**Cluster:** `${config:incident.kusto.clusters.rpaas.cluster}`  
**Database:** `${config:incident.kusto.clusters.rpaas.database}`

---

## Quick Start Guide

USE THIS TOOLKIT FOR:
- Resource creation/update/deletion failures
- ARM operation timeouts (504 Gateway Timeout)
- RPaaS to UserRP communication issues
- Resource provisioning stuck or hanging
- Control plane latency investigation

MCP SUPERPOWER:
Use Azure MCP Server for Azure Data Explorer (`kusto_query` tool) to execute these queries directly. Ask:
- "Investigate ARM DELETE failures in eus001 for Oct 7, 10:00-11:00 UTC"
- "Run STEP 1 queries for resource deletion failure"

PERFORMANCE TIP:
Run queries SELECTIVELY based on symptoms.
- GOOD: "Run STEP 1 queries for resource deletion failure" (3 queries, ~30s)
- GOOD: "Check RPaaS COMMUNICATION for slow operations" (2 queries, ~15s)
- AVOID: "Run all control plane queries" (12 queries, 5-10 min, may timeout)

THRESHOLD REFERENCE:
| Level | P95 | P99 | Failure Rate |
|-------|-----|-----|--------------|
| Normal | < 5s | < 10s | < 1% |
| Elevated | 5-15s | 10-30s | 1-5% |
| High | > 15s | > 30s | > 5% |
| Critical | Timeouts (504) | - | > 10% |

TABLE REFERENCE:
- `HttpIncomingRequests` - ARM → RPaaS gateway operations (client-facing control plane)
- `HttpOutgoingRequests` - RPaaS → UserRP communication (backend control plane)

---

## Investigation Workflow

1. **STEP 1: FAILURES** - Identify operation failures (DELETE, PUT, PATCH)
2. **STEP 2: RPAAS** - Analyze RPaaS → UserRP communication
3. **STEP 3: LATENCY** - Control plane operation timing and timeouts
4. **STEP 4: DEEP DIVE** - Resource-specific timeline investigation

---

## STEP 1: Operation Failure Detection

> START HERE when investigating resource provisioning issues

### 1.1 - ARM Operation Failure Summary

Update: time range

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
| where TaskName in ("HttpIncomingRequestEndWithClientFailure", "HttpIncomingRequestEndWithServerFailure")
| extend operation_type = case(
    operationName contains "DELETE", "Delete",
    operationName contains "PUT", "Create/Update",
    operationName contains "PATCH", "Patch",
    operationName contains "GET", "Get",
    "Other"
  )
| summarize
    FailureCount = count(),
    P95DurationMs = percentile(durationInMilliseconds, 95),
    StatusCodes = make_set(httpStatusCode, 5),
    SampleOperations = make_set(operationName, 5)
  by bin(PreciseTimeStamp, 1h), operation_type, _RoleLocation
| where FailureCount > 0
| order by PreciseTimeStamp desc, FailureCount desc
```

### 1.2 - Deletion Operation Failures

Update: time range, resource type filter

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
| where httpStatusCode != 404  // Exclude not found (expected for already deleted)
| where TaskName in ("HttpIncomingRequestEndWithClientFailure", "HttpIncomingRequestEndWithServerFailure")
| where failureCause == "gateway"
| where operationName contains "DELETE"
// Optional: filter by resource type
// | where operationName contains "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES", "ACCOUNTS"
| summarize
    FailureCount = count(),
    AvgDurationMs = avg(durationInMilliseconds),
    MaxDurationMs = max(durationInMilliseconds),
    SampleOperations = make_set(operationName, 3)
  by bin(PreciseTimeStamp, 1h), _RoleLocation, httpStatusCode
| where FailureCount > 0
| order by PreciseTimeStamp desc, FailureCount desc
```

### 1.3 - Resource Provisioning Failures

Update: time range, resource type filter

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
| where operationName contains "PUT"
// Optional: filter by resource type
// | where operationName contains "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES", "ACCOUNTS"
| where httpStatusCode >= 400
| summarize
    FailureCount = count(),
    Client4xxCount = countif(httpStatusCode between (400..499)),
    Server5xxCount = countif(httpStatusCode >= 500),
    AvgDurationMs = avg(durationInMilliseconds),
    SampleOperations = make_set(operationName, 5)
  by bin(PreciseTimeStamp, 30m), httpStatusCode, _RoleLocation
| extend FailureType = iff(httpStatusCode >= 500, "Server Error", "Client Error")
| order by PreciseTimeStamp desc, FailureCount desc
```

---

## STEP 2: RPaaS Communication Analysis

> Analyze communication between RPaaS gateway and your UserRP

### 2.1 - RPaaS to UserRP Request Tracking

Update: time range, targetUri filter

```kql
HttpOutgoingRequests
| where TIMESTAMP >= ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
// Optional: filter by resource type and UserRP URI
// | where resourceTypeName =~ "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
// | where targetUri contains "<userRP>"          // e.g., "deidrpapi"
| summarize
    RequestCount = count(),
    FailureCount = countif(httpStatusCode >= 400),
    FailureRate = round(100.0 * countif(httpStatusCode >= 400) / count(), 2),
    AvgDurationMs = avg(durationInMilliseconds),
    P95DurationMs = percentile(durationInMilliseconds, 95),
    P99DurationMs = percentile(durationInMilliseconds, 99),
    MaxDurationMs = max(durationInMilliseconds),
    SampleErrors = make_set_if(exceptionMessage, isnotempty(exceptionMessage), 5)
  by bin(PreciseTimeStamp, 15m), operationName, httpStatusCode
| where FailureCount > 0 or P95DurationMs > 5000  // Show failures or slow operations (>5s)
| order by PreciseTimeStamp desc, FailureRate desc
```

### 2.2 - RPaaS Request Latency Analysis

Update: time range, targetUri filter

```kql
HttpOutgoingRequests
| where TIMESTAMP >= ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
// Optional: filter by resource type and UserRP URI
// | where resourceTypeName =~ "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
// | where targetUri contains "<userRP>"          // e.g., "deidrpapi"
| extend operation_type = case(
    operationName contains "Delete", "Delete",
    operationName contains "Get", "Get",
    operationName contains "Put", "Create/Update",
    operationName contains "Patch", "Patch",
    "Other"
  )
| where isnotnull(durationInMilliseconds) and durationInMilliseconds >= 0
| summarize
    RequestCount = count(),
    AvgDurationMs = round(avg(durationInMilliseconds), 2),
    MinDurationMs = min(durationInMilliseconds),
    P50DurationMs = percentile(durationInMilliseconds, 50),
    P95DurationMs = percentile(durationInMilliseconds, 95),
    P99DurationMs = percentile(durationInMilliseconds, 99),
    MaxDurationMs = max(durationInMilliseconds),
    TimeoutCount = countif(durationInMilliseconds >= 60000),
    SlowRequestCount = countif(durationInMilliseconds > 10000)
  by bin(PreciseTimeStamp, 30m), operation_type, hostName
| extend
    SlowRequestRate = round(100.0 * SlowRequestCount / RequestCount, 2),
    TimeoutRate = round(100.0 * TimeoutCount / RequestCount, 2)
| where RequestCount >= 3
| where SlowRequestRate > 5 or P95DurationMs > 5000
| order by PreciseTimeStamp desc, P95DurationMs desc
```

### 2.3 - RPaaS Failure Correlation

Update: time range, targetUri filter

```kql
// NOTE: Occasional 404s are expected (e.g., checking non-existent resources, idempotent deletes)
//       Focus on 5xx errors and high failure rates
HttpOutgoingRequests
| where TIMESTAMP >= ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
// Optional: filter by resource type and UserRP URI
// | where resourceTypeName =~ "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
// | where targetUri contains "<userRP>"          // e.g., "deidrpapi"
| summarize
    TotalCount = count(),
    FailureCount = countif(httpStatusCode >= 400),
    SuccessCount = countif(httpStatusCode < 400),
    FailureRate = round(100.0 * countif(httpStatusCode >= 400) / count(), 2),
    SampleCorrelationIds = make_set_if(correlationId, httpStatusCode >= 400, 5),
    ExceptionMessages = make_set_if(exceptionMessage, isnotempty(exceptionMessage), 5)
  by httpStatusCode, operationName, hostName
| where FailureCount > 0
| order by FailureCount desc
```

---

## STEP 3: Control Plane Latency Investigation

> Analyze operation timing and detect timeouts

### 3.1 - Control Plane Operation Latency

Update: time range

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(6h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
// Optional: filter by resource type
// | where operationName contains "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
| extend operation_type = case(
    operationName contains "DELETE", "Delete",
    operationName contains "PUT", "Create/Update",
    operationName contains "PATCH", "Patch",
    operationName contains "GET", "Get",
    "Other"
  )
| summarize
    RequestCount = count(),
    AvgDurationMs = avg(durationInMilliseconds),
    P50DurationMs = percentile(durationInMilliseconds, 50),
    P95DurationMs = percentile(durationInMilliseconds, 95),
    P99DurationMs = percentile(durationInMilliseconds, 99),
    MaxDurationMs = max(durationInMilliseconds),
    TimeoutCount = countif(httpStatusCode == 504),
    FailureCount = countif(httpStatusCode >= 400)
  by bin(PreciseTimeStamp, 30m), operation_type, _RoleLocation
| extend
    TimeoutRate = round(100.0 * TimeoutCount / RequestCount, 2),
    FailureRate = round(100.0 * FailureCount / RequestCount, 2)
| order by PreciseTimeStamp desc, P95DurationMs desc
```

### 3.2 - Operation Timeout Detection (504)

Update: time range

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
| where httpStatusCode == 504  // Gateway Timeout
// Optional: filter by resource type
// | where operationName contains "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
| extend operation_type = case(
    operationName contains "DELETE", "Delete",
    operationName contains "PUT", "Create/Update",
    operationName contains "PATCH", "Patch",
    "Other"
  )
| summarize
    TimeoutCount = count(),
    AvgAttemptedDurationMs = avg(durationInMilliseconds),
    SampleOperations = make_set(operationName, 5),
    SampleCorrelationIds = make_set(correlationId, 5)
  by bin(PreciseTimeStamp, 1h), operation_type, _RoleLocation
| order by PreciseTimeStamp desc, TimeoutCount desc
```

### 3.3 - Regional Control Plane Hotspots

Update: time range

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(24h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
// Optional: filter by resource type
// | where operationName contains "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
| summarize
    TotalRequests = count(),
    FailureCount = countif(httpStatusCode >= 400),
    TimeoutCount = countif(httpStatusCode == 504),
    AvgDurationMs = avg(durationInMilliseconds),
    P95DurationMs = percentile(durationInMilliseconds, 95),
    MaxDurationMs = max(durationInMilliseconds)
  by _RoleLocation
| extend
    FailureRate = round(100.0 * FailureCount / TotalRequests, 2),
    TimeoutRate = round(100.0 * TimeoutCount / TotalRequests, 2)
| where FailureRate > 1 or TimeoutRate > 0.5 or P95DurationMs > 10000
| order by FailureRate desc, TimeoutRate desc
```

---

## STEP 4: Resource-Specific Investigation

> Deep dive into specific resource operations

### 4.1 - Resource Operation Timeline

Update: time range, resource_name

```kql
// NOTE: operationName is truncated at 120 chars, so use partial matches
// Example: "PRIVATEENDPOINTCONNECTIONPROXIES" or "Test" (partial resource name)
HttpIncomingRequests
| where PreciseTimeStamp > ago(7d)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
| where operationName contains "<resource_name>"  // UPDATE: Partial name or substring
| extend operation_type = case(
    operationName contains "DELETE", "Delete",
    operationName contains "PUT", "Create/Update",
    operationName contains "PATCH", "Patch",
    operationName contains "GET", "Get",
    "Other"
  )
| project
    PreciseTimeStamp,
    operation_type,
    operationName,
    httpStatusCode,
    durationInMilliseconds,
    failureCause,
    correlationId,
    _RoleLocation
| order by PreciseTimeStamp asc
| take 500
```

### 4.2 - Cross-Reference RPaaS Operations

Update: time range, resource_name

```kql
// Correlate with HttpIncomingRequests using correlationId
HttpOutgoingRequests
| where TIMESTAMP >= ago(7d)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
// Optional: filter by resource type
// | where resourceTypeName =~ "<RESOURCE_TYPE>"  // e.g., "DEIDSERVICES"
| where targetUri contains "<resource_name>"  // UPDATE: resource name to investigate
| project
    PreciseTimeStamp,
    operationName,
    httpStatusCode,
    durationInMilliseconds,
    exceptionMessage,
    correlationId,
    targetUri,
    hostName
| order by PreciseTimeStamp asc
| take 500
```

### 4.3 - Operations by Correlation ID

Update: correlation_id

```kql
HttpIncomingRequests
| where PreciseTimeStamp > ago(7d)
| where correlationId == "<correlation_id>"
| project
    PreciseTimeStamp,
    operationName,
    httpStatusCode,
    durationInMilliseconds,
    failureCause,
    _RoleLocation
| order by PreciseTimeStamp asc
```

### 4.4 - Cross-Cluster Correlation (clientRequestId)

> Use clientRequestId to trace requests from control plane to data plane

Update: time range, filters

```kql
// clientRequestId matches x-ms-client-request-id header in data plane logs
// Use this for precise cross-cluster correlation under high traffic
HttpOutgoingRequests
| where TIMESTAMP > ago(6h)
| where providerNamespace =~ "${config:incident.kusto.clusters.rpaas.provider_namespace}"
| where httpStatusCode >= 400 or durationInMilliseconds > 10000
| project PreciseTimeStamp, correlationId, clientRequestId, targetUri, httpStatusCode, durationInMilliseconds
| order by PreciseTimeStamp desc
| take 50
```

---

## Troubleshooting Tips

### Common Failure Patterns

**1. 504 Gateway Timeout**
- ARM operation took > 3 minutes
- Check CONTROL PLANE OPERATION LATENCY for slow operations
- May indicate backend UserRP performance issues
- Cross-reference with data-plane-toolkit-geneva.md

**2. 500 Internal Server Error**
- Backend UserRP error during processing
- Check RPAAS FAILURE CORRELATION for error details
- Review backend logs in data plane toolkit

**3. 409 Conflict**
- Resource locked or in transitional state
- Check RESOURCE OPERATION TIMELINE for concurrent operations
- May indicate retry logic or race conditions

**4. High Latency without Failures**
- Check REGIONAL CONTROL PLANE HOTSPOTS for regional issues
- Compare P95/P99 latency across regions
- May indicate Azure service degradation

**5. Stuck Deletions**
- Run DELETION OPERATION FAILURES
- Check for 504 timeouts during deletion
- May require manual cleanup or support escalation

### Integration with Data Plane

1. **Control plane succeeds but resources don't work:**
   → Use data-plane-toolkit-geneva.md to check data plane API

2. **504 timeouts during resource creation:**
   → Check RPAAS REQUEST LATENCY ANALYSIS here
   → Then check NGINX INGRESS LATENCY in data plane toolkit

3. **Pod restarts after successful resource creation:**
   → Use POD RESTART ROOT CAUSE ANALYSIS in data plane toolkit

### Region Reference
- `cny001` - Canary region (pre-production)
- `eus001`, `eus2001` - East US (production)
- `wus2001`, `wus3001` - West US (production)
- `weu001` - West Europe (production)
- `uks001` - UK South (production)
- `scus001` - South Central US (production)
- `cac001` - Canada Central (production)
- `ncus001` - North Central US (production)
