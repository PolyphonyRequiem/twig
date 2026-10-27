import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import {
    parseAttachmentFrame, serializeAttachmentRequest, validateAttachmentResult,
} from "../attachment-document.mjs";

const target = {
    workItemId: 42, bench: "12", identityId: "identity", benchName: "Sprint",
    expectedKey: { checkoutRoot: "C:\\repo", connectionId: "connection", bindingId: "binding", workItemId: 42 },
    origin: { bindingRevision: 4, selectionRevision: 8, attachmentRevision: 3,
        selectionSource: "bench", storageGeneration: "9" },
};
const attachment = { id: "4dab0171-49dc-4da5-8f18-35c923fbdb0a", fileName: "report.pdf", reportedSize: "5" };
const context = {
    itemKey: target.expectedKey, benchId: "12", benchName: "Sprint", benchContentsDigest: "native-digest",
    identityId: "identity", bindingRevision: "4", selectionSource: "bench", selectionRevision: "8",
    attachmentRevision: "3", storageGeneration: "9", workItemRevision: 7,
};
const baseResult = {
    version: 1, status: "inventory", capability: { implementation: "native", availability: "available" },
    context, attachments: [attachment], attachment: null, contentType: null, contentLength: null, sha256: null, error: null,
};

test("inventory and download requests carry only the captured native qualifiers", () => {
    assert.deepEqual(JSON.parse(serializeAttachmentRequest(target, "inventory")), {
        version: 1, action: "inventory", itemKey: target.expectedKey, bench: "12",
        expectBench: "12", expectIdentity: "identity", expectedRevision: null, attachmentId: null,
    });
    assert.deepEqual(JSON.parse(serializeAttachmentRequest(target, "download", { id: attachment.id, revision: 7 })), {
        version: 1, action: "download", itemKey: target.expectedKey, bench: "12",
        expectBench: "12", expectIdentity: "identity", expectedRevision: 7, attachmentId: attachment.id,
    });
});

test("inventory validation requires exact context and rejects duplicate or unsafe attachments", () => {
    assert.equal(validateAttachmentResult(baseResult, { target, action: "inventory", exitCode: 0 }).attachments[0].id,
        attachment.id);
    for (const mutate of [
        value => { value.context.itemKey.workItemId = 43; },
        value => { value.context.attachmentRevision = "4"; },
        value => { value.attachments[0].fileName = "../secret"; },
        value => { value.attachments[0].fileName = "bad\\r\\nname"; },
        value => { value.attachments[0].reportedSize = 1; },
        value => { value.attachments.push(structuredClone(value.attachments[0])); },
        value => { value.extra = true; },
    ]) {
        const value = structuredClone(baseResult);
        mutate(value);
        assert.throws(() => validateAttachmentResult(value, { target, action: "inventory", exitCode: 0 }));
    }
});

test("download validation matches the selected attachment, exact bytes, size and digest metadata", () => {
    const bytes = Buffer.from([0, 255, 13, 10, 128]);
    const value = { ...baseResult, status: "download", attachments: [], attachment,
        contentType: "application/pdf", contentLength: bytes.length,
        sha256: createHash("sha256").update(bytes).digest("hex") };
    const expectedEntry = { id: attachment.id, revision: 7 };
    const result = validateAttachmentResult(value, { target, action: "download", exitCode: 0, content: bytes, expectedEntry });
    assert.equal(result.content, bytes);
    for (const mutate of [
        response => { response.contentLength++; },
        response => { response.attachment.id = "4dab0171-49dc-4da5-8f18-35c923fbdb0b"; },
        response => { response.contentType = "text/html\r\nX-Evil: yes"; },
        response => { response.sha256 = "A".repeat(64); },
    ]) {
        const broken = structuredClone(value);
        mutate(broken);
        assert.throws(() => validateAttachmentResult(broken, {
            target, action: "download", exitCode: 0, content: bytes, expectedEntry,
        }));
    }
    assert.throws(() => validateAttachmentResult(value, {
        target, action: "download", exitCode: 0, content: Buffer.alloc(4), expectedEntry,
    }));
});

test("binary frame accepts exact bounded framing and rejects malformed, trailing or oversized data", () => {
    const header = Buffer.from(JSON.stringify(baseResult));
    const bytes = Buffer.from([0, 255, 13, 10, 128]);
    const frame = Buffer.alloc(16 + header.length + bytes.length);
    frame.write("TWIGATT1", 0, "ascii");
    frame.writeUInt32BE(header.length, 8);
    header.copy(frame, 12);
    frame.writeUInt32BE(bytes.length, 12 + header.length);
    bytes.copy(frame, 16 + header.length);
    const parsed = parseAttachmentFrame(frame);
    assert.deepEqual(parsed.value, baseResult);
    assert.deepEqual(parsed.content, bytes);
    assert.throws(() => parseAttachmentFrame(Buffer.concat([frame, Buffer.from([1])])));
    assert.throws(() => parseAttachmentFrame(Buffer.from("not-a-frame")));
});
