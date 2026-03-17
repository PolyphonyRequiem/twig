# Reliability Validation Rules

## High Severity

### REL-001: Retry and Backoff Strategy
**What it checks:** Design specifies retry logic for transient failures.

**Patterns detected:**
- "retry", "backoff", "exponential backoff"
- "transient failure", "retry policy"
- "circuit breaker", "retry count"

**Why it matters:** Without retries, transient failures cause unnecessary outages.

**Recommendation:** Document retry count, backoff strategy (exponential recommended), and jitter.

---

### REL-002: Circuit Breaker Pattern
**What it checks:** System can isolate failing dependencies.

**Patterns detected:**
- "circuit breaker", "bulkhead", "isolation"
- "fail fast", "failure isolation"
- "dependency failure"

**Why it matters:** Without circuit breakers, one failed dependency can cascade to entire system.

**Recommendation:** Document circuit breaker thresholds, open/half-open states, and fallback behavior.

---

### REL-003: Timeout Handling
**What it checks:** All external calls have defined timeouts.

**Patterns detected:**
- "timeout", "deadline", "time limit"
- "request timeout", "connection timeout"
- "SLA", "latency"

**Why it matters:** Missing timeouts cause thread/connection exhaustion.

**Recommendation:** Specify connection, read, and write timeouts for all external calls.

---

## Medium Severity

### REL-004: Graceful Degradation
**What it checks:** System behavior when dependencies fail.

**Patterns detected:**
- "graceful degradation", "fallback", "degraded mode"
- "cached response", "default value"
- "partial failure"

**Why it matters:** Users prefer degraded service over complete outage.

**Recommendation:** Document fallback behavior for each critical dependency.

---

### REL-005: Health Check Endpoints
**What it checks:** Service exposes health/readiness probes.

**Patterns detected:**
- "health check", "liveness", "readiness"
- "health endpoint", "/health", "/ready"
- "probe", "heartbeat"

**Why it matters:** Without health checks, orchestrators can't manage service lifecycle.

**Recommendation:** Define liveness and readiness probe endpoints and their criteria.

---

### REL-006: Idempotency
**What it checks:** Operations can be safely retried.

**Patterns detected:**
- "idempotent", "idempotency key"
- "safe to retry", "at-least-once"
- "deduplication"

**Why it matters:** Non-idempotent operations can cause data corruption on retry.

**Recommendation:** Document idempotency approach for write operations.

---

### REL-007: Rate Limiting
**What it checks:** System protects itself from overload.

**Patterns detected:**
- "rate limit", "throttling", "quota"
- "requests per second", "RPS"
- "back pressure"

**Why it matters:** Without rate limits, bursts can overwhelm the system.

**Recommendation:** Specify rate limits per client/endpoint and 429 response handling.

---

## Low Severity

### REL-008: SLA and SLO
**What it checks:** Service level objectives are defined.

**Patterns detected:**
- "SLA", "SLO", "SLI"
- "availability", "uptime"
- "99.9%", "latency target"

**Why it matters:** Without SLOs, reliability can't be measured or improved.

**Recommendation:** Define availability target, latency percentiles, and error budget.

---

### REL-009: Disaster Recovery
**What it checks:** Recovery strategy for major failures.

**Patterns detected:**
- "disaster recovery", "DR", "failover"
- "backup", "restore", "RTO", "RPO"
- "multi-region"

**Why it matters:** Major failures will happen; recovery must be planned.

**Recommendation:** Document RTO, RPO, backup frequency, and failover procedure.
