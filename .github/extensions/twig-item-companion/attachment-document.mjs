import { createHash } from "node:crypto";

const record = value => value !== null && typeof value === "object" && !Array.isArray(value);
const nonempty = value => typeof value === "string" && value.trim().length > 0;
const decimal = value => typeof value === "string" && /^(0|[1-9][0-9]*)$/.test(value);
const positive = value => decimal(value) && value !== "0";
const keyKeys = ["checkoutRoot", "connectionId", "bindingId", "workItemId"];
const contextKeys = ["itemKey", "benchId", "benchName", "benchContentsDigest", "identityId",
    "bindingRevision", "selectionSource", "selectionRevision", "attachmentRevision", "storageGeneration",
    "workItemRevision"];
const entryKeys = ["id", "fileName", "reportedSize"];
const resultKeys = ["version", "status", "capability", "context", "attachments", "attachment",
    "contentType", "contentLength", "sha256", "error"];
const refusalCodes = new Set(["usage", "invalid-json", "input-too-large", "provider-unsupported",
    "provider-unknown", "provider-unavailable", "authority-invalidated", "canceled",
    "bench-item-not-member", "bench-item-not-cached", "attachment-denied", "attachment-not-found",
    "attachment-offline", "attachment-throttled", "attachment-too-large", "attachment-stale",
    "attachment-not-linked", "attachment-response-invalid", "attachment-unavailable"]);
export const MAX_ATTACHMENT_BYTES = 16 * 1024 * 1024;
const fail = () => { throw new Error("Native attachment response was invalid or mismatched."); };
const shape = (value, keys) => record(value) && Object.keys(value).sort().join(",") === [...keys].sort().join(",");
function validKey(key) {
    return shape(key, keyKeys) && keyKeys.slice(0, 3).every(name => nonempty(key[name])) &&
        Number.isInteger(key.workItemId) && key.workItemId > 0 && key.workItemId <= 2147483647;
}
function sameKey(a, b) { return keyKeys.every(name => a[name] === b[name]); }
function validContext(context) {
    return shape(context, contextKeys) && validKey(context.itemKey) && positive(context.benchId) &&
        ["benchName", "benchContentsDigest", "identityId", "selectionSource"].every(name => nonempty(context[name])) &&
        ["bindingRevision", "selectionRevision", "attachmentRevision"].every(name => decimal(context[name])) &&
        (context.storageGeneration === null || nonempty(context.storageGeneration)) &&
        Number.isSafeInteger(context.workItemRevision) && context.workItemRevision > 0;
}
function validEntry(entry) {
    return shape(entry, entryKeys) && typeof entry.id === "string" &&
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(entry.id) &&
        nonempty(entry.fileName) && entry.fileName.length <= 255 && !/[/\\\u0000-\u001f\u007f-\u009f]/.test(entry.fileName) &&
        entry.fileName !== "." && entry.fileName !== ".." &&
        (entry.reportedSize === null || decimal(entry.reportedSize));
}
function validateContext(context, target) {
    if (!validContext(context) || !sameKey(context.itemKey, target.expectedKey) ||
        context.benchId !== target.bench || context.identityId !== target.identityId) fail();
    const origin = target.origin;
    if (!record(origin) || ["bindingRevision", "selectionRevision", "attachmentRevision"].some(name =>
        !Number.isSafeInteger(origin[name]) || origin[name] < 0 || context[name] !== String(origin[name])) ||
        context.selectionSource !== origin.selectionSource ||
        context.storageGeneration !== (origin.storageGeneration ?? null) ||
        context.benchName !== target.benchName) fail();
}

export function serializeAttachmentRequest(target, action, entry = null) {
    if (!record(target) || !validKey(target.expectedKey) || target.workItemId !== target.expectedKey.workItemId ||
        !positive(target.bench) || !nonempty(target.identityId) || !["inventory", "download"].includes(action)) fail();
    if (action === "inventory" ? entry !== null :
        !record(entry) || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(entry.id) ||
        !Number.isSafeInteger(entry.revision) || entry.revision <= 0) fail();
    const source = JSON.stringify({
        version: 1, action, itemKey: target.expectedKey, bench: target.bench,
        expectBench: target.bench, expectIdentity: target.identityId,
        expectedRevision: action === "download" ? entry.revision : null,
        attachmentId: action === "download" ? entry.id : null,
    });
    if (Buffer.byteLength(source, "utf8") > 262144) fail();
    return source;
}

export function validateAttachmentResult(value, { target, action, exitCode, content = null, expectedEntry = null }) {
    if (!shape(value, resultKeys) || value.version !== 1 || !shape(value.capability, ["implementation", "availability"]) ||
        !Array.isArray(value.attachments)) fail();
    if (value.status === "refused") {
        if (![1, 2].includes(exitCode) || value.context !== null || value.attachments.length ||
            value.attachment !== null || value.contentType !== null || value.contentLength !== null ||
            value.sha256 !== null || !shape(value.error, ["code", "message", "recovery"]) ||
            !refusalCodes.has(value.error.code) || typeof value.error.message !== "string" ||
            typeof value.error.recovery !== "string" || value.capability.availability !== "unavailable" ||
            value.capability.implementation !== (["provider-unsupported", "provider-unavailable"].includes(value.error.code)
                ? "unsupported" : "native") ||
            (exitCode === 2) !== ["usage", "invalid-json", "input-too-large"].includes(value.error.code)) fail();
        return { status: "refused", code: value.error.code };
    }
    if (exitCode !== 0 || value.error !== null || value.capability.implementation !== "native" ||
        value.capability.availability !== "available") fail();
    validateContext(value.context, target);
    if (action === "inventory") {
        if (value.status !== "inventory" || value.attachment !== null || value.contentType !== null ||
            value.contentLength !== null || value.sha256 !== null || content !== null ||
            value.attachments.length > 10000 || value.attachments.some(entry => !validEntry(entry)) ||
            new Set(value.attachments.map(entry => entry.id)).size !== value.attachments.length) fail();
        return { status: "inventory", context: structuredClone(value.context), attachments: structuredClone(value.attachments) };
    }
    if (value.status !== "download" || value.attachments.length || !validEntry(value.attachment) ||
        !Number.isSafeInteger(value.contentLength) || value.contentLength < 0 ||
        value.contentLength > MAX_ATTACHMENT_BYTES || typeof value.contentType !== "string" ||
        value.contentType.length > 256 || !/^[\w!#$&^_.+-]+\/[\w!#$&^_.+-]+(?:\s*;\s*[\w!#$&^_.+-]+=[\w!#$&^_.+-]+)*$/.test(value.contentType) ||
        typeof value.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(value.sha256) ||
        !Buffer.isBuffer(content) || content.length !== value.contentLength ||
        !record(expectedEntry) || value.attachment.id !== expectedEntry.id ||
        value.context.workItemRevision !== expectedEntry.revision ||
        createHash("sha256").update(content).digest("hex") !== value.sha256) fail();
    return { status: "download", context: structuredClone(value.context), attachment: structuredClone(value.attachment),
        contentType: value.contentType, contentLength: value.contentLength, sha256: value.sha256, content };
}

export function parseAttachmentFrame(frame) {
    if (!Buffer.isBuffer(frame) || frame.length < 16 || frame.subarray(0, 8).toString("ascii") !== "TWIGATT1") fail();
    const headerLength = frame.readUInt32BE(8);
    if (headerLength < 2 || headerLength > 65536 || frame.length < 16 + headerLength) fail();
    const headerEnd = 12 + headerLength;
    const contentLength = frame.readUInt32BE(headerEnd);
    if (contentLength > MAX_ATTACHMENT_BYTES || frame.length !== headerEnd + 4 + contentLength) fail();
    let value;
    try { value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(frame.subarray(12, headerEnd))); }
    catch { fail(); }
    return { value, content: frame.subarray(headerEnd + 4) };
}
