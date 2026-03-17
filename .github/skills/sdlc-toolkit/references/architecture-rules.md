# Architecture Validation Rules

## High Severity

### ARCH-001: Scalability Considerations
**What it checks:** Design addresses how system handles growth.

**Patterns detected:**
- "scalability", "scale out", "horizontal scaling"
- "load balancer", "auto-scaling"
- "stateless", "distributed"

**Why it matters:** Unplanned scalability leads to rewrites under pressure.

**Recommendation:** Document scaling strategy, statelessness approach, and capacity limits.

---

### ARCH-002: Data Model Design
**What it checks:** Data structures and storage are defined.

**Patterns detected:**
- "data model", "schema", "entity"
- "database", "storage", "persistence"
- "relationship", "normalization"

**Why it matters:** Poor data models are expensive to change later.

**Recommendation:** Document entities, relationships, storage choice rationale, and migration strategy.

---

## Medium Severity

### ARCH-003: Caching Strategy
**What it checks:** Caching approach for performance.

**Patterns detected:**
- "cache", "caching", "Redis", "Memcached"
- "cache invalidation", "TTL"
- "cache aside", "write through"

**Why it matters:** Missing or poor caching causes performance issues at scale.

**Recommendation:** Define what to cache, TTL, invalidation strategy, and cache sizing.

---

### ARCH-004: API Design
**What it checks:** API contracts and versioning.

**Patterns detected:**
- "API", "endpoint", "contract"
- "REST", "GraphQL", "gRPC"
- "versioning", "backward compatible"

**Why it matters:** Breaking API changes cause integration failures.

**Recommendation:** Document API style, versioning strategy, and deprecation policy.

---

### ARCH-005: Async Processing
**What it checks:** Long-running operations handled asynchronously.

**Patterns detected:**
- "async", "asynchronous", "queue"
- "message bus", "event-driven"
- "background job", "worker"

**Why it matters:** Synchronous long operations block threads and hurt UX.

**Recommendation:** Identify long operations and document async processing approach.

---

### ARCH-006: Service Dependencies
**What it checks:** External dependencies are documented.

**Patterns detected:**
- "dependency", "depends on", "integration"
- "external service", "third-party"
- "upstream", "downstream"

**Why it matters:** Unknown dependencies cause unexpected failures.

**Recommendation:** Document all dependencies, their criticality, and fallback behavior.

---

## Low Severity

### ARCH-007: Observability
**What it checks:** Logging, metrics, and tracing approach.

**Patterns detected:**
- "observability", "monitoring", "metrics"
- "logging", "tracing", "telemetry"
- "dashboard", "alerting"

**Why it matters:** Without observability, issues are hard to diagnose.

**Recommendation:** Define logging strategy, key metrics, and tracing approach.

---

### ARCH-008: Configuration Management
**What it checks:** How configuration is managed.

**Patterns detected:**
- "configuration", "config", "settings"
- "environment variable", "feature flag"
- "dynamic config"

**Why it matters:** Hardcoded config requires deployments for changes.

**Recommendation:** Document configuration sources, feature flags, and change process.

---

### ARCH-009: Testing Strategy
**What it checks:** Testing approach is defined.

**Patterns detected:**
- "testing", "test strategy", "unit test"
- "integration test", "e2e", "end-to-end"
- "test coverage", "test pyramid"

**Why it matters:** Untested code has unknown quality.

**Recommendation:** Define testing levels, coverage targets, and test environment approach.

---

### ARCH-010: Deployment Strategy
**What it checks:** How changes are deployed.

**Patterns detected:**
- "deployment", "deploy", "release"
- "blue-green", "canary", "rolling"
- "CI/CD", "pipeline"

**Why it matters:** Poor deployment strategy increases risk of outages.

**Recommendation:** Document deployment method, rollback strategy, and validation approach.
