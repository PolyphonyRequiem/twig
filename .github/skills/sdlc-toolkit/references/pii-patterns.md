# PII Detection Patterns

Reference patterns for detecting Personally Identifiable Information (PII) in code, data, and outputs.

## High Risk PII Types

### Email Addresses
**Pattern:** `[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}`
**Risk Level:** High
**Compliance:** GDPR, CCPA, HIPAA
**Recommendation:** Hash or encrypt email addresses at rest, mask in logs

### Social Security Numbers (SSN)
**Pattern:** `\d{3}-\d{2}-\d{4}` or `\d{9}`
**Risk Level:** Critical
**Compliance:** PCI-DSS, SOX, HIPAA
**Recommendation:** Never store full SSN, use last 4 digits only if needed

### Credit Card Numbers
**Pattern:** `\d{4}[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}`
**Risk Level:** Critical
**Compliance:** PCI-DSS
**Recommendation:** Never store full card numbers, use tokenization

### Phone Numbers
**Pattern:** `(\+\d{1,3})?[\s.-]?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}`
**Risk Level:** Medium
**Compliance:** GDPR, CCPA
**Recommendation:** Mask in logs, encrypt at rest

### IP Addresses
**Pattern:** `\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}`
**Risk Level:** Medium
**Compliance:** GDPR (EU considers IP PII)
**Recommendation:** Anonymize for analytics, encrypt in logs

## Medium Risk PII Types

### Physical Addresses
**Pattern:** Street numbers followed by street names, city, state, zip
**Risk Level:** Medium
**Compliance:** GDPR, CCPA
**Recommendation:** Encrypt at rest, limit access

### Date of Birth
**Pattern:** `\d{1,2}[/-]\d{1,2}[/-]\d{2,4}`
**Risk Level:** Medium
**Compliance:** GDPR, HIPAA
**Recommendation:** Store only if necessary, encrypt at rest

### Names
**Pattern:** Context-dependent (harder to detect programmatically)
**Risk Level:** Medium (varies by context)
**Compliance:** GDPR, CCPA
**Recommendation:** Pseudonymize where possible

## Healthcare PII (HIPAA)

### Medical Record Numbers
**Pattern:** Varies by institution
**Risk Level:** Critical
**Compliance:** HIPAA
**Recommendation:** Strong encryption, audit logging

### Health Information
**Pattern:** Medical terms, diagnoses, prescriptions
**Risk Level:** Critical
**Compliance:** HIPAA
**Recommendation:** Encrypt, access control, audit logging

## Financial PII

### Bank Account Numbers
**Pattern:** `\d{8,17}` (varies by country)
**Risk Level:** High
**Compliance:** PCI-DSS, SOX
**Recommendation:** Tokenization, encryption

### Tax IDs
**Pattern:** `\d{2}-\d{7}` (EIN) or SSN format
**Risk Level:** High
**Compliance:** IRS regulations
**Recommendation:** Encrypt, minimize storage

## Detection Strategies

### 1. Pattern Matching
- Use regex for structured PII (SSN, email, phone)
- High precision but may miss variations

### 2. Named Entity Recognition (NER)
- ML-based detection for names, addresses
- Better coverage but may have false positives

### 3. Context Analysis
- Look for field names like "ssn", "email", "phone"
- Check for data that appears together (name + address)

### 4. Data Flow Analysis
- Track where sensitive data flows through the system
- Identify accidental exposure points

## Remediation Guidelines

| Risk Level | Action Required |
|------------|-----------------|
| Critical | Immediate removal, incident report, encryption |
| High | Encrypt at rest, mask in logs, limit access |
| Medium | Review necessity, encrypt if stored long-term |
| Low | Document handling, standard access controls |

## Compliance Quick Reference

| Regulation | Key PII Types | Penalty |
|------------|---------------|---------|
| GDPR | All personal data of EU residents | Up to 4% global revenue |
| CCPA | Personal info of CA residents | $7,500 per violation |
| HIPAA | Protected Health Information (PHI) | Up to $1.5M per category |
| PCI-DSS | Cardholder data | Fines, loss of card processing |
