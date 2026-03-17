# Data Plane Investigation Toolkit (Geneva/K8s)

> **Schema:** Geneva Log table with Kubernetes metadata  
> **Tables:** `Log`, `KubernetesContainers`

## Purpose

Generic DRI toolkit for investigating high latency incidents in K8s-based services using Geneva telemetry.

**Cluster:** `${config:incident.kusto.clusters.telemetry.cluster}`  
**Database:** `${config:incident.kusto.clusters.telemetry.database}`

SERVICE-SPECIFIC PATTERNS: Check for `data-plane-toolkit-geneva.*.md` files in this folder for service-specific query patterns (e.g., internal service breakdown, dependency analysis, batch jobs). Copy and adapt for your service.

---

## Quick Start Guide

USE THIS TOOLKIT FOR:
- API latency spikes and degradation
- Infrastructure issues (pod/node hotspots)
- Regional and temporal patterns
- Trace/correlation ID lookups

MCP SUPERPOWER:
Use Azure MCP Server for Azure Data Explorer (`kusto_query` tool) to execute these queries directly. Ask:
- "Analyze high latency in weu001 region for Oct 4, 22:10-23:10 UTC"
- "Run STEP 1 queries for IcM #12345"

PERFORMANCE TIP:
Run queries SELECTIVELY based on symptoms. Don't execute all at once.
- GOOD: "Run STEP 1 queries" (3-4 queries, ~1-2 min)
- AVOID: "Run all queries" (may timeout)

TIME RANGE GUIDANCE:
| Scenario | Start With | Expand To | Notes |
|----------|------------|-----------|-------|
| Active IcM | `ago(2h)` | `ago(6h)` | Use IcM DataStartTime/DataEndTime |
| Historical RCA | IcM window +/- 30min | +/- 2 hours | Look for pre-incident patterns |
| Pattern Hunt | `ago(24h)` | `ago(7d)` | Use STEP 5 queries |
| Specific Spike | `between(start..end)` | +/- 15min | Precise window for deep dive |

THRESHOLD REFERENCE (defaults - adjust for your service SLA):
| Level | P95 | P99 | Extreme Rate | Notes |
|-------|-----|-----|--------------|-------|
| Normal | < 100ms | < 200ms | < 0.01% | Baseline |
| Elevated | 100-500ms | 200-1000ms | 0.01-0.1% | Monitor |
| High | > 500ms | > 1000ms | > 0.1% | Investigate |
| Catastrophic | - | - | > 1.0% | Incident |

Override in octane.yaml: `incident.kusto.thresholds.p95_normal_ms`, etc.

TABLE REFERENCE:
- `Log` - Backend application logs (HttpLoggingHandler, controllers)
- `KubernetesContainers` - Nginx ingress controller logs (network/routing layer)

---

## Investigation Workflow

1. **STEP 1: SCOPE** - Find incident window, affected regions
2. **STEP 2: ERRORS** - Error correlation with latency
3. **STEP 3: INFRASTRUCTURE** - Pod/node hotspots, ingress layer
4. **STEP 4: DEEP DIVE** - Trace/correlation ID investigation
5. **STEP 5: PATTERNS** - Daily/hourly/regional outlier analysis

---

## STEP 1: Scope the Investigation

> START HERE when you have an IcM/alert

### 1.1 - Primary DRI Metric (Latency Overview)

Update: time range

```kql
Log
| where PreciseTimeStamp > ago(12h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 0
| summarize
    AvgLatencyMs = avg(latency_ms),
    P95LatencyMs = percentile(latency_ms, 95),
    P99LatencyMs = percentile(latency_ms, 99),
    RequestCount = count()
  by bin(PreciseTimeStamp, 30m), env_cloud_env
| order by PreciseTimeStamp desc
| render timechart
```

### 1.2 - Incident Spike Detection

Update: time range, latency threshold (default 1000ms)

```kql
// THRESHOLD: 1000ms is a reasonable default for "high latency"
// Adjust based on your service baseline or use config threshold:
// ${config:incident.kusto.thresholds.p95_elevated_ms}
Log
| where PreciseTimeStamp > ago(24h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 1000  // Adjust: your service's elevated threshold
| summarize
    HighLatencyCount = count(),
    MaxLatencyMs = max(latency_ms),
    AvgHighLatencyMs = avg(latency_ms)
  by bin(PreciseTimeStamp, 30m), env_cloud_env
| where HighLatencyCount > 3  // Filter for significant incident volumes
| order by MaxLatencyMs desc
```

### 1.3 - Regional Latency Hotspots

Update: time range

```kql
// THRESHOLDS: 100ms (elevated), 1000ms (high) are reasonable defaults
// Adjust based on your service SLA
Log
| where PreciseTimeStamp > ago(24h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 100  // Adjust: filter for elevated latency
| summarize
    RequestCount = count(),
    AvgLatencyMs = avg(latency_ms),
    P95LatencyMs = percentile(latency_ms, 95),
    MaxLatencyMs = max(latency_ms),
    HighLatencyCount = countif(latency_ms > 1000)
  by bin(PreciseTimeStamp, 1h), env_cloud_env
| extend HighLatencyRate = round(100.0 * HighLatencyCount / RequestCount, 1)
| where HighLatencyRate > 5  // Show only periods with >5% high latency
| order by PreciseTimeStamp desc, AvgLatencyMs desc
```

---

## STEP 2: Error Correlation

> Correlate errors with latency categories

### 2.1 - Error Correlation with High Latency

Update: time range

```kql
// LATENCY CATEGORIES: Adjust thresholds based on your service SLA
// Config reference: ${config:incident.kusto.thresholds.p95_*}
Log
| where PreciseTimeStamp > ago(6h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| extend latency_category = case(
    latency_ms > 5000, "Critical (>5s)",   // Adjust: critical threshold
    latency_ms > 2000, "High (2-5s)",      // Adjust: high threshold  
    latency_ms > 1000, "Elevated (1-2s)",  // Adjust: elevated threshold
    "Normal (<1s)"
  )
| summarize
    RequestCount = count(),
    AvgLatencyMs = avg(latency_ms),
    ErrorRate = round(100.0 * countif(httpStatusCode >= 400) / count(), 2)
  by latency_category, env_cloud_env
| order by AvgLatencyMs desc
```

---

## STEP 3: Infrastructure Analysis

> Identify pod/node level issues and ingress layer problems

### 3.1 - Nginx Ingress Latency Analysis

Update: time range, domain filter

```kql
KubernetesContainers
| where PreciseTimeStamp > ago(6h)
| where ContainerName == "controller"
| where NamespaceName == "nginx"
| where log contains "${config:incident.kusto.clusters.telemetry.service_domain}"  // e.g., "myservice.azure.com"
| parse log with * "host: " urlname " service_name: " servicename " status: " code: int " request_method: " requestMethod " request_time: " request_time: decimal " upstream: " server
| where isnotnull(request_time)
| extend latency_ms = request_time * 1000
| summarize
    RequestCount = count(),
    AvgLatencyMs = avg(latency_ms),
    P95LatencyMs = percentile(latency_ms, 95),
    P99LatencyMs = percentile(latency_ms, 99),
    MaxLatencyMs = max(latency_ms),
    HighLatencyCount = countif(latency_ms > 1000)
  by bin(PreciseTimeStamp, 30m), Role
| extend HighLatencyRate = round(100.0 * HighLatencyCount / RequestCount, 2)
| where HighLatencyRate > 1
| order by PreciseTimeStamp desc, P99LatencyMs desc
```

### 3.2 - Pod/Node Latency Hotspots

Update: time range, domain filter

```kql
KubernetesContainers
| where PreciseTimeStamp > ago(6h)
| where ContainerName == "controller"
| where NamespaceName == "nginx"
| where log contains "${config:incident.kusto.clusters.telemetry.service_domain}"  // e.g., "myservice.azure.com"
| parse log with * "host: " urlname " service_name: " servicename " status: " code: int " request_method: " requestMethod " request_time: " request_time: decimal " upstream: " server
| where isnotnull(request_time)
| extend latency_ms = request_time * 1000
| where latency_ms > 500
| summarize
    RequestCount = count(),
    AvgLatencyMs = avg(latency_ms),
    P95LatencyMs = percentile(latency_ms, 95),
    MaxLatencyMs = max(latency_ms),
    ExtremeCount = countif(latency_ms > 5000),
    SampleStatusCodes = make_set(code, 5)
  by PodName, Node, Role
| extend ExtremeRate = round(100.0 * ExtremeCount / RequestCount, 2)
| where RequestCount > 10
| order by ExtremeRate desc, P95LatencyMs desc
| take 20
```

---

## STEP 4: Deep Dive Investigation

> Trace and correlation ID lookups

### 4.1 - Trace ID Lookup

Update: time range, trace_id

```kql
Log
| where PreciseTimeStamp > ago(2d)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_dt_traceId == "<trace_id>"  // UPDATE: from alert or correlation
| project PreciseTimeStamp, name, body, httpStatusCode, env_cloud_env
| order by PreciseTimeStamp asc
| take 1000
```

### 4.2 - Correlation ID Lookup

Update: time range, correlation_id

```kql
Log
| where PreciseTimeStamp > ago(2d)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_app_name =~ "${config:incident.kusto.clusters.telemetry.app_name}"
| where correlationId == "<correlation_id>"  // UPDATE: from alert or logs
| project PreciseTimeStamp, name, correlationId, env_dt_traceId, env_ex_msg, env_ex_type, body, httpStatusCode
| order by PreciseTimeStamp asc
| take 1000
```

### 4.3 - Cross-Cluster Correlation (clientRequestId)

> Use clientRequestId from control-plane-toolkit query 4.4 to find matching data plane request

Update: time range, client_request_id

```kql
// clientRequestId from RPaaS = x-ms-client-request-id header in data plane
// More reliable than timestamp+path under high traffic
Log
| where PreciseTimeStamp > ago(6h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name contains "RequestContextMiddleware"
| where body contains "<client_request_id>"  // UPDATE: from control-plane query 4.4
| extend props = parse_json(env_properties)
| project PreciseTimeStamp, env_cloud_env, TraceId = tostring(props.TraceId), 
          headerName = tostring(props.headerName), headerValue = tostring(props.headerValue)
| where headerName == "x-ms-client-request-id"
```

---

## STEP 5: Pattern Analysis

> Identify recurring issues and temporal patterns

### 5.1 - Daily Extreme Outlier Comparison (30-day)

```kql
Log
| where PreciseTimeStamp > ago(30d)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 0
| extend
    DayOfWeek = dayofweek(PreciseTimeStamp),
    DayName = case(
        dayofweek(PreciseTimeStamp) == 0d, "Sunday",
        dayofweek(PreciseTimeStamp) == 1d, "Monday",
        dayofweek(PreciseTimeStamp) == 2d, "Tuesday",
        dayofweek(PreciseTimeStamp) == 3d, "Wednesday",
        dayofweek(PreciseTimeStamp) == 4d, "Thursday",
        dayofweek(PreciseTimeStamp) == 5d, "Friday",
        dayofweek(PreciseTimeStamp) == 6d, "Saturday",
        "Unknown"
    )
| summarize
    TotalRequests = count(),
    ExtremeCalls_30s = countif(latency_ms > 30000),
    ExtremeCalls_10s = countif(latency_ms > 10000),
    ExtremeCalls_5s = countif(latency_ms > 5000),
    ExtremeRate_30s = round(100.0 * countif(latency_ms > 30000) / count(), 4),
    ExtremeRate_10s = round(100.0 * countif(latency_ms > 10000) / count(), 4),
    MaxLatencyMs = max(latency_ms),
    P99_9LatencyMs = percentile(latency_ms, 99.9)
  by DayName, DayOfWeek
| order by ExtremeRate_30s desc, DayOfWeek asc
```

### 5.2 - Hourly Extreme Outlier Tracking

Update: target day (0=Sunday, 1=Monday, etc.)

```kql
Log
| where PreciseTimeStamp > ago(30d)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 0
| where dayofweek(PreciseTimeStamp) == 0d  // UPDATE: 0=Sunday, 1=Monday, etc.
| extend HourUTC = hourofday(PreciseTimeStamp)
| summarize
    RequestCount = count(),
    AvgLatencyMs = avg(latency_ms),
    P95LatencyMs = percentile(latency_ms, 95),
    MaxLatencyMs = max(latency_ms),
    ExtremeCalls_30s = countif(latency_ms > 30000),
    ExtremeCalls_10s = countif(latency_ms > 10000),
    ExtremeRate_30s = round(100.0 * countif(latency_ms > 30000) / count(), 4),
    ExtremeRate_10s = round(100.0 * countif(latency_ms > 10000) / count(), 4)
  by HourUTC, env_cloud_env
| order by HourUTC asc, ExtremeRate_30s desc
```

### 5.3 - Regional Extreme Outlier Comparison

```kql
Log
| where PreciseTimeStamp > ago(30d)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 0
| summarize
    TotalRequests = count(),
    ExtremeCalls_30s = countif(latency_ms > 30000),
    ExtremeCalls_20s = countif(latency_ms > 20000),
    ExtremeRate_30s = round(100.0 * countif(latency_ms > 30000) / count(), 4),
    ExtremeRate_20s = round(100.0 * countif(latency_ms > 20000) / count(), 4),
    MaxLatencyMs = max(latency_ms),
    P99_9LatencyMs = percentile(latency_ms, 99.9)
  by env_cloud_env
| order by ExtremeRate_30s desc
```

---

## STEP 6: Pod Restart Investigation

> For pod restart alerts

### 6.1 - Pod Restart Root Cause Analysis

Update: restart_time, pod_name

```kql
// Extract pod name and restart timestamp from IcM KubePodContainerRestart alert
KubernetesContainers
| where PreciseTimeStamp between(datetime_add('hour', -1, datetime("<restart_time>"))..datetime("<restart_time>"))  // UPDATE: from IcM
| where PodName == "<pod_name>"  // UPDATE: from IcM alert
| where NamespaceName == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| project PreciseTimeStamp, PodName, ContainerName, RoleInstance, Node, log
| order by PreciseTimeStamp asc
| take 500
```

---

## Troubleshooting Tips

### Latency Investigation
1. Check if latency spike correlates with throughput increase
2. Look for specific pods/nodes driving high P99
3. Compare against baseline from same time yesterday/last week

### Infrastructure Investigation
1. Check for pod restarts correlating with latency spikes
2. Look for node-level patterns (specific nodes consistently slow)
3. Check regional distribution for geo-specific issues
4. Consider scaling if throughput correlates with latency

### Region Reference
- `cny001` - Canary region (pre-production)
- `eus001`, `eus2001` - East US (production)
- `wus2001`, `wus3001` - West US (production)
- `weu001` - West Europe (production)
- `uks001` - UK South (production)
- `scus001` - South Central US (production)
- `cac001` - Canada Central (production)
