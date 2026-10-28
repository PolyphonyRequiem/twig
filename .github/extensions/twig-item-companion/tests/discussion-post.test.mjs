import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { EventEmitter } from "node:events";
import { mkdtemp, mkdir, readFile, realpath, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { createNativeDiscussionPoster, validateDiscussionPreparation } from "../native-discussion-post.mjs";
import { createDiscussionPost } from "../discussion-post.mjs";
import { discussionContext } from "./discussion-fixture.mjs";
import { fakeExecutable } from "./executable-fixture.mjs";

async function setup({ blocked = false, missingReceipt = false } = {}) {
    const root = await realpath(await mkdtemp(path.join(os.tmpdir(), "twig-discussion-post-")));
    await mkdir(path.join(root, ".twig"));
    const context = discussionContext();
    context.itemKey.checkoutRoot = root;
    context.benchContentsDigest = "b".repeat(64);
    const text = "<script>literal</script>";
    const effectiveBody = "<p>&lt;script&gt;literal&lt;/script&gt;</p>\n";
    const operation = { id: "discussion-one", kind: "append-comment", workItemId: 42, savedBenchId: context.benchId,
        expectedBenchContentsDigest: context.benchContentsDigest, expectedIdentityId: context.identityId,
        text, format: "plain", effectiveBody };
    const workspace = { organization: "synthetic", project: "synthetic" };
    const proposal = JSON.stringify({ version: 1, workspace, operations: [operation] });
    const digest = createHash("sha256").update(proposal).digest("hex");
    const native = { version: 1, status: "prepared", context, proposal, digest, error: null };
    const preview = { digest, canApply: !blocked, issues: [], pendingChanges: [], operations: [
        { ordinal: 0, id: operation.id, kind: "AppendComment" }],
        reviewModel: { model: "twig.change-proposal.review", modelVersion: 2, digest, workspace,
            rationale: null, affectedItems: [], blockers: [], operations: [
                { ordinal: 0, opId: operation.id, kind: "append-comment",
                    target: { workItemId: 42, stagedIdentity: null }, summary: "Append plain discussion comment",
                    preconditions: [{ kind: "savedBenchId", value: context.benchId },
                        { kind: "expectedBenchContentsDigest", value: context.benchContentsDigest },
                        { kind: "expectedIdentityId", value: context.identityId }],
                    consequences: [{ kind: "discussion-append", bodyFormat: "plain", submittedText: text,
                        effectiveBody, field: null, to: null, relation: null, otherId: null }] }] } };
    const calls = [];
    const adapter = createNativeDiscussionPoster({ executable: fakeExecutable(),
        execFile(exe, args, options, callback) {
            calls.push({ kind: "preview", exe, args, options });
            callback(null, JSON.stringify(preview), "");
        },
        launch(exe, args, options, callback) {
            const call = { kind: args.includes("apply") ? "apply" : "prepare", exe, args, options };
            calls.push(call);
            const child = new EventEmitter();
            child.pid = 123;
            child.stdin = new EventEmitter();
            const finish = value => { callback(null, JSON.stringify(value)); child.emit("close", 0, null); };
            child.stdin.end = source => {
                call.input = JSON.parse(source);
                queueMicrotask(() => finish(native));
            };
            if (call.kind === "apply") queueMicrotask(() => finish({ digest, failed: false, error: null,
                operations: [{ ordinal: 0, opId: operation.id, state: "Verified", error: null, warning: null,
                    resultJson: missingReceipt ? null : JSON.stringify({ workItemId: 42,
                        commentId: "9007199254740993", body: effectiveBody, format: "html" }) }] }));
            return child;
        },
    });
    return { root, context, text, native, digest, calls, adapter,
        close: () => rm(root, { recursive: true, force: true }) };
}

test("native adapter keeps formatting native, writes exact proposal and applies only exact retained digest", async () => {
    const h = await setup();
    try {
        const prepared = await h.adapter.prepare({ context: h.context, text: h.text, format: "plain", workspacePath: h.root });
        assert.deepEqual(h.calls.map(value => value.kind), ["prepare", "preview"]);
        assert.deepEqual(h.calls[0].args, ["bench", "discussion", "prepare-post", "--stdin", "-o", "json"]);
        assert.deepEqual(h.calls[0].input, { version: 1, context: h.context, text: h.text, format: "plain" });
        assert.equal(await readFile(prepared.observation.acquisition.file, "utf8"), h.native.proposal);
        assert.equal(prepared.effectiveBody, "<p>&lt;script&gt;literal&lt;/script&gt;</p>\n");
        await assert.rejects(h.adapter.apply({ prepared, confirm: "0".repeat(64), workspacePath: h.root }));
        assert.equal(h.calls.length, 2);
        const outcome = await h.adapter.apply({ prepared, confirm: h.digest, workspacePath: h.root });
        assert.equal(outcome.status, "verified");
        assert.equal(outcome.commentId, "9007199254740993");
        assert.deepEqual(h.calls[2].args, ["proposal", "apply", "--file", prepared.observation.acquisition.file,
            "--confirm", h.digest, "-o", "json"]);
        assert.equal(h.calls[2].options.shell, false);
    } finally { await h.close(); }
});

test("native preparation mismatch and blocked preview cannot be promoted to Apply", async () => {
    const h = await setup({ blocked: true });
    try {
        const expected = { context: h.context, text: h.text, format: "plain", exitCode: 0 };
        for (const mutate of [
            value => { value.digest = "0".repeat(64); },
            value => { value.context.itemKey.workItemId = 99; },
            value => { value.proposal = value.proposal.replace("literal", "changed"); },
        ]) {
            const changed = structuredClone(h.native); mutate(changed);
            assert.throws(() => validateDiscussionPreparation(changed, expected));
        }
        const prepared = await h.adapter.prepare({ ...expected, workspacePath: h.root });
        assert.equal(prepared.canApply, false);
        await assert.rejects(h.adapter.apply({ prepared, confirm: h.digest, workspacePath: h.root }));
        assert.equal(h.calls.length, 2);
    } finally { await h.close(); }
});

test("Verified label without typed native receipt is not a verified discussion outcome", async () => {
    const h = await setup({ missingReceipt: true });
    try {
        const prepared = await h.adapter.prepare({ context: h.context, text: h.text, format: "plain", workspacePath: h.root });
        await assert.rejects(h.adapter.apply({ prepared, confirm: h.digest, workspacePath: h.root }));
        assert.equal(h.calls.filter(value => value.kind === "apply").length, 1);
    } finally { await h.close(); }
});

test("discussion bridge latches unknown without retry even after invalidation and reprepare", async () => {
    let attempts = 0;
    const bridge = createDiscussionPost({ workspacePath: "synthetic", adapter: {
        async prepare() { return { digest: "a".repeat(64), target: { workItemId: 42 }, canApply: true }; },
        async apply() { attempts++; throw new Error("Lost acknowledgment"); },
    } });
    const capture = await bridge.prepare({ context: {}, text: "exact", format: "plain" });
    await assert.rejects(bridge.apply({ captureHandle: capture.captureHandle, confirm: "b".repeat(64) }));
    assert.equal(attempts, 0);
    await assert.rejects(bridge.apply({ captureHandle: capture.captureHandle, confirm: capture.digest }), /unknown/);
    bridge.invalidate();
    await assert.rejects(bridge.prepare({ context: {}, text: "exact", format: "plain" }), /unresolved/);
    assert.equal(attempts, 1);
    assert.equal(bridge.outcomes[0].status, "outcome-unknown");
});
