// Self-contained so the same validator can be embedded in the nonce-bound iframe script.
export function validateDraftDocument(doc, expectedKey) {
    const record = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
    const nonempty = (v) => typeof v === "string" && v.trim().length > 0;
    const value = (v) => v === null || typeof v === "string";
    const integer = (v) => Number.isSafeInteger(v) && v >= 0;
    const id = (v) => Number.isInteger(v) && v !== 0 && Math.abs(v) <= 2147483647;
    const states = ["current", "conflicted", "blocked-local-work", "field-unavailable", "capability-refused", "rules-refused", "rules-unknown"];
    const completeness = ["complete-for-draft-effects", "incomplete-for-draft-effects", "blocked-by-local-work"];
    function requireShape(ok, message) {
        if (!ok) throw new Error(`Native draft inspection unavailable: ${message}`);
    }
    function validKey(key) {
        return record(key) && ["checkoutRoot", "connectionId", "bindingId"].every((k) => nonempty(key[k])) && id(key.workItemId);
    }
    requireShape(validKey(expectedKey), "a captured qualified item key is required");
    requireShape(record(doc) && doc.draftDocumentVersion === 1, "unsupported or missing draft document version");
    const key = doc.itemKey;
    requireShape(validKey(key) &&
        ["checkoutRoot", "connectionId", "bindingId", "workItemId"].every((k) => key[k] === expectedKey[k]), "qualified item identity changed");
    requireShape(["generation", "baselineRevision", "observedRevision", "currentRevision"].every((k) => integer(doc[k])),
        "missing, negative or unsafe numeric generation/revision");
    requireShape(["empty", "staged"].includes(doc.draftStatus) && states.includes(doc.status) &&
        doc.reconciliationState === doc.status && typeof doc.hasConflicts === "boolean" &&
        typeof doc.localPendingWork === "boolean" && doc.freshness === "cached" &&
        completeness.includes(doc.observationCompleteness), "missing or unsupported observation state");
    const e = doc.eligibility;
    requireShape(record(e) && typeof e.fieldsPersistable === "boolean" &&
        ["not-applicable", "unknown", "accepted", "rejected"].includes(e.rules) && value(e.rulesReason) &&
        e.authorized === false && e.inspectionOnly === true && e.serverIsFinalAuthority === true,
    "missing inspection-only eligibility or unexpected authority");
    for (const name of ["changes", "conflicts", "fields"]) requireShape(Array.isArray(doc[name]), `missing ${name}`);
    const changes = new Map();
    for (const change of doc.changes) {
        requireShape(record(change) && nonempty(change.fieldRef) && !changes.has(change.fieldRef.toLowerCase()) &&
            value(change.baselineValue) && value(change.proposedValue), "invalid or duplicate field change");
        changes.set(change.fieldRef.toLowerCase(), change);
    }
    const seen = new Set();
    for (const field of doc.fields) {
        const fieldKey = typeof field?.fieldRef === "string" ? field.fieldRef.toLowerCase() : undefined;
        const change = changes.get(fieldKey);
        requireShape(record(field) && change && !seen.has(fieldKey) &&
            value(field.baselineValue) && value(field.observedValue) && value(field.proposedValue) &&
            field.baselineValue === change.baselineValue && field.proposedValue === change.proposedValue &&
            typeof field.observedValueAvailable === "boolean" && typeof field.nativePersistable === "boolean" &&
            field.nativeCapabilitySource === "native-edit-sink" && value(field.eligibilityReason) &&
            typeof field.matchesBaseline === "boolean", "missing or contradictory per-field evidence");
        requireShape(field.observedValueAvailable || (field.observedValue === null && field.matchesBaseline === false),
            "unavailable observation must not imply a known value");
        requireShape(!field.matchesBaseline || field.observedValueAvailable &&
            field.observedValue === field.baselineValue, "baseline comparison contradicts observation");
        seen.add(fieldKey);
    }
    requireShape(seen.size === changes.size && (doc.draftStatus === "empty") === (changes.size === 0),
        "field coverage or draft status mismatch");
    const conflicts = new Set();
    for (const conflict of doc.conflicts) {
        const fieldKey = typeof conflict?.fieldRef === "string" ? conflict.fieldRef.toLowerCase() : undefined;
        const change = changes.get(fieldKey);
        requireShape(record(conflict) && change && !conflicts.has(fieldKey) &&
            ["baselineValue", "observedValue", "proposedValue"].every((k) => value(conflict[k])) &&
            conflict.baselineValue === change.baselineValue && conflict.proposedValue === change.proposedValue,
        "invalid conflict evidence");
        conflicts.add(fieldKey);
    }
    requireShape(doc.observedRevision === doc.currentRevision, "observed and current native revisions disagree");
    requireShape(doc.hasConflicts === (doc.baselineRevision !== doc.observedRevision || doc.conflicts.length > 0),
        "conflict flag contradicts revision drift or retained conflicts");
    requireShape(!doc.hasConflicts || doc.status === "conflicted", "conflicted observation is mislabeled");
    requireShape(!doc.localPendingWork || doc.observationCompleteness === "blocked-by-local-work",
        "local work completeness mismatch");
    return doc;
}
