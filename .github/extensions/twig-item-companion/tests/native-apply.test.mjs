import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import path from "node:path";
import test from "node:test";
import { createNativeProposalReader } from "../native-proposal.mjs";
import { createNativeProposalApplier, validateApplyRequest, validateApplyResult } from "../native-apply.mjs";
import { createReviewServer } from "../review-server.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";

const workspacePath = process.cwd();
const digest = "a".repeat(64);
const workspace = { organization: "synthetic", project: "synthetic" };
const input = { file: "proposal.json", digest, workspace };
const nativePreview = () => ({
    digest, canApply: true, issues: [], pendingChanges: [],
    operations: [{ ordinal: 0, id: "edit", kind: "Batch" }],
    reviewModel: {
        model: "twig.change-proposal.review", modelVersion: 1, digest, workspace,
        rationale: null, affectedItems: [], blockers: [], authorizationChoices: ["authorize-and-apply"],
        operations: [{ ordinal: 0, opId: "edit", kind: "batch", summary: "Change title",
            target: { workItemId: 42, stagedIdentity: null },
            preconditions: [{ kind: "expectedRevision", value: "7" }],
            consequences: [{ kind: "field-set", field: "System.Title", to: "Synthetic title", relation: null, otherId: null }] }],
    },
});
async function observe() {
    return createNativeProposalReader({ executable: fakeExecutable(), realpath: async (value) => value,
        execFile(_exe, _args, _options, callback) { callback(null, JSON.stringify(nativePreview()), ""); },
    })({ input, workspacePath });
}
const request = (observation) => ({ captureId: observation.expected.captureId, confirm: digest, authorize: "Synthetic reviewer" });
const receipt = () => ({ digest, failed: false, error: null,
    operations: [{ opId: "edit", ordinal: 0, kind: "Batch", state: "Verified", error: null, warning: null,
        resultJson: '{"revision":8}' }] });
function applier({ raw = receipt(), exit = 0, executable = fakeExecutable(), realpath = async (value) => value } = {}) {
    const calls = [];
    const apply = createNativeProposalApplier({ executable, realpath,
        stopTree: async (child) => { child.stopped = true; },
        launch(exe, args, options, callback) {
            calls.push({ exe, args, options });
            const child = new EventEmitter();
            child.pid = 123;
            queueMicrotask(() => {
                callback(exit === 0 ? null : Object.assign(new Error("Native refusal"), { code: exit }),
                    typeof raw === "string" ? raw : JSON.stringify(raw));
                child.emit("close", exit, null);
            });
            return child;
        },
    });
    return { apply, calls, executable };
}

test("Apply reuses owned runner, pinned executable and exact native argv, never preview or a shell", async () => {
    const observation = await observe();
    const { apply, calls, executable } = applier();
    const authority = { ...request(observation), rationale: "Reviewed the exact effects" };
    const result = await apply({ input: authority, observation, workspacePath });
    assert.equal(result.status, "verified");
    assert.deepEqual(result.native, receipt());
    assert.deepEqual(executable.prepared, ["proposal-apply"]);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].exe, FAKE_TWIG);
    assert.deepEqual(calls[0].args, ["proposal", "apply", "--file", path.join(workspacePath, input.file),
        "--confirm", digest, "--authorize", authority.authorize, "--rationale", authority.rationale, "-o", "json"]);
    assert.equal(calls[0].options.cwd, workspacePath);
    assert.equal(calls[0].options.shell, false);
    assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
    assert.equal(calls[0].options.timeout, undefined, "owned-tree deadline, not direct-child timeout");
});

test("no caller-supplied path, foreign capture/digest, blank identity or blocked preview can launch Apply", async () => {
    const observation = await observe();
    const { apply, calls } = applier();
    for (const authority of [{ ...request(observation), file: "other.json" }, { ...request(observation), authorize: "" },
        { ...request(observation), authorize: "bad\0identity" }, { ...request(observation), rationale: "" },
        { ...request(observation), confirm: "b".repeat(64) }, { ...request(observation), captureId: "foreign" }]) {
        await assert.rejects(apply({ input: authority, observation, workspacePath }), /Apply/);
    }
    for (const mutate of [
        (v) => { v.retained.envelope.canApply = false; },
        (v) => { v.retained.envelope.reviewModel.modelVersion = 2; },
        (v) => { v.retained.envelope.reviewModel.operations = []; },
        (v) => { v.retained.envelope.reviewModel.blockers.push({ kind: "blocked" }); },
        (v) => { v.retained.envelope.pendingChanges.push({ id: 1 }); },
        (v) => { v.retained.envelope.issues.push({ code: "invalid" }); },
    ]) {
        const changed = structuredClone(observation);
        mutate(changed);
        assert.throws(() => validateApplyRequest(request(observation), changed), /blocked or not executable/);
    }
    assert.equal(calls.length, 0);
});

test("changed physical file and refused executable prepare never launch a mutation", async () => {
    const observation = await observe();
    const moved = applier({ realpath: async (value) => value === workspacePath ? value : `${value}.moved` });
    await assert.rejects(moved.apply({ input: request(observation), observation, workspacePath }), /physical proposal file changed/);
    assert.equal(moved.calls.length, 0);
    const refused = applier({ executable: fakeExecutable({ refuse: new Error("Missing proposal-apply capability") }) });
    await assert.rejects(refused.apply({ input: request(observation), observation, workspacePath }), /Missing proposal-apply/);
    assert.equal(refused.calls.length, 0);
});

test("partial failure preserves full rows, warnings and unsettled effects; exit zero alone cannot claim verification", async () => {
    const observation = await observe();
    const failure = receipt();
    failure.failed = true;
    failure.error = "acknowledged-effect-unsettled";
    failure.operations[0].state = "Indeterminate";
    failure.operations[0].warning = "Inspect native journal";
    failure.unsettledAcknowledgments = { edit: '{"revision":8}' };
    const native = applier({ raw: failure, exit: 1 });
    const result = await native.apply({ input: request(observation), observation, workspacePath });
    assert.equal(result.status, "not-verified");
    assert.deepEqual(result.native, failure);
    assert.equal(native.calls.length, 1);
    const expected = { digest, operations: nativePreview().reviewModel.operations, exitCode: 0 };
    for (const value of [
        { ...receipt(), failed: true }, { ...receipt(), operations: [] }, { ...receipt(), digest: "b".repeat(64) },
        { ...receipt(), unsettledAcknowledgments: { edit: "unsettled" } },
        { ...receipt(), operations: [receipt().operations[0], receipt().operations[0]] },
        { ...receipt(), operations: [{ ...receipt().operations[0], opId: "foreign" }] },
    ]) assert.throws(() => validateApplyResult(value, expected), /Invalid|invalid|without complete/);
    const bad = applier({ raw: '{"digest":' });
    await assert.rejects(bad.apply({ input: request(observation), observation, workspacePath }), /outcome is unknown/);
    assert.equal(bad.calls.length, 1);
});

async function panel(applyNativeProposal) {
    const observation = await observe();
    let previews = 0;
    const server = await createReviewServer({ workspacePath, applyNativeProposal,
        readNativeProposal: async () => { previews++; return observation; } });
    const html = await (await fetch(server.url)).text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
    const state = async () => (await fetch(new URL("/api/state", server.url), { headers })).json();
    return { server, observation, headers, state, previews: () => previews };
}

test("HTTP acquisition/state are inert toward Apply; exact explicit authorization submits once and survives iframe reload", async () => {
    const native = applier();
    const { server, observation, headers, state, previews } = await panel(native.apply);
    try {
        await server.acquire(input);
        await fetch(server.url);
        await state();
        assert.equal(native.calls.length, 0);
        const url = new URL("/api/apply", server.url);
        const authority = request(observation);
        const send = (body, extra = {}) => fetch(url, { method: "POST", headers: { ...headers, ...extra }, body: JSON.stringify(body) });
        assert.equal((await send(authority, { Origin: "https://untrusted.invalid" })).status, 403);
        assert.equal((await send({ ...authority, captureId: "foreign" })).status, 400);
        assert.equal(native.calls.length, 0);
        assert.equal((await send(authority)).status, 200);
        await fetch(server.url);
        assert.equal((await state()).applyAttempt.status, "verified");
        assert.equal((await send(authority)).status, 400);
        assert.equal(native.calls.length, 1);
        assert.equal(previews(), 1);
    } finally { await server.close(); }
});

test("single-flight blocks replacement/repeat and transport failure consumes capture without hiding the unknown outcome", async () => {
    let release, signal;
    const { server, observation, state } = await panel(async (value) => {
        signal = value.signal;
        return new Promise((_, reject) => { release = reject; });
    });
    try {
        await server.acquire(input);
        const pending = server.apply(request(observation));
        await assert.rejects(server.apply(request(observation)), /already in progress/);
        await assert.rejects(server.acquire(input), /already in progress/);
        release(new Error("Lost native response"));
        await assert.rejects(pending, /Lost native response/);
        const snapshot = await state();
        assert.equal(snapshot.applyAttempt.status, "outcome-unknown");
        assert.match(snapshot.error, /Lost native response/);
        assert.ok(snapshot.observation, "reviewed evidence remains inspectable");
        await assert.rejects(server.apply(request(observation)), /already submitted/);
        assert.equal(signal.aborted, false);
    } finally { await server.close(); }
});

test("close aborts Apply and suppresses late success without claiming rollback", async () => {
    let release, signal;
    const { server, observation } = await panel(async (value) => {
        signal = value.signal;
        return new Promise((resolve) => { release = resolve; });
    });
    await server.acquire(input);
    const pending = server.apply(request(observation));
    await server.close();
    assert.equal(signal.aborted, true);
    release({ status: "verified" });
    await assert.rejects(pending, /closed or superseded.*unknown/);
});
