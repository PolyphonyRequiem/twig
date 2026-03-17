<!--
============================================================
EXAMPLE FILE: DeID Service Patterns

This file demonstrates service-specific query patterns.
It is provided as a reference implementation.

FOR DeID TEAM: This works out-of-the-box with your octane.yaml config.

FOR OTHER TEAMS:
  Option 1: Copy and adapt - rename to data-plane-toolkit-geneva.yourservice.md
  Option 2: Delete this file if not applicable

Deleting this file will NOT break any prompts.
============================================================
-->

# DeID Service Patterns (Geneva/K8s Example)

> **EXAMPLE:** DeID-specific query patterns extending [data-plane-toolkit-geneva.md](data-plane-toolkit-geneva.md)  
> **Adapt for your service:** Copy patterns and replace DeID-specific markers with your service's log patterns

## DeID Config Example

When using this toolkit, your `octane.yaml` should have:
```yaml
incident:
  kusto:
    clusters:
      telemetry:
        cluster: "https://resolutetelemetry.westus2.kusto.windows.net"
        database: "Logs"
        k8s_namespace: "deid"
        app_name: "DeIDAPIRole"
        handler_name: "Microsoft.R9.Extensions.HttpClient.Logging.Internal.HttpLoggingHandler"
```

## Service Architecture Context

### Namespace → Plane Mapping

| Namespace | Plane | Purpose | Kusto Cluster |
|-----------|-------|---------|---------------|
| `deidrp` | **Control Plane** | Resource lifecycle (ARM → RPaaS → UserRP) | rpsaas.kusto.windows.net |
| `deid` | **Data Plane** | User data processing (de-identification API) | resolutetelemetry.westus2.kusto.windows.net |

> **Terminology Note:**  
> - **Control Plane** = Resource management (create/delete/update DeID service instances)  
> - **Data Plane** = User data operations (de-identification requests)  
>   
> While control plane operations *affect* the data plane service (e.g., provisioning creates capacity), the distinction is about **intent**: managing resources vs processing user data.

### Processing Components

DeID service has two main processing components:
- **Tagger** - AI entity recognition/tagging
- **Surrogator** - AI entity replacement/surrogation

Logs contain markers: `"Calling tagger"` and `"Calling surrogator"` in RealtimeController logs.

## Time Range Guidance

| Scenario | Start With | Expand To | Notes |
|----------|------------|-----------|-------|
| Active IcM | `ago(2h)` | `ago(6h)` | Use IcM DataStartTime/DataEndTime |
| PHI Tagger spike | `ago(1h)` | Specific 5-10min window | AI model issues are short-lived |
| Pattern Hunt | `ago(24h)` | `ago(7d)` | Use Pattern Analysis section |
| Batch Job | `ago(24h)` | Job duration + 1h | Check job start/end times |

## Known Baseline Patterns
- **Surrogator typically 2x slower than Tagger** - This is normal
- **AI model 424 errors** - Cause 30-40s timeouts (check for DependencyFailedException)
- **PLValidator errors** - Watch for 401/403/500/-1 status codes (auth service, not backend)
- **Weekend patterns** - May show higher extreme rates due to batch job scheduling

---

## PHI Tagger / AI Model Analysis

> Detect AI model timeouts, retry storms, and health check failures

### Polly Retry Storm Detection

> Identify cascading retry patterns that indicate AI model degradation

Update: time range

```kql
Log
| where PreciseTimeStamp > ago(1h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_k8s_pod contains "phitagger"
| where name == "Polly.Retry.IRetryPolicy"
| summarize
    RetryCount = count(),
    UniquePods = dcount(env_k8s_pod)
  by bin(PreciseTimeStamp, 1m)
| where RetryCount > 50  // Normal is near 0
| order by PreciseTimeStamp desc
```

### PHI Tagger Health Check Failures

> Monitor health check status - failures indicate AI model unavailability

Update: time range

```kql
Log
| where PreciseTimeStamp > ago(1h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_k8s_pod contains "phitagger"
| where body contains "HealthCheck" and severityText in ("Error", "Warning")
| summarize
    FailureCount = count(),
    SampleMessages = make_set(substring(body, 0, 200), 3)
  by bin(PreciseTimeStamp, 1m), env_k8s_pod
| where FailureCount > 0
| order by PreciseTimeStamp desc
```

### AI Model Timeout vs Throttling Differentiation

> Distinguish between timeout (TaskCanceledException) and throttling (429) issues

Update: time range

```kql
Log
| where PreciseTimeStamp > ago(1h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_k8s_pod contains "phitagger"
| where isnotempty(env_ex_type) or httpStatusCode == 429
| extend IssueType = case(
    env_ex_type contains "TaskCanceledException", "TIMEOUT",
    env_ex_type contains "DependencyFailedException", "DEPENDENCY FAILED",
    httpStatusCode == 429, "THROTTLED (429)",
    env_ex_type contains "HttpRequestException", "NETWORK ERROR",
    "OTHER"
  )
| summarize
    Count = count(),
    SampleException = any(env_ex_msg)
  by bin(PreciseTimeStamp, 5m), IssueType
| order by PreciseTimeStamp desc, Count desc
```

### AI Model Error Timeline (Detailed)

> Full timeline of AI model errors for root cause analysis

Update: time range (narrow to spike window)

```kql
Log
| where PreciseTimeStamp > ago(15m)  // Narrow to spike window
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_k8s_pod contains "phitagger"
| where severityText in ("Error", "Critical") or isnotempty(env_ex_type)
| project
    PreciseTimeStamp,
    Pod = env_k8s_pod,
    Logger = name,
    Severity = severityText,
    ExceptionType = env_ex_type,
    Message = substring(coalesce(env_ex_msg, body), 0, 150)
| order by PreciseTimeStamp asc
| take 200
```

---

## Service Breakdown Analysis

> Identify which internal service (Tagger vs Surrogator) is causing latency

### Service Breakdown by Component

Update: time range

```kql
let service_a_calls = Log
    | where PreciseTimeStamp > ago(6h)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where name contains "DeIDApi.Api." and name endswith "RealtimeController"
    | where body contains "Calling tagger"
    | project PreciseTimeStamp, correlationId, call_type = "Tagger", env_cloud_env;
let service_b_calls = Log
    | where PreciseTimeStamp > ago(6h)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where name contains "DeIDApi.Api." and name endswith "RealtimeController"
    | where body contains "Calling surrogator"
    | project PreciseTimeStamp, correlationId, call_type = "Surrogator", env_cloud_env;
let http_calls = Log
    | where PreciseTimeStamp > ago(6h)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
    | where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
    | where isnotnull(Duration) and Duration != ""
    | extend latency_ms = toint(Duration)
    | project PreciseTimeStamp, correlationId, latency_ms;
union service_a_calls, service_b_calls
| join kind=inner http_calls on correlationId
| where abs(datetime_diff('second', PreciseTimeStamp1, PreciseTimeStamp)) <= 10
| summarize
    AvgLatencyMs = avg(latency_ms),
    P95LatencyMs = percentile(latency_ms, 95),
    P99LatencyMs = percentile(latency_ms, 99),
    MaxLatencyMs = max(latency_ms),
    RequestCount = count()
  by call_type, env_cloud_env
| order by P99LatencyMs desc
```

### Service Performance with Status

Update: time range

```kql
let service_calls = Log
    | where PreciseTimeStamp > ago(6h)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where name contains "DeIDApi.Api." and name endswith "RealtimeController"
    | where body contains "Calling tagger" or body contains "Calling surrogator"
    | extend target_service = case(
        body contains "Calling tagger", "Tagger",
        body contains "Calling surrogator", "Surrogator",
        "Unknown"
      )
    | project PreciseTimeStamp, correlationId, target_service, env_cloud_env, env_k8s_pod;
Log
| where PreciseTimeStamp > ago(6h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
| where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
| where isnotnull(Duration) and Duration != ""
| extend latency_ms = toint(Duration)
| where latency_ms > 0
| join kind=inner service_calls on correlationId
| where abs(datetime_diff('second', PreciseTimeStamp1, PreciseTimeStamp)) <= 10
| summarize
    RequestCount = count(),
    AvgLatencyMs = round(avg(latency_ms), 1),
    P50LatencyMs = percentile(latency_ms, 50),
    P95LatencyMs = percentile(latency_ms, 95),
    P99LatencyMs = percentile(latency_ms, 99),
    MaxLatencyMs = max(latency_ms),
    UniquePods = dcount(env_k8s_pod)
  by bin(PreciseTimeStamp, 30m), target_service, env_cloud_env
| where RequestCount > 5
| extend PerformanceStatus = case(
    P95LatencyMs > 500, "Critical",
    P95LatencyMs > 200, "Elevated",
    "Normal"
  )
| order by PreciseTimeStamp desc, P95LatencyMs desc
```

---

## Infrastructure Analysis (DeID-Specific)

### PLValidator Authorization Failures

> DeID uses PLValidator for authorization - check for auth failures

Update: time range

```kql
HdaiControlPlaneLog
| where PreciseTimeStamp > ago(12h)
| where env_aks_namespace == "plvalidator"
| where severityText in ("Error", "Critical")
| where HostName contains ".deid."
| summarize
    ErrorCount = count(),
    UniqueHosts = dcount(HostName),
    SampleMessage = any(body)
  by bin(PreciseTimeStamp, 15m), env_region, severityText
| where ErrorCount > 3
| order by PreciseTimeStamp desc, ErrorCount desc
```

### HTTP Endpoint Request Patterns

Update: time range

```kql
Log
| where PreciseTimeStamp > ago(6h)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where name == "Microsoft.R9.Service.Middleware.Http.Logging.HttpLoggingMiddleware"
| extend properties = parse_json(env_properties)
| extend requestPath = tostring(properties.RequestPath)
| where isnotempty(requestPath) and requestPath != ""
| where requestPath contains "deid" or requestPath contains "jobs"
| summarize
    RequestCount = count(),
    ErrorLogs = countif(severityText == "Error" or severityText == "Critical"),
    WarningLogs = countif(severityText == "Warning"),
    UniqueConnectionIds = dcount(tostring(properties.ConnectionId))
  by requestPath, bin(PreciseTimeStamp, 15m), env_cloud_env
| extend
    ErrorRate = round(100.0 * ErrorLogs / RequestCount, 2),
    AvgConnectionsPerMin = round(UniqueConnectionIds / 15.0, 1)
| where RequestCount > 10
| order by PreciseTimeStamp desc, RequestCount desc
```

---

## ICM Deep Dive (DeID-Specific)

### High Latency Request Correlation with Service Breakdown

Update: incident_start, incident_end, target_region, latency_threshold

```kql
// CUSTOMIZE FOR EACH ICM:
// 1. UPDATE TIME RANGE: Use Monitor.DataStartTime/DataEndTime from IcM
// 2. UPDATE REGION: Extract env_cloud_env from IcM MetricData
// 3. UPDATE THRESHOLD: Use IcM P95 value * 2-3 for outlier detection
let incident_start = datetime(2025-10-04 22:10:45Z);  // UPDATE: from IcM
let incident_end = datetime(2025-10-04 23:10:45Z);    // UPDATE: from IcM
let target_region = "weu001";                          // UPDATE: from IcM
let latency_threshold = 4400;                          // UPDATE: IcM P95 * 2
//
let service_a_calls = Log
    | where PreciseTimeStamp between (incident_start..incident_end)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where env_cloud_env == target_region
    | where name contains "DeIDApi.Api." and name endswith "RealtimeController"
    | where body contains "Calling tagger"
    | project PreciseTimeStamp, correlationId, call_type = "Tagger", env_cloud_env;
let service_b_calls = Log
    | where PreciseTimeStamp between (incident_start..incident_end)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where env_cloud_env == target_region
    | where name contains "DeIDApi.Api." and name endswith "RealtimeController"
    | where body contains "Calling surrogator"
    | project PreciseTimeStamp, correlationId, call_type = "Surrogator", env_cloud_env;
let high_latency_http = Log
    | where PreciseTimeStamp between (incident_start..incident_end)
    | where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
    | where env_cloud_env == target_region
    | where name == "${config:incident.kusto.clusters.telemetry.handler_name}"
    | where env_app_name == "${config:incident.kusto.clusters.telemetry.app_name}"
    | where isnotnull(Duration) and Duration != ""
    | extend latency_ms = toint(Duration)
    | where latency_ms > latency_threshold
    | project PreciseTimeStamp, correlationId, latency_ms, env_dt_traceId, httpStatusCode;
union service_a_calls, service_b_calls
| join kind=inner high_latency_http on correlationId
| where abs(datetime_diff('second', PreciseTimeStamp1, PreciseTimeStamp)) <= 10
| project PreciseTimeStamp1, latency_ms, service_type = call_type, env_cloud_env, correlationId, env_dt_traceId, httpStatusCode
| order by latency_ms desc
| take 50
```

### AI Model Dependency Failure Analysis

> DeID calls AI models for inference - detect retry storms and timeouts

Update: time range, trace_id

```kql
Log
| where PreciseTimeStamp > ago(2d)
| where env_k8s_namespace == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where env_dt_traceId == "<trace_id>"  // UPDATE: from correlation query
| order by PreciseTimeStamp asc
| project PreciseTimeStamp, name, body, httpStatusCode, env_cloud_env, env_ex_msg, env_ex_type, httpMethod, httpPath
| extend
    IsPollyRetry = iff(name contains "Polly.Retry", "✓ Retry Attempt", ""),
    IsDependencyFailure = iff(env_ex_type contains "DependencyFailedException", "✗ DEPENDENCY FAILED", ""),
    IsTaskCanceled = iff(env_ex_msg contains "task was canceled" or env_ex_type contains "TaskCanceledException", "✗ TIMEOUT", ""),
    IsInferenceError = iff(env_ex_msg contains "Error occurred during inference", "✗ AI MODEL ERROR", "")
| extend FailureIndicator = strcat(IsPollyRetry, IsDependencyFailure, IsTaskCanceled, IsInferenceError)
| where FailureIndicator != "" or name contains "RealtimeController" or name contains "HttpLoggingHandler"
| project PreciseTimeStamp, name, FailureIndicator, env_ex_msg, env_ex_type, body
| take 1000
```

---

## Batch Job Investigation

> DeID has async batch processing - investigate specific batch jobs

Update: time range, batch_id

```kql
// OPTIMIZED: Filter by batchId BEFORE JSON parsing for faster results
KubernetesContainers
| where PreciseTimeStamp > ago(24h)
| where NamespaceName == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| where ['log'] contains "<batch_id>"  // UPDATE: Pre-filter by batchId
| project PreciseTimeStamp, jsonLog = parse_json(['log']), ContainerName, PodName, Role, Node
| evaluate bag_unpack(jsonLog, 'x_')
| where x_Category !in (
    "Microsoft.Health.DeID.Common.Services.JobDispatcherClientProvider",
    "Microsoft.Health.DeID.Common.Telemetry.MetricsReporter",
    "Microsoft.R9.Extensions.HttpClient.Logging.Internal.HttpLoggingHandler")
| where isnotnull(x_Scopes) and array_length(x_Scopes) >= 2
| extend scopeData = x_Scopes[1]
| where isnotnull(scopeData.batchId)
| extend
    batchId = tostring(scopeData.batchId),
    jobName = tostring(scopeData.jobName),
    jobType = tostring(scopeData.jobType)
| where batchId == "<batch_id>"  // UPDATE: same batchId as above
| project PreciseTimeStamp, ContainerName, PodName, Role, x_Message, jobName, jobType, batchId, x_EventId
| order by PreciseTimeStamp asc
| take 2000
```

---

## DeID-Specific Troubleshooting Tips

### Known Patterns
- **Surrogator typically 2x slower than Tagger** - This is baseline behavior
- **AI model 424 errors** - Cause 30-40s timeouts (check for DependencyFailedException)
- **PLValidator errors** - Watch for 401/403/500/-1 status codes (auth service, not backend latency)
- **Weekend patterns** - May show higher extreme rates due to batch job scheduling

### Common Root Causes
1. **Tagger slow** → Check AI model endpoint health
2. **Surrogator slow** → Check database/storage latency
3. **Both slow** → Infrastructure issue (check nginx, pod restarts)
4. **Intermittent 30-40s spikes** → AI model cold starts or throttling

---

## Pattern Analysis (30-Day Trends)

> Detect recurring issues and catastrophic failure patterns

### Daily Extreme Outlier Comparison

> Identify which days show catastrophic failures (30+ second calls)

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
    ExtremeCalls_20s = countif(latency_ms > 20000),
    ExtremeCalls_10s = countif(latency_ms > 10000),
    ExtremeCalls_5s = countif(latency_ms > 5000),
    ExtremeRate_30s = round(100.0 * countif(latency_ms > 30000) / count(), 4),
    ExtremeRate_20s = round(100.0 * countif(latency_ms > 20000) / count(), 4),
    MaxLatencyMs = max(latency_ms),
    P99_9LatencyMs = percentile(latency_ms, 99.9)
  by DayName, DayOfWeek
| order by ExtremeRate_30s desc, DayOfWeek asc
```

### Hourly Extreme Outlier Tracking

> Hour-by-hour analysis for specific day investigation

Update: dayofweek filter (0=Sunday, 1=Monday, etc.)

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

### Regional Extreme Outlier Comparison

> Which regions show worst catastrophic failure rates?

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

## Pod Restart Investigation

> Investigate logs around pod restart events (from KubePodContainerRestart alerts)

Update: restart_time, pod_name

```kql
KubernetesContainers
| where PreciseTimeStamp between(datetime_add('hour', -1, datetime("<restart_time>"))..datetime("<restart_time>"))  // UPDATE: from IcM
| where PodName == "<pod_name>"  // UPDATE: from IcM alert
| where NamespaceName == "${config:incident.kusto.clusters.telemetry.k8s_namespace}"
| project PreciseTimeStamp, PodName, ContainerName, RoleInstance, Node, log
| order by PreciseTimeStamp asc
| take 500
```
