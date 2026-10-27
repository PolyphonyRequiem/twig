import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import test from "node:test";
import { createNativeAttachmentReader } from "../native-attachments.mjs";

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
class Child extends EventEmitter {
    constructor(args, result) {
        super();
        this.pid = 1234;
        this.stdin = new PassThrough();
        this.stdout = new PassThrough();
        this.stderr = new PassThrough();
        this.input = "";
        this.args = args;
        this.stdin.setEncoding("utf8");
        this.stdin.on("data", chunk => { this.input += chunk; });
        this.stdin.on("finish", () => {
            queueMicrotask(() => {
                this.stdout.once("end", () => this.emit("close", 0, null));
                this.stdout.end(result(JSON.parse(this.input)));
            });
        });
    }
}
function harness(action) {
    const calls = [];
    const content = Buffer.from([0, 255, 13, 10, 128]);
    const result = request => {
        calls.push(request);
        const base = { version: 1, status: action, capability: { implementation: "native", availability: "available" },
            context, attachments: [], attachment: null, contentType: null, contentLength: null, sha256: null, error: null };
        if (action === "inventory") { base.attachments = [attachment]; return Buffer.from(JSON.stringify(base)); }
        base.attachment = attachment;
        base.contentType = "application/pdf";
        base.contentLength = content.length;
        base.sha256 = createHash("sha256").update(content).digest("hex");
        const header = Buffer.from(JSON.stringify(base));
        const frame = Buffer.alloc(16 + header.length + content.length);
        frame.write("TWIGATT1", 0, "ascii");
        frame.writeUInt32BE(header.length, 8);
        header.copy(frame, 12);
        frame.writeUInt32BE(content.length, 12 + header.length);
        content.copy(frame, 16 + header.length);
        return frame;
    };
    const executable = { prepare: async name => { assert.equal(name, "attachments"); return "C:\\repo\\twig.exe"; } };
    const reader = createNativeAttachmentReader({
        executable,
        realpath: async value => value,
        spawn: (_file, args, options) => {
            assert.equal(options.shell, false);
            const child = new Child(args, result);
            return child;
        },
    });
    return { reader, calls, content };
}

test("native adapter sends strict inventory request over the admitted shell-free child", async () => {
    const h = harness("inventory");
    const result = await h.reader({ target, action: "inventory", workspacePath: target.expectedKey.checkoutRoot });
    assert.equal(result.status, "inventory");
    assert.deepEqual(h.calls, [{
        version: 1, action: "inventory", itemKey: target.expectedKey, bench: "12",
        expectBench: "12", expectIdentity: "identity", expectedRevision: null, attachmentId: null,
    }]);
});

test("native adapter frames download bytes separately from bounded metadata", async () => {
    const h = harness("download");
    const result = await h.reader({
        target, action: "download", entry: { id: attachment.id, revision: 7 },
        workspacePath: target.expectedKey.checkoutRoot,
    });
    assert.equal(result.status, "download");
    assert.deepEqual(result.content, h.content);
    assert.deepEqual(h.calls[0], {
        version: 1, action: "download", itemKey: target.expectedKey, bench: "12",
        expectBench: "12", expectIdentity: "identity", expectedRevision: 7, attachmentId: attachment.id,
    });
});
