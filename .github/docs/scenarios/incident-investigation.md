# Incident Investigation Scenario

## Overview

The Incident Investigation scenario provides AI-guided workflows for investigating Azure service incidents using KQL toolkits. Engineers can investigate control plane and data plane issues without deep Kusto expertise.

## What's Included

### Prompts
- **Octane.Investigate.ControlPlane** - Investigate ARM/RPaaS operations, deployments, and resource provisioning issues
- **Octane.Investigate.DataPlane** - Investigate service API latency, exceptions, and throughput issues
- **Octane.Investigate.Correlation** - Correlate incidents across control plane and data plane

### Templates
- **control-plane-toolkit.md** - KQL patterns for ARM/RPaaS investigation
- **data-plane-toolkit-geneva.md** - Generic KQL patterns for Geneva Log table with K8s metadata
- **data-plane-toolkit-geneva.deid.md** - DeID-specific example patterns (service breakdown, batch jobs, AI model failures)

### MCP Servers Required
- **azure-mcp** - For executing Kusto queries and accessing Azure resource context

## Prerequisites

- Azure MCP configured with Kusto access
- Service-specific cluster configuration in `octane.yaml`
- Understanding of your service's telemetry tables

## Configuration

Configure your service's Kusto clusters in `.config/octane.yaml`:

```yaml
incident:
  kusto:
    clusters:
      rpaas:  # Control plane (ARM/RPaaS operations)
        cluster: "https://rpsaas.kusto.windows.net"
        database: "RPaaSProd"
        provider_namespace: ""  # REQUIRED: e.g., "Microsoft.HealthDataAIServices"
      telemetry:  # Data plane (service telemetry)
        cluster: ""    # REQUIRED: your telemetry cluster
        database: ""   # REQUIRED: your telemetry database
    thresholds:
      p95_normal_ms: 5000
      p95_elevated_ms: 15000
      failure_rate_warning: 1
```

## Quick Start Examples

### Control Plane Investigation
```
/Octane.Investigate.ControlPlane IcM 123456789
/Octane.Investigate.ControlPlane check ARM failures in the last 6 hours
/Octane.Investigate.ControlPlane why are DELETE operations returning 409?
```

### Data Plane Investigation
```
/Octane.Investigate.DataPlane latency spike in weu001 around 10:00 UTC
/Octane.Investigate.DataPlane events recap for the last 24 hours
/Octane.Investigate.DataPlane trace ID abc123-def456
```

### Cross-Plane Correlation
```
/Octane.Investigate.Correlation what caused the 500 errors on Dec 15 at 14:30 UTC?
/Octane.Investigate.Correlation build timeline for subscription 12345678-1234-1234-1234-123456789abc
/Octane.Investigate.Correlation correlate Tagger failure with control plane impact
```

## Example Workflows

### Investigate Control Plane Issues

The workflow will:
- Query RPaaS for deployment operations
- Identify failed ARM operations
- Correlate with async operation status
- Suggest mitigation steps

### Investigate Data Plane Latency

The workflow will:
- Analyze P50/P95/P99 latency percentiles
- Identify slow operations and their patterns
- Check for correlated infrastructure issues
- Recommend optimization steps

### Cross-Plane Correlation

The workflow will:
- Check control plane for recent deployments
- Check data plane for latency spikes
- Identify temporal correlation
- Build incident timeline

## Use Cases

- Investigating customer-reported incidents
- Analyzing deployment failures
- Debugging latency spikes
- Correlating control plane and data plane issues
- Building incident timelines for RCA

## Difficulty

**Intermediate** - Requires basic understanding of Azure telemetry concepts

## Tags

`incident` `kusto` `kql` `investigation` `telemetry` `operations` `troubleshooting`
