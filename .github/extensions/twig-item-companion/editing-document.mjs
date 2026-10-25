const record = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const text = (value) => typeof value === "string" && value.trim().length > 0;
const nullable = (value) => value === null || typeof value === "string";
const own = (value, key) => Object.hasOwn(value, key);
const decimal = (value, positive = false) => typeof value === "string" &&
    /^(0|[1-9][0-9]*)$/.test(value) && value.length <= 19 &&
    BigInt(value) <= 9223372036854775807n && (!positive || value !== "0");
const itemId = (value) => Number.isInteger(value) && value > 0 && value <= 2147483647;
function requireShape(valid, message) {
    if (!valid) throw new Error(`Native editing contract unavailable: ${message}`);
}
function keys(value, required, optional = []) {
    return record(value) && required.every((key) => own(value, key)) &&
        Object.keys(value).every((key) => required.includes(key) || optional.includes(key));
}
function unique(values) {
    return new Set(values.map((value) => value.toLowerCase())).size === values.length;
}
function freeze(value) {
    if (value && typeof value === "object") {
        for (const child of Object.values(value)) freeze(child);
        Object.freeze(value);
    }
    return value;
}

export function validateEditingContext(context) {
    const required = ["itemKey", "externalIdentity", "benchId", "benchName", "benchContentsDigest",
        "identityId", "bindingRevision", "selectionSource", "selectionRevision", "attachmentRevision",
        "storageGeneration", "originDigest", "evidenceDigest"];
    requireShape(keys(context, required), "complete native context is required");
    requireShape(keys(context.itemKey, ["checkoutRoot", "connectionId", "bindingId", "workItemId"]) &&
        ["checkoutRoot", "connectionId", "bindingId"].every((key) => text(context.itemKey[key])) &&
        itemId(context.itemKey.workItemId), "invalid qualified item key");
    requireShape(keys(context.externalIdentity, ["providerKind", "resourceScope", "nativeId"]) &&
        Object.values(context.externalIdentity).every(text), "invalid native external identity");
    requireShape(decimal(context.benchId, true) &&
        ["benchName", "benchContentsDigest", "identityId", "selectionSource", "originDigest", "evidenceDigest"]
            .every((key) => text(context[key])) &&
        ["bindingRevision", "selectionRevision", "attachmentRevision"].every((key) => decimal(context[key])) &&
        (context.storageGeneration === null || text(context.storageGeneration)), "invalid native origin tokens");
    return context;
}

function layoutNode(value) {
    return record(value) && typeof value.id === "string" && typeof value.label === "string" &&
        typeof value.visible === "boolean" && typeof value.isContribution === "boolean";
}
function control(value) {
    if (!layoutNode(value) || typeof value.controlType !== "string" || typeof value.readOnly !== "boolean") return false;
    if (value.isContribution) return value.value == null;
    const field = value.value;
    return record(field) && [0, 1, 2].includes(field.state) &&
        (field.state === 0 ? typeof field.full === "string" : field.full == null) &&
        (field.short == null || typeof field.short === "string");
}
function page(value) {
    return layoutNode(value) && typeof value.pageType === "string" && Array.isArray(value.sections) &&
        value.sections.every((section) => record(section) && typeof section.id === "string" &&
            Array.isArray(section.groups) && section.groups.every((group) => layoutNode(group) &&
                Array.isArray(group.controls) && group.controls.every(control)));
}
function effects(values, conflict = false) {
    const required = ["fieldRef", "baselineValue", "proposedValue", ...(conflict ? ["observedValue"] : [])];
    return Array.isArray(values) && values.every((value) => keys(value, required) && text(value.fieldRef) &&
        required.filter((key) => key !== "fieldRef").every((key) => nullable(value[key]))) &&
        unique(values.map((value) => value.fieldRef));
}
function document(value) {
    requireShape(keys(value, ["nativeContext", "generation", "baselineRevision", "observedRevision", "currentRevision",
        "hasConflicts", "changes", "conflicts", "systemControls", "pages", "fields", "authorized", "serverFinalAuthority"],
    ["eligibilityIssue"]), "incomplete editing document");
    validateEditingContext(value.nativeContext);
    requireShape(decimal(value.generation) &&
        ["baselineRevision", "observedRevision", "currentRevision"].every((key) => decimal(value[key], true)) &&
        typeof value.hasConflicts === "boolean" && effects(value.changes) && effects(value.conflicts, true) &&
        value.hasConflicts === (value.baselineRevision !== value.observedRevision || value.conflicts.length > 0) &&
        value.authorized === false &&
        value.serverFinalAuthority === true, "invalid draft values or authority");
    requireShape(Array.isArray(value.systemControls) && value.systemControls.every(control) &&
        Array.isArray(value.pages) && value.pages.every(page), "invalid ordered native form");
    requireShape(Array.isArray(value.fields) && value.fields.every((field) =>
        keys(field, ["fieldRef", "editable"], ["reason"]) && text(field.fieldRef) &&
        typeof field.editable === "boolean" && (field.editable ? field.reason == null : text(field.reason))) &&
        unique(value.fields.map((field) => field.fieldRef)) &&
        (value.eligibilityIssue == null || text(value.eligibilityIssue)), "invalid native field eligibility");
}
function sameContext(a, b) {
    validateEditingContext(b);
    return Object.keys(a).every((key) => record(a[key])
        ? Object.keys(a[key]).every((part) => a[key][part] === b[key][part])
        : a[key] === b[key]);
}

export function validateEditingResult(value, { action, exitCode, expectedContext, expectedKey, bench, expectedRequest } = {}) {
    requireShape(keys(value, ["version", "status", "committed"], ["document", "error"]) &&
        value.version === 1 && typeof value.committed === "boolean", "unsupported result envelope");
    requireShape(action === "open" || action === "stage", "explicit operation is required");
    const success = value.status === (action === "open" ? "opened" : "staged");
    const refused = value.status === "refused";
    const uncertain = value.status === "indeterminate";
    requireShape(success || refused || uncertain, "unexpected result status");
    if (success) {
        requireShape(exitCode === 0 && value.committed === true && value.error == null &&
            value.document != null, "success contradicts exit or commit evidence");
    } else {
        requireShape((exitCode === 1 || exitCode === 2) &&
            keys(value.error, ["code", "message", "recovery"]) && Object.values(value.error).every(text) &&
            value.committed === uncertain && (!refused || value.document == null),
        "failure contradicts exit or commit evidence");
        requireShape(exitCode === (["usage", "invalid-json", "input-too-large"].includes(value.error.code) ? 2 : 1),
            "native error contradicts exit code");
    }
    if (value.document != null) {
        document(value.document);
        const context = value.document.nativeContext;
        requireShape(context.externalIdentity.providerKind === "ado", "editing provider is unsupported");
        if (expectedContext) requireShape(sameContext(context, expectedContext), "native context changed");
        if (expectedKey) requireShape(Object.keys(expectedKey).every((key) => context.itemKey[key] === expectedKey[key]),
            "returned qualified item differs");
        if (bench) requireShape(context.benchId === bench || context.benchName === bench, "returned Bench differs");
        if (success && action === "stage" && expectedRequest) {
            const generation = BigInt(expectedRequest.generation) + BigInt(expectedRequest.changes.length);
            requireShape(generation <= 9223372036854775807n &&
                value.document.generation === generation.toString() &&
                value.document.baselineRevision === expectedRequest.baselineRevision &&
                value.document.observedRevision === expectedRequest.observedRevision &&
                value.document.currentRevision === expectedRequest.observedRevision,
            "stage acknowledgement counters or revisions differ from submitted batch");
        }
    }
    return freeze(structuredClone(value));
}

export function serializeStageRequest(value) {
    requireShape(keys(value, ["version", "nativeContext", "generation", "baselineRevision", "observedRevision", "changes"]) &&
        value.version === 1, "unsupported stage request");
    validateEditingContext(value.nativeContext);
    requireShape(decimal(value.generation) && decimal(value.baselineRevision, true) && decimal(value.observedRevision, true),
        "lossless decimal counters are required");
    requireShape(Array.isArray(value.changes) && value.changes.length > 0 && value.changes.length <= 128 &&
        value.changes.every((change) => keys(change, ["fieldRef", "proposedValue"]) &&
            text(change.fieldRef) && nullable(change.proposedValue)) &&
        unique(value.changes.map((change) => change.fieldRef)), "invalid ordered nullable changes");
    requireShape(BigInt(value.generation) + BigInt(value.changes.length) <= 9223372036854775807n,
        "stage generation would overflow Int64");
    const json = JSON.stringify(value);
    requireShape(Buffer.byteLength(json, "utf8") <= 262144, "stage request exceeds the native UTF-8 limit");
    return json;
}
