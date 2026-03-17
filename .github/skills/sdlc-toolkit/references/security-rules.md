# Security Validation Rules

## Critical Severity

### SEC-001: Authentication Mechanism
**What it checks:** Design document defines how users/services authenticate.

**Patterns detected:**
- "authentication", "auth", "login", "identity"
- "OAuth", "OIDC", "SAML", "JWT"
- "API key", "bearer token", "certificate"

**Why it matters:** Without defined authentication, anyone can access the system.

**Recommendation:** Document the authentication mechanism, supported identity providers, and token handling.

---

### SEC-002: Authorization Model
**What it checks:** Design defines who can do what (access control).

**Patterns detected:**
- "authorization", "access control", "permissions"
- "RBAC", "ABAC", "ACL"
- "roles", "privileges", "scopes"

**Why it matters:** Auth without authz means all authenticated users have full access.

**Recommendation:** Document roles, permissions, and how access decisions are made.

---

## High Severity

### SEC-003: Data Encryption
**What it checks:** Encryption approach for data at rest and in transit.

**Patterns detected:**
- "encryption", "TLS", "HTTPS", "SSL"
- "encrypted at rest", "AES", "key management"
- "certificate", "secure channel"

**Why it matters:** Unencrypted data can be intercepted or stolen.

**Recommendation:** Specify TLS version for transit, encryption algorithm for rest, and key management approach.

---

### SEC-004: Input Validation
**What it checks:** How user/external input is validated and sanitized.

**Patterns detected:**
- "input validation", "sanitization", "validation"
- "injection", "XSS", "SQL injection"
- "schema validation", "type checking"

**Why it matters:** Unvalidated input leads to injection attacks.

**Recommendation:** Document validation approach, schema enforcement, and sanitization strategy.

---

### SEC-006: API Security
**What it checks:** API authentication and rate limiting.

**Patterns detected:**
- "API security", "rate limit", "throttling"
- "API key", "OAuth", "bearer"
- "quota", "request limit"

**Why it matters:** Unsecured APIs are common attack vectors.

**Recommendation:** Document API auth mechanism, rate limits, and abuse prevention.

---

## Medium Severity

### SEC-005: Secrets Management
**What it checks:** How secrets (keys, passwords, tokens) are stored and accessed.

**Patterns detected:**
- "secrets", "key vault", "secret management"
- "environment variable", "configuration"
- "rotation", "credential"

**Why it matters:** Hardcoded secrets leak and are hard to rotate.

**Recommendation:** Specify secrets storage (Key Vault, etc.), rotation policy, and access controls.

---

### SEC-007: Data Classification
**What it checks:** How sensitive data is identified and handled.

**Patterns detected:**
- "data classification", "PII", "sensitive"
- "GDPR", "compliance", "privacy"
- "data handling", "retention"

**Why it matters:** Mishandled sensitive data causes compliance violations.

**Recommendation:** Document data classification, handling requirements, and retention policy.

---

### SEC-008: Audit Logging
**What it checks:** Security-relevant events are logged.

**Patterns detected:**
- "audit log", "security log", "event log"
- "login attempts", "access log"
- "trail", "forensics"

**Why it matters:** Without audit logs, security incidents can't be investigated.

**Recommendation:** Document what security events are logged and retention period.
