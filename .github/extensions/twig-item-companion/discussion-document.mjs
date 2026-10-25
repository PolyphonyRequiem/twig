const record = value => value !== null && typeof value === "object" && !Array.isArray(value);
const nonempty = value => typeof value === "string" && value.trim().length > 0;
const nullableText = value => value === null || typeof value === "string";
const decimal = value => typeof value === "string" && /^(0|[1-9][0-9]*)$/.test(value);
const positive = value => decimal(value) && value !== "0";
const profile = "ado-comments-v1-top50-asc-deleted-none";
const contextKeys = ["itemKey", "benchId", "benchName", "benchContentsDigest", "identityId",
    "bindingRevision", "selectionSource", "selectionRevision", "attachmentRevision", "storageGeneration"];
const keyKeys = ["checkoutRoot", "connectionId", "bindingId", "workItemId"];
const refusalCodes = new Set(["usage", "invalid-json", "input-too-large", "provider-unsupported",
    "provider-unknown", "provider-unavailable", "authority-invalidated", "canceled",
    "bench-item-not-member", "bench-item-not-cached", "discussion-denied", "discussion-not-found",
    "discussion-offline", "discussion-throttled", "discussion-response-too-large",
    "discussion-response-invalid", "discussion-unavailable"]);
function requireValue(condition) {
    if (!condition) throw new Error("Native discussion returned an invalid or mismatched page.");
}
function shape(value, keys) {
    return record(value) && Object.keys(value).sort().join(",") === [...keys].sort().join(",");
}
function validKey(key) {
    return shape(key, keyKeys) && keyKeys.slice(0, 3).every(name => nonempty(key[name])) &&
        Number.isInteger(key.workItemId) && key.workItemId > 0 && key.workItemId <= 2147483647;
}
function sameKey(a, b) { return keyKeys.every(name => a[name] === b[name]); }
function validContext(context) {
    return shape(context, contextKeys) && validKey(context.itemKey) && positive(context.benchId) &&
        ["benchName", "benchContentsDigest", "identityId", "selectionSource"].every(name => nonempty(context[name])) &&
        ["bindingRevision", "selectionRevision", "attachmentRevision"].every(name => decimal(context[name])) &&
        nullableText(context.storageGeneration);
}
export function sameDiscussionContext(a, b) {
    return validContext(a) && validContext(b) && sameKey(a.itemKey, b.itemKey) &&
        contextKeys.filter(name => name !== "itemKey").every(name => a[name] === b[name]);
}
function validCursor(cursor) {
    return shape(cursor, ["version", "context", "providerToken", "optionProfile"]) &&
        cursor.version === 1 && validContext(cursor.context) && cursor.optionProfile === profile &&
        nonempty(cursor.providerToken) && Buffer.byteLength(cursor.providerToken, "utf8") <= 8192 &&
        !/[\u0000-\u001f\u007f-\u009f]/.test(cursor.providerToken);
}
export function serializeDiscussionRequest(target, cursor = null) {
    requireValue(record(target) && validKey(target.expectedKey) &&
        target.workItemId === target.expectedKey.workItemId && positive(target.bench) && nonempty(target.identityId));
    requireValue(cursor === null || validCursor(cursor));
    if (cursor !== null) requireValue(sameKey(cursor.context.itemKey, target.expectedKey) &&
        cursor.context.benchId === target.bench && cursor.context.identityId === target.identityId);
    const source = JSON.stringify({ version: 1, itemKey: target.expectedKey, bench: target.bench,
        expectBench: target.bench, expectIdentity: target.identityId, cursor });
    requireValue(Buffer.byteLength(source, "utf8") <= 262144);
    return source;
}
export function validateDiscussionResult(value, { target, context = null, cursor = null, exitCode }) {
    requireValue(shape(value, ["version", "status", "capability", "context", "comments", "paging", "error"]) &&
        value.version === 1 && shape(value.capability, ["implementation", "availability"]) &&
        Array.isArray(value.comments));
    if (value.status === "refused") {
        requireValue([1, 2].includes(exitCode) && value.context === null && value.paging === null &&
            value.comments.length === 0 && shape(value.error, ["code", "message", "recovery"]) &&
            refusalCodes.has(value.error.code) && typeof value.error.message === "string" &&
            typeof value.error.recovery === "string" &&
            value.capability.availability === "unavailable" &&
            value.capability.implementation === (value.error.code === "provider-unsupported" ? "unsupported" : "native"));
        requireValue((exitCode === 2) === ["usage", "invalid-json", "input-too-large"].includes(value.error.code));
        return { status: "refused", code: value.error.code };
    }
    requireValue(exitCode === 0 && value.status === "read" && value.error === null &&
        value.capability.implementation === "native" && value.capability.availability === "available" &&
        validContext(value.context));
    const actual = value.context;
    requireValue(sameKey(actual.itemKey, target.expectedKey) && actual.benchId === target.bench &&
        actual.identityId === target.identityId);
    if (context !== null) requireValue(sameDiscussionContext(actual, context));
    const origin = target.origin;
    requireValue(record(origin));
    for (const name of ["bindingRevision", "selectionRevision", "attachmentRevision"]) {
        requireValue(Number.isSafeInteger(origin[name]) && origin[name] >= 0 &&
            actual[name] === String(origin[name]));
    }
    requireValue(actual.selectionSource === origin.selectionSource &&
        actual.storageGeneration === (origin.storageGeneration ?? null) && actual.benchName === target.benchName);
    const paging = value.paging;
    requireValue(shape(paging, ["traversal", "count", "reportedTotalCount", "nextCursor", "scope", "snapshotConsistency"]) &&
        ["more", "exhausted"].includes(paging.traversal) && decimal(paging.count) &&
        BigInt(paging.count) === BigInt(value.comments.length) && value.comments.length <= 50 &&
        (paging.reportedTotalCount === null || decimal(paging.reportedTotalCount) &&
            BigInt(paging.reportedTotalCount) >= BigInt(paging.count)) &&
        paging.scope === "provider-page" && paging.snapshotConsistency === "not-guaranteed" &&
        (paging.traversal === "more" ? validCursor(paging.nextCursor) &&
            sameDiscussionContext(paging.nextCursor.context, actual) : paging.nextCursor === null));
    if (paging.nextCursor && cursor) requireValue(paging.nextCursor.providerToken !== cursor.providerToken);
    const ids = new Set();
    const comments = value.comments.map(comment => {
        requireValue(shape(comment, ["id", "version", "author", "createdAt", "modifiedAt", "isDeleted", "body", "format"]) &&
            positive(comment.id) && !ids.has(comment.id) && (comment.version === null || positive(comment.version)) &&
            shape(comment.author, ["id", "displayName"]) && nullableText(comment.author.id) &&
            nullableText(comment.author.displayName) && nullableText(comment.body) &&
            (comment.format === null || ["html", "markdown", "unknown"].includes(comment.format)) &&
            (comment.isDeleted === null || typeof comment.isDeleted === "boolean") &&
            [comment.createdAt, comment.modifiedAt].every(time => time === null ||
                typeof time === "string" && /(?:Z|\+00:00)$/.test(time) && Number.isFinite(Date.parse(time))));
        ids.add(comment.id);
        return structuredClone(comment);
    });
    return { status: "read", context: structuredClone(actual), cursor: structuredClone(paging.nextCursor),
        page: { comments, count: paging.count, reportedTotalCount: paging.reportedTotalCount,
            traversal: paging.traversal, scope: paging.scope, snapshotConsistency: paging.snapshotConsistency } };
}
