import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { request as httpRequest } from "node:http";
import test from "node:test";
import { createEditingBridge } from "../editing-bridge.mjs";
import { createCanvasServer } from "../canvas-server.mjs";

const detail = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
function nativeDocument() {
    return {
        nativeContext: {
            itemKey: detail.itemKey, externalIdentity: { providerKind: "ado", resourceScope: "fixture-resource", nativeId: "42" },
            benchId: "7", benchName: "Fixture bench", benchContentsDigest: "digest", identityId: "fixture-identity",
            bindingRevision: "1", selectionSource: "checkout", selectionRevision: "1", attachmentRevision: "1",
            storageGeneration: null, originDigest: "origin", evidenceDigest: "evidence",
        },
        generation: "9007199254740993", baselineRevision: "17", observedRevision: "17", currentRevision: "17",
        hasConflicts: false, changes: [], conflicts: [],
        systemControls: structuredClone(detail.document.systemControls), pages: structuredClone(detail.document.pages),
        fields: [{ fieldRef: "System.Title", editable: true }, { fieldRef: "Custom.Body", editable: true }],
        authorized: false, serverFinalAuthority: true,
    };
}
function setup(overrides = {}) {
    const calls = [];
    let document = nativeDocument();
    const adapter = {
        async open(input) {
            calls.push({ action: "open", input });
            return { version: 1, status: "opened", committed: true, document: structuredClone(document) };
        },
        async stage(input) {
            calls.push({ action: "stage", input });
            document = { ...document, generation: (BigInt(input.request.generation) + BigInt(input.request.changes.length)).toString(),
                changes: input.request.changes.map((change) => ({ ...change, baselineValue: null })) };
            return { version: 1, status: "staged", committed: true, document: structuredClone(document) };
        },
        async retain(input) {
            calls.push({ action: "retain", input });
            return retained(input.request);
        },
        ...overrides,
    };
    const target = { workItemId: 42, expectedKey: detail.itemKey, identityId: detail.identityId, bench: "7" };
    return { adapter, calls, target, bridge: createEditingBridge({ adapter, workspacePath: "fixture" }) };
}

function retained(request) {
    const canonicalJson = '{"opaqueNativeClaim":900719925474099312345}';
    const { version, ...captureContext } = structuredClone(request);
    return { version: 1, status: "retained", committed: true, capture: {
        ...captureContext, currentRevision: request.observedRevision,
        digest: createHash("sha256").update(canonicalJson).digest("hex"), canonicalJson,
        operationId: "draft-" + "b".repeat(64),
        effects: [{ fieldRef: "System.Title", baselineValue: null, proposedValue: "" }],
        originCoverage: "editing-context-and-native-origin-digest", authorized: false, canApply: false,
    } };
}
async function stageForRetain(h) {
    const opened = await h.bridge.open(h.target);
    return h.bridge.stage({ captureHandle: opened.captureHandle,
        changes: [{ fieldRef: "System.Title", proposedValue: "" }] });
}

test("RETAIN derives exact context and counters server-side and consumes handle with receipt-only projection", async () => {
    const h = setup();
    const staged = await stageForRetain(h);
    assert.equal(staged.canRetain, true);
    const result = await h.bridge.retain({ captureHandle: staged.captureHandle });
    const request = h.calls[2].input.request;
    assert.deepEqual(Object.keys(request).sort(), ["baselineRevision", "generation", "nativeContext", "observedRevision", "version"]);
    assert.equal(request.generation, "9007199254740994");
    assert.deepEqual(request.nativeContext, nativeDocument().nativeContext);
    assert.equal(result.committed, true);
    assert.equal(result.canStage, false);
    assert.equal(result.canRetain, false);
    assert.deepEqual(result.receipt.effects, [{ fieldRef: "System.Title", baselineValue: null, proposedValue: "" }]);
    assert.equal(result.receipt.canonicalJson, undefined);
    assert.equal(result.receipt.nativeContext, undefined);
    assert.equal(result.capture, undefined);
    await assert.rejects(h.bridge.retain({ captureHandle: staged.captureHandle }), /server-held/);
    await assert.rejects(h.bridge.stage({ captureHandle: staged.captureHandle, changes: [] }), /server-held/);
});

test("RETAIN refuses browser authority, empty/conflicted drafts and revoked handles before adapter invocation", async () => {
    for (const extra of [{ nativeContext: {} }, { generation: "1" }, { digest: "a".repeat(64) },
        { changes: [] }, { authorizer: "human" }, { canRetain: true }]) {
        const h = setup();
        const staged = await stageForRetain(h);
        await assert.rejects(h.bridge.retain({ captureHandle: staged.captureHandle, ...extra }), /server-held/);
        assert.equal(h.calls.length, 2);
    }
    const h = setup();
    const opened = await h.bridge.open(h.target);
    assert.equal(opened.canRetain, false);
    await assert.rejects(h.bridge.retain({ captureHandle: opened.captureHandle }), /effective staged/);
    h.bridge.invalidate();
    await assert.rejects(h.bridge.retain({ captureHandle: opened.captureHandle }), /server-held/);
    assert.equal(h.calls.length, 1);
    const conflict = setup({ open: async () => {
        const document = nativeDocument();
        document.observedRevision = document.currentRevision = "18";
        document.hasConflicts = true;
        document.changes = [{ fieldRef: "System.Title", baselineValue: null, proposedValue: "" }];
        return { version: 1, status: "opened", committed: true, document };
    } });
    const drift = await conflict.bridge.open(conflict.target);
    await assert.rejects(conflict.bridge.retain({ captureHandle: drift.captureHandle }), /effective staged/);
});

test("RETAIN uncertainty holds the exact target across reopen while a known refusal permits explicit reopen", async () => {
    for (const kind of ["unknown", "indeterminate", "indeterminate-with-capture", "refused"]) {
        let retains = 0;
        const h = setup({ retain: async ({ request }) => {
            retains++;
            if (kind === "unknown") throw new Error("Missing stdout; unknown outcome");
            if (kind === "refused") return { version: 1, status: "refused", committed: false,
                error: { code: "draft-empty", message: "No effective draft", recovery: "Reopen" } };
            return { version: 1, status: "indeterminate", committed: true,
                ...(kind.endsWith("capture") ? { capture: retained(request).capture } : {}),
                error: { code: "post-commit-fence", message: "Committed then fenced", recovery: "Inspect" } };
        } });
        const staged = await stageForRetain(h);
        try { await h.bridge.retain({ captureHandle: staged.captureHandle }); }
        catch (error) { assert.equal(kind, "unknown"); assert.match(error.message, /Missing stdout/); }
        const outcome = h.bridge.outcome;
        assert.equal(outcome.inspectFirst, kind !== "refused");
        if (kind.startsWith("indeterminate")) assert.equal(outcome.committed, true);
        if (kind.endsWith("capture")) assert.ok(outcome.receipt);
        if (kind !== "refused") assert.match(outcome.recovery, /journal/);
        const openCount = h.calls.length;
        const reopened = await h.bridge.open(h.target);
        assert.equal(reopened.status, kind === "refused" ? "opened" : outcome.status);
        assert.equal(h.calls.length, openCount + (kind === "refused" ? 1 : 0));
        assert.equal(retains, 1);
        assert.equal(h.bridge.retainOutcomes.length, 1);
    }
});

test("late RETAIN committed evidence survives invalidation while ownership fences replacement and targets remain tagged", async () => {
    let release;
    let aborted = false;
    const h = setup({ retain: async ({ request, signal }) => {
        await new Promise(resolve => {
            release = resolve;
            signal.addEventListener("abort", () => { aborted = true; }, { once: true });
        });
        return { version: 1, status: "indeterminate", committed: true, capture: retained(request).capture,
            error: { code: "post-commit-fence", message: "Late receipt", recovery: "Inspect" } };
    } });
    const staged = await stageForRetain(h);
    const pending = h.bridge.retain({ captureHandle: staged.captureHandle });
    let idle = false;
    const idlePending = h.bridge.awaitIdle().then(() => { idle = true; });
    h.bridge.invalidate();
    assert.equal(aborted, true);
    assert.equal(h.bridge.busy, true);
    await Promise.resolve();
    assert.equal(idle, false);
    assert.throws(() => h.bridge.open(h.target), /already in progress/);
    release();
    const late = await pending;
    await idlePending;
    assert.equal(late.committed, true);
    assert.ok(late.receipt);
    const other = { ...h.target, identityId: "other-identity" };
    const snapshot = h.bridge.retainOutcomes;
    assert.equal(snapshot[0].identityId, h.target.identityId);
    snapshot[0].receipt.effects.length = 0;
    assert.equal(h.bridge.retainOutcomes[0].receipt.effects.length, 1);
    await assert.rejects(h.bridge.open(other), /identity changed/);
    const held = await h.bridge.open(h.target);
    assert.equal(held.inspectFirst, true);
    assert.equal(held.receipt.effects.length, 1);
});

test("explicit OPEN/STAGE keep context/counters server-held and rotate single-use capture handles", async () => {
    const { bridge, calls, target } = setup();
    assert.equal(calls.length, 0);
    const opened = await bridge.open(target);
    assert.equal(opened.captureHandle.length, 43);
    assert.equal(opened.nativeContext, undefined);
    assert.equal(opened.generation, undefined);
    assert.deepEqual(opened.editors.map(field => field.fieldRef), ["System.Title"]);
    const changes = [{ fieldRef: "System.Title", proposedValue: "" }];
    const staged = await bridge.stage({ captureHandle: opened.captureHandle, changes });
    assert.notEqual(staged.captureHandle, opened.captureHandle);
    assert.equal(calls[1].input.request.generation, "9007199254740993");
    assert.deepEqual(calls[1].input.request.nativeContext, nativeDocument().nativeContext);
    assert.deepEqual(calls[1].input.request.changes, changes);
    await assert.rejects(bridge.stage({ captureHandle: opened.captureHandle, changes }), /server-held/);
    assert.equal(calls.length, 2);
});

test("forged counters/context, unsupported controls, duplicate intent and invalidated handles cannot stage", async () => {
    for (const malicious of [
        { generation: "1" }, { nativeContext: {} },
        { changes: [{ fieldRef: "Custom.Body", proposedValue: "unsupported HTML editor" }] },
        { changes: [{ fieldRef: "System.Title", proposedValue: null }, { fieldRef: "system.title", proposedValue: "" }] },
    ]) {
        const { bridge, calls, target } = setup();
        const opened = await bridge.open(target);
        await assert.rejects(bridge.stage({ captureHandle: opened.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: null }], ...malicious }));
        assert.equal(calls.length, 1);
    }
    const { bridge, target, calls } = setup();
    const opened = await bridge.open(target);
    bridge.invalidate();
    await assert.rejects(bridge.stage({ captureHandle: opened.captureHandle, changes: [{ fieldRef: "System.Title", proposedValue: null }] }));
    assert.equal(calls.length, 1);
});

test("native revision drift disables stage; unknown transport and indeterminate require explicit reopen", async () => {
    const drift = setup({ async open() {
        const document = nativeDocument();
        document.observedRevision = document.currentRevision = "18";
        document.hasConflicts = true;
        return { version: 1, status: "opened", committed: true, document };
    } });
    const opened = await drift.bridge.open(drift.target);
    assert.equal(opened.canStage, false);
    await assert.rejects(drift.bridge.stage({ captureHandle: opened.captureHandle, changes: [{ fieldRef: "System.Title", proposedValue: "" }] }), /drift/);
    for (const stage of [
        async () => { throw new Error("Missing native envelope / unknown subcommand"); },
        async () => ({ version: 1, status: "indeterminate", committed: true,
            error: { code: "origin-changed", message: "Committed then fenced", recovery: "Reopen" } }),
    ]) {
        const { bridge, target } = setup({ stage });
        const capture = await bridge.open(target);
        try { await bridge.stage({ captureHandle: capture.captureHandle, changes: [{ fieldRef: "System.Title", proposedValue: null }] }); }
        catch (error) { assert.match(error.message, /Missing native envelope/); }
        assert.equal(bridge.outcome.canStage, false);
        await assert.rejects(bridge.stage({ captureHandle: capture.captureHandle, changes: [{ fieldRef: "System.Title", proposedValue: "" }] }), /server-held/);
    }
});

test("invalidation aborts ownership but does not admit a second operation before closure; unknown outcome is cached", async () => {
    let release;
    let aborted = false;
    const { bridge, target } = setup({ stage: async ({ signal }) => {
        await new Promise(resolve => {
            release = resolve;
            signal.addEventListener("abort", () => { aborted = true; }, { once: true });
        });
        throw new Error("Owned child closed after cancellation");
    } });
    const opened = await bridge.open(target);
    const staging = bridge.stage({ captureHandle: opened.captureHandle, changes: [{ fieldRef: "System.Title", proposedValue: "" }] });
    bridge.invalidate();
    assert.equal(aborted, true);
    assert.equal(bridge.busy, true);
    assert.throws(() => bridge.open(target), /already in progress/);
    release();
    await assert.rejects(staging, /Owned child closed/);
    assert.equal(bridge.busy, false);
    assert.equal(bridge.outcome.status, "unknown");
});

test("wrong OPEN identity and contradictory STAGE acknowledgement never acquire usable authority", async () => {
    const wrongIdentity = setup({ open: async () => {
        const document = nativeDocument();
        document.nativeContext.identityId = "other-identity";
        return { version: 1, status: "opened", committed: true, document };
    } });
    await assert.rejects(wrongIdentity.bridge.open(wrongIdentity.target), /identity changed/);
    for (const field of ["generation", "baselineRevision", "observedRevision", "currentRevision"]) {
        const h = setup({ stage: async () => {
            const document = nativeDocument();
            document.generation = "9007199254740994";
            document[field] = "20";
            document.hasConflicts = document.baselineRevision !== document.observedRevision;
            return { version: 1, status: "staged", committed: true, document };
        } });
        const opened = await h.bridge.open(h.target);
        await assert.rejects(h.bridge.stage({ captureHandle: opened.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "" }] }), /acknowledgement/);
        assert.equal(h.bridge.outcome.status, "unknown");
        assert.equal(h.bridge.outcome.canStage, false);
    }
});

async function httpSetup(overrides) {
    const native = setup(overrides);
    const bench = JSON.parse(await readFile(new URL("./fixtures/bench-v2.json", import.meta.url), "utf8")).browser;
    const server = await createCanvasServer({ workspacePath: "fixture", workbench: true,
        nativeEditing: native.adapter, readNativeBench: async () => bench, readNativeBenchDetail: async () => detail });
    try {
        const html = await (await fetch(server.url)).text();
        const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
        const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json", Origin: new URL(server.url).origin };
        const post = (route, body, extra = {}) => fetch(new URL(route, server.url), {
            method: "POST", headers: { ...headers, ...extra }, body: JSON.stringify(body),
        });
        await server.loadBench("7");
        await server.loadBenchDetail(42);
        return { ...native, server, post, headers };
    } catch (error) {
        try { await server.close(); }
        catch (cleanupError) { throw new AggregateError([error, cleanupError], "Editing HTTP setup and server cleanup both failed."); }
        throw error;
    }
}

test("loopback editing routes reuse token/origin guards, forbid browser authority and preserve unrelated 4KiB cap", async () => {
    const h = await httpSetup();
    try {
        assert.equal(h.calls.length, 0);
        assert.equal((await h.post("/api/editing", { action: "open" }, { Authorization: "" })).status, 401);
        assert.equal((await h.post("/api/editing", { action: "open" }, { Origin: "https://invalid.example" })).status, 403);
        assert.equal((await h.post("/api/editing", { action: "open", nativeContext: {} })).status, 400);
        for (const body of ['{"action":"open","action":"stage"}',
            '{"action":"open","\\u0061ction":"open"}', '{"action":', '{"action":"open","extra":true}']) {
            const response = await fetch(new URL("/api/editing", h.server.url), { method: "POST", headers: h.headers, body });
            assert.equal(response.status, 400);
        }
        assert.equal(h.calls.length, 0);
        const opened = await (await h.post("/api/editing", { action: "open" })).json();
        const body = { action: "stage", captureHandle: opened.editing.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "x".repeat(8192) }] };
        assert.equal((await h.post("/api/editing", body)).status, 200);
        assert.equal((await h.post("/api/bench", { action: "refresh", padding: "x".repeat(8192) })).status, 413);
        assert.equal((await h.post("/api/editing", { ...body, padding: "x".repeat(262144) })).status, 413);
    } finally { await h.server.close(); }
});

test("HTTP RETAIN admits only exact handle body and exposes capture-only target-tagged outcomes", async () => {
    const h = await httpSetup();
    try {
        assert.equal((await h.post("/api/editing", { action: "retain", captureHandle: "fake" },
            { Authorization: "" })).status, 401);
        assert.equal((await h.post("/api/editing", { action: "retain", captureHandle: "fake" },
            { Origin: "https://invalid.example" })).status, 403);
        const opened = (await (await h.post("/api/editing", { action: "open" })).json()).editing;
        const staged = (await (await h.post("/api/editing", { action: "stage", captureHandle: opened.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "" }] })).json()).editing;
        for (const extra of [{ nativeContext: {} }, { changes: [] }, { digest: "a" }, { authorize: true }]) {
            assert.equal((await h.post("/api/editing", { action: "retain", captureHandle: staged.captureHandle,
                ...extra })).status, 400);
        }
        const response = await h.post("/api/editing", { action: "retain", captureHandle: staged.captureHandle });
        assert.equal(response.status, 200);
        const value = (await response.json()).editing;
        assert.equal(value.status, "retained");
        assert.equal(value.receipt.canonicalJson, undefined);
        const state = await (await fetch(new URL("/api/state", h.server.url), { headers: h.headers })).json();
        assert.equal(state.retainOutcomes[0].identityId, detail.identityId);
        assert.deepEqual(state.retainOutcomes[0].receipt, value.receipt);
        assert.doesNotMatch(JSON.stringify(state.retainOutcomes), /canonicalJson|nativeContext|authorizer/);
    } finally { await h.server.close(); }
});

test("HTTP RETAIN disconnect retains busy until settlement and preserves optional postcommit receipt", async () => {
    let release, markEntered, markAborted;
    const entered = new Promise(resolve => { markEntered = resolve; });
    const aborted = new Promise(resolve => { markAborted = resolve; });
    const h = await httpSetup({ retain: async ({ request, signal }) => {
        await new Promise(resolve => {
            release = resolve;
            signal.addEventListener("abort", markAborted, { once: true });
            markEntered();
        });
        return { version: 1, status: "indeterminate", committed: true, capture: retained(request).capture,
            error: { code: "post-commit-fence", message: "Native committed", recovery: "Inspect" } };
    } });
    try {
        const opened = (await (await h.post("/api/editing", { action: "open" })).json()).editing;
        const staged = (await (await h.post("/api/editing", { action: "stage", captureHandle: opened.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "" }] })).json()).editing;
        const req = httpRequest(new URL("/api/editing", h.server.url), { method: "POST", headers: h.headers });
        req.on("error", () => {});
        req.end(JSON.stringify({ action: "retain", captureHandle: staged.captureHandle }));
        await entered;
        req.destroy();
        await aborted;
        const pendingState = await (await fetch(new URL("/api/state", h.server.url), { headers: h.headers })).json();
        assert.equal(pendingState.editingBusy, true);
        release();
        let state;
        for (let i = 0; i < 100; i++) {
            state = await (await fetch(new URL("/api/state", h.server.url), { headers: h.headers })).json();
            if (!state.editingBusy) break;
            await new Promise(resolve => setTimeout(resolve, 5));
        }
        assert.equal(state.editingBusy, false);
        assert.equal(state.retainOutcomes[0].committed, true);
        assert.equal(state.retainOutcomes[0].inspectFirst, true);
        assert.ok(state.retainOutcomes[0].receipt);
        await h.server.close();
    } finally { release?.(); await h.server.close(); }
});

test("HTTP RETAIN unknown hold survives invalidation and reloaded canonical Bench detail without another native OPEN", async () => {
    let retains = 0;
    const h = await httpSetup({ retain: async () => {
        retains++;
        throw new Error("Transport outcome unknown");
    } });
    try {
        const opened = (await (await h.post("/api/editing", { action: "open" })).json()).editing;
        const staged = (await (await h.post("/api/editing", { action: "stage", captureHandle: opened.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "" }] })).json()).editing;
        assert.equal((await h.post("/api/editing", { action: "retain",
            captureHandle: staged.captureHandle })).status, 500);
        const nativeOpenCount = h.calls.filter(call => call.action === "open").length;
        await h.post("/api/editing", { action: "invalidate" });
        await h.server.loadBenchDetail(42);
        const held = (await (await h.post("/api/editing", { action: "open" })).json()).editing;
        assert.equal(held.status, "unknown");
        assert.equal(held.inspectFirst, true);
        assert.equal(held.benchId, detail.benchId, "canonical native Bench ID remains the hold key");
        assert.equal(h.calls.filter(call => call.action === "open").length, nativeOpenCount);
        assert.equal(retains, 1);
        assert.equal(held.canRetain, false);
        assert.match(held.recovery, /journal/);
    } finally { await h.server.close(); }
});

test("actual server close during RETAIN waits for settlement and returns late committed optional receipt", async () => {
    for (const includeCapture of [false, true]) {
        let release, markEntered, markAborted;
        const entered = new Promise(resolve => { markEntered = resolve; });
        const aborted = new Promise(resolve => { markAborted = resolve; });
        const h = await httpSetup({ retain: async ({ request, signal }) => {
            await new Promise(resolve => {
                release = resolve;
                signal.addEventListener("abort", markAborted, { once: true });
                markEntered();
            });
            return { version: 1, status: "indeterminate", committed: true,
                ...(includeCapture ? { capture: retained(request).capture } : {}),
                error: { code: "post-commit-fence", message: "Committed before close", recovery: "Inspect" } };
        } });
        try {
            const opened = (await (await h.post("/api/editing", { action: "open" })).json()).editing;
            const staged = (await (await h.post("/api/editing", { action: "stage",
                captureHandle: opened.captureHandle,
                changes: [{ fieldRef: "System.Title", proposedValue: "" }] })).json()).editing;
            const pending = h.post("/api/editing", { action: "retain", captureHandle: staged.captureHandle });
            await entered;
            let closed = false;
            const closing = h.server.close().then(() => { closed = true; });
            await aborted;
            await new Promise(resolve => setTimeout(resolve, 25));
            assert.equal(closed, false, "server close cannot outpace native RETAIN settlement");
            const secondClose = h.server.close();
            release();
            const outcome = (await (await pending).json()).editing;
            await Promise.all([closing, secondClose]);
            assert.equal(closed, true);
            assert.equal(outcome.committed, true);
            assert.equal(outcome.inspectFirst, true);
            assert.equal(outcome.itemKey.workItemId, detail.itemKey.workItemId);
            assert.equal(outcome.identityId, detail.identityId);
            assert.equal(Boolean(outcome.receipt), includeCapture);
            if (includeCapture) assert.equal(outcome.receipt.effects[0].proposedValue, "");
            assert.equal(outcome.canRetain, false);
        } finally { release?.(); await h.server.close(); }
    }
});

test("client disconnect aborts native work, retains busy until closure, and exposes unknown outcome after closure", async () => {
    let release;
    let aborted;
    const abortSeen = new Promise(resolve => { aborted = resolve; });
    const h = await httpSetup({ stage: async ({ signal }) => {
        await new Promise(resolve => {
            release = resolve;
            signal.addEventListener("abort", aborted, { once: true });
        });
        throw new Error("Disconnected native operation reaped");
    } });
    try {
        const opened = await (await h.post("/api/editing", { action: "open" })).json();
        const req = httpRequest(new URL("/api/editing", h.server.url), { method: "POST", headers: h.headers });
        req.on("error", () => {}); // This test deliberately destroys its client socket.
        req.end(JSON.stringify({ action: "stage", captureHandle: opened.editing.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "" }] }));
        for (let i = 0; i < 100 && !release; i++) await new Promise(resolve => setTimeout(resolve, 5));
        assert.ok(release, "owned stage entered");
        req.destroy();
        await Promise.race([abortSeen, new Promise((_, reject) => setTimeout(() => reject(new Error("Disconnect did not abort")), 1000))]);
        assert.equal((await h.post("/api/editing", { action: "open" })).status, 500);
        release();
        let state;
        for (let i = 0; i < 100; i++) {
            state = await (await fetch(new URL("/api/state", h.server.url), { headers: h.headers })).json();
            if (!state.editingBusy) break;
            await new Promise(resolve => setTimeout(resolve, 5));
        }
        assert.equal(state.editingBusy, false);
        assert.equal(state.editingOutcome.status, "unknown");
        assert.equal(state.editingOutcome.canStage, false);
    } finally { release?.(); await h.server.close(); }
});

test("panel close after client disconnect awaits owned adapter settlement even without an HTTP connection", async () => {
    let release;
    let markEntered;
    let markAborted;
    const entered = new Promise(resolve => { markEntered = resolve; });
    const aborted = new Promise(resolve => { markAborted = resolve; });
    const h = await httpSetup({ stage: async ({ signal }) => {
        await new Promise(resolve => {
            release = resolve;
            signal.addEventListener("abort", markAborted, { once: true });
            markEntered();
        });
        throw new Error("Disconnected child reaped");
    } });
    try {
        const opened = await (await h.post("/api/editing", { action: "open" })).json();
        const req = httpRequest(new URL("/api/editing", h.server.url), { method: "POST", headers: h.headers });
        req.on("error", () => {});
        req.end(JSON.stringify({ action: "stage", captureHandle: opened.editing.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "" }] }));
        await entered;
        req.destroy();
        await aborted;
        let closed = false;
        const closing = h.server.close().then(() => { closed = true; });
        await new Promise(resolve => setTimeout(resolve, 25));
        assert.equal(closed, false, "no client socket does not mean the owned native invocation has settled");
        const secondClose = h.server.close();
        release();
        await Promise.all([closing, secondClose]);
        assert.equal(closed, true);
    } finally { release?.(); await h.server.close(); }
});
