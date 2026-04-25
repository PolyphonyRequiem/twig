---
description: 'Telemetry implementation rules and privacy constraints for twig CLI.'
applyTo: '**/Telemetry/**'
---

# Telemetry Implementation Instructions

## Architecture

- No SDK dependencies — direct HTTPS POST to Application Insights ingestion API
- Opt-in via environment variables: `TWIG_TELEMETRY_ENDPOINT` (URL) + `TWIG_TELEMETRY_KEY` (ikey)
- Fire-and-forget with `Task.Run` — never awaited on the command path
- All telemetry code must be AOT-compatible (no reflection, source-gen JSON only)

## Allowed Telemetry Properties

Only these property patterns are permitted in telemetry events:

```
command         — the twig command name (string)
duration_ms     — wall-clock milliseconds (long)
exit_code       — 0 or 1 (int)
output_format   — "human", "json", "minimal" (string)
version         — twig version string (string)
os_platform     — "win", "linux", "osx" (string)
had_*           — boolean flags (bool)
is_*            — boolean flags (bool)
*_count         — numeric counts, no identifiers (int)
*_changed       — boolean diff flags (bool)
```

## Prohibited Properties

Any telemetry key matching these patterns must be rejected at compile-time or test-time:

- `*org*`, `*project*`, `*team*`
- `*user*`, `*name*`, `*email*`, `*display*`
- `*template*`, `*process*`
- `*type*` (work item types)
- `*field*`, `*reference*`
- `*area*`, `*iteration*`, `*path*`
- `*title*`, `*description*`, `*content*`
- `*repo*`, `*branch*`, `*commit*`
- `*id*` (when referring to work item or ADO entity IDs)

## Testing Requirements

- Unit tests must validate the allowlist against all emitted keys
- Integration tests must assert zero HTTP calls when env vars are unset
- A dedicated `TelemetryPropertyAllowlistTests` class must enumerate all event types
  and assert every key passes the allowlist
