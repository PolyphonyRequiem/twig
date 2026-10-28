import assert from "node:assert/strict";
import { readFile, realpath, stat } from "node:fs/promises";
import path from "node:path";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createNativeDraftReader, validateDraftDocument as validateNative } from "../native-draft.mjs";
import { validateDraftDocument } from "../draft-document.mjs";
import { renderPage } from "../render.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";

const workspacePath = process.cwd();
const key = { checkoutRoot: workspacePath, connectionId: "fictional-connection", bindingId: "fictional-binding", workItemId: 42 };
const detailFixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
function detail(workItemId = 42) {
    const value = structuredClone(detailFixture);
    value.itemKey = { ...key, workItemId };
    value.item.id = value.document.workItemId = workItemId;
    return value;
}
function draft() {
    return {
        draftDocumentVersion: 1, itemKey: { ...key }, generation: 1,
        baselineRevision: 3, observedRevision: 3, currentRevision: 3,
        draftStatus: "empty", status: "current", reconciliationState: "current",
        hasConflicts: false, localPendingWork: false, freshness: "cached",
        observationCompleteness: "complete-for-draft-effects", changes: [], conflicts: [], fields: [],
        eligibility: { fieldsPersistable: true, rules: "not-applicable", rulesReason: null,
            serverIsFinalAuthority: true, authorized: false, inspectionOnly: true },
    };
}
function fakeReader(calls, payload = draft()) {
    return createNativeDraftReader({ executable: fakeExecutable(),
        realpath: async (value) => value,
        execFile(exe, args, options, callback) {
            calls.push({ exe, args, options });
            callback(null, JSON.stringify(payload), "");
        },
    });
}
async function openServer(readNativeDraft, readNativeDetail = async ({ workItemId }) => detail(workItemId)) {
    const server = await createCanvasServer({ workspacePath, readNativeDraft, readNativeDetail });
    const html = await (await fetch(server.url)).text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const state = async () => {
        const response = await fetch(new URL("/api/state", server.url), { headers: { Authorization: `Bearer ${token}` } });
        assert.equal(response.status, 200);
        return response.json();
    };
    return { server, html, state, token };
}
function deferred() {
    let resolve;
    const promise = new Promise((r) => { resolve = r; });
    return { promise, resolve };
}

test("open, state, detail and refresh never inspect; only explicit inspection uses retained key and bounded fake process", async () => {
    const calls = [];
    const { server, state } = await openServer(fakeReader(calls));
    try {
        assert.equal((await state()).draftDocument, null);
        await assert.rejects(server.inspectDraft(), /Load native item detail/);
        await server.load(42);
        await server.refresh();
        assert.equal(calls.length, 0);
        const document = await server.inspectDraft();
        assert.deepEqual(document.itemKey, key);
        assert.equal(calls.length, 1);
        assert.deepEqual(calls[0].args, ["draft", "show", "42", "--document", "-o", "json"]);
        assert.equal(calls[0].options.cwd, workspacePath);
        assert.equal(calls[0].options.shell, false);
        assert.equal(calls[0].options.timeout, 15000);
        assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
        assert.deepEqual((await state()).draftDocument, document);
        await state();
        assert.equal(calls.length, 1);
    } finally { await server.close(); }
});

test("item changes, refresh and detail refusal clear retained draft without inspecting again", async () => {
    const calls = [];
    let refuse = false;
    const { server, state } = await openServer(fakeReader(calls), async ({ workItemId }) => {
        if (refuse) throw new Error("actual detail admission refusal");
        return detail(workItemId);
    });
    try {
        await server.load(42);
        await server.inspectDraft();
        await server.refresh();
        assert.equal((await state()).draftDocument, null);
        await server.inspectDraft();
        refuse = true;
        await assert.rejects(server.load(43), /actual detail admission refusal/);
        assert.equal((await state()).draftDocument, null);
        refuse = false;
        await server.load(43);
        assert.equal((await state()).draftDocument, null);
        assert.equal(calls.length, 2);
    } finally { await server.close(); }
});

test("inspection refusal and contradictory identity clear prior draft and expose actual failure", async () => {
    let mode = "valid";
    const { server, state } = await openServer(async () => {
        if (mode === "refuse") throw new Error("native rules observation refused");
        const document = draft();
        if (mode === "wrong-key") document.itemKey.bindingId = "different";
        return document;
    });
    try {
        await server.load(42);
        await server.inspectDraft();
        mode = "refuse";
        await assert.rejects(server.inspectDraft(), /native rules observation refused/);
        assert.equal((await state()).draftDocument, null);
        assert.equal((await state()).draftError, "native rules observation refused");
        mode = "wrong-key";
        await assert.rejects(server.inspectDraft(), /qualified item identity changed/);
        assert.equal((await state()).draftDocument, null);
    } finally { await server.close(); }
});

test("cancelled late inspection cannot republish after detail change; commands remain serialized until settlement", async () => {
    const pending = deferred();
    let signal;
    const { server, state } = await openServer(async (input) => { signal = input.signal; return pending.promise; });
    try {
        await server.load(42);
        const inspection = server.inspectDraft();
        const rejected = assert.rejects(inspection, /superseded or closed/);
        await assert.rejects(server.load(43), /already in progress/);
        assert.equal(signal.aborted, true);
        pending.resolve(draft());
        await rejected;
        await server.load(43);
        assert.equal((await state()).observation.itemKey.workItemId, 43);
        assert.equal((await state()).draftDocument, null);
        assert.equal((await state()).draftError, null);
    } finally { pending.resolve(draft()); await server.close(); }
});

test("close cancels pending inspection and its late result cannot survive", async () => {
    const pending = deferred();
    let signal;
    const { server } = await openServer(async (input) => { signal = input.signal; return pending.promise; });
    await server.load(42);
    const inspection = server.inspectDraft();
    const rejected = assert.rejects(inspection, /superseded or closed/);
    await server.close();
    assert.equal(signal.aborted, true);
    pending.resolve(draft());
    await rejected;
    await assert.rejects(server.inspectDraft(), /closed/);
});

test("a competing explicit inspection cancels but never overlaps the unsettled native operation", async () => {
    const pending = deferred();
    let count = 0;
    let signal;
    const { server, state } = await openServer(async (input) => {
        count++;
        signal = input.signal;
        return pending.promise;
    });
    try {
        await server.load(42);
        const inspection = server.inspectDraft();
        const rejected = assert.rejects(inspection, /superseded or closed/);
        await assert.rejects(server.inspectDraft(), /already in progress/);
        assert.equal(signal.aborted, true);
        assert.equal(count, 1);
        pending.resolve(draft());
        await rejected;
        assert.equal((await state()).draftDocument, null);
        assert.match((await state()).draftError, /already in progress/);
    } finally { pending.resolve(draft()); await server.close(); }
});

test("no HTTP inspection route or workbench draft acquisition is added", async () => {
    const calls = [];
    const { server, token, state } = await openServer(fakeReader(calls));
    try {
        await server.load(42);
        for (const route of ["/api/draft", "/api/inspect_draft"]) {
            const response = await fetch(new URL(route, server.url), {
                method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: "{}",
            });
            assert.equal(response.status, 404);
        }
        assert.equal((await state()).draftDocument, null);
        assert.equal(calls.length, 0);
    } finally { await server.close(); }
    const bench = await createCanvasServer({ workspacePath, workbench: true, readNativeDraft: fakeReader(calls) });
    try { await assert.rejects(bench.inspectDraft(), /unavailable/); }
    finally { await bench.close(); }
    assert.equal(calls.length, 0);
});

test("browser-safe extraction preserves ordinal identity, numeric/authority checks and unknown-member retention", () => {
    const original = draft();
    original.advisoryFutureMember = { untouched: true };
    assert.equal(validateDraftDocument(original, key), original);
    assert.equal(validateNative(original, key, workspacePath), original);
    for (const mutate of [
        (v) => { v.itemKey.checkoutRoot = path.join(workspacePath, "child") + path.sep + ".."; },
        (v) => { v.currentRevision = Number.MAX_SAFE_INTEGER + 1; },
        (v) => { v.observedRevision = v.currentRevision = 4; },
        (v) => { delete v.eligibility.inspectionOnly; },
        (v) => { v.eligibility.authorized = true; },
        (v) => { delete v.fields; },
    ]) {
        const document = draft();
        mutate(document);
        assert.throws(() => validateDraftDocument(document, key), /unavailable/);
        assert.throws(() => validateNative(document, key, workspacePath), /unavailable/);
    }
    assert.throws(() => validateNative(draft(), key, path.join(workspacePath, "elsewhere")), /qualified item key/);
});

class Element {
    children = [];
    textContent = "";
    disabled = false;
    dataset = {};
    classList = { add() {}, remove() {} };
    constructor(tag, ownerDocument) { this.tag = tag; this.ownerDocument = ownerDocument; }
    getAttribute() { return null; }
    setAttribute(name, value) { this[name] = value; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    addEventListener() {}
    get childElementCount() { return this.children.length; }
    set innerHTML(_) { throw new Error("Untrusted HTML must not be interpreted"); }
}
const all = (node) => [node, ...node.children.flatMap(all)];
async function renderState(state, pendingState) {
    const nodes = new Map();
    const document = {
        createElement: (tag) => new Element(tag, document),
        querySelector(selector) {
            if (!nodes.has(selector)) nodes.set(selector, new Element("div", document));
            return nodes.get(selector);
        },
    };
    document.documentElement = new Element("html", document);
    document.body = new Element("body", document);
    const requests = [];
    const context = createContext({
        document, window: { matchMedia: () => ({ matches: false, addEventListener() {} }) },
        MutationObserver: class { observe() {} },
        fetch: async (url) => {
            requests.push(url);
            return { ok: true, json: async () => url === "/api/state" ?
                (pendingState ? pendingState.promise : state) : { observation: detail(43) } };
        },
    });
    const html = renderPage({ token: "fictional", nonce: "test" });
    new Script(html.match(/<script nonce="test">([\s\S]*?)<\/script>/)[1]).runInContext(context);
    await new Promise((resolve) => setImmediate(resolve));
    return { root: nodes.get("#draft-inspection"), requests, html, context, nodes };
}

test("nonce-bound iframe restores retained draft inertly without Node imports, polling or native acquisition", async () => {
    const document = draft();
    document.eligibility.rulesReason = "</script><script>untrusted()</script>";
    const { root, requests, html } = await renderState({ observation: detail(), draftDocument: document, busy: false });
    const text = all(root).map((node) => node.textContent).join("\n");
    assert.match(text, /Independently captured observations/);
    assert.match(text, /not zero-write/);
    assert.match(text, /no offline guarantee/);
    assert.ok(text.includes(document.eligibility.rulesReason));
    assert.equal(all(root).some((node) => ["button", "input", "script", "a"].includes(node.tag)), false);
    assert.deepEqual(requests, ["/api/state"]);
    assert.doesNotMatch(html, /node:|eval\(|setInterval\(|unsafe-eval|unsafe-inline/);
    const failure = await renderState({ observation: detail(), draftError: "actual native refusal", busy: false });
    assert.match(all(failure.root).map((node) => node.textContent).join("\n"), /actual native refusal/);
});

test("late initial state cannot restore an old draft over a newer item selection", async () => {
    const pending = deferred();
    const { root, context, nodes } = await renderState(null, pending);
    await new Script("perform('load', 43)").runInContext(context);
    pending.resolve({ observation: detail(), draftDocument: draft(), busy: false });
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(root.childElementCount, 0);
    assert.match(all(nodes.get("#observation")).map((node) => node.textContent).join("\n"), /#43/);
    assert.doesNotMatch(all(nodes.get("#observation")).map((node) => node.textContent).join("\n"), /#42 /);
});

test("existing SDK declaration wires only explicit inspection with no caller-supplied key or automatic acquisition", async () => {
    const source = await readFile(new URL("../extension.mjs", import.meta.url), "utf8");
    const calls = [];
    let declarations;
    let serverOptions;
    class CanvasError extends Error {
        constructor(code, message) { super(message); this.code = code; }
    }
    const fakeServer = {
        url: "http://127.0.0.1:1/",
        inspectDraft: async () => { calls.push("inspect"); return draft(); },
        close: async () => { calls.push("close"); },
    };
    const context = createContext({
        CanvasError, realpath, stat, path, process, createCanvas: (value) => value,
        joinSession: async ({ canvases }) => { declarations = canvases; return { workspacePath: path.join(workspacePath, "session-state") }; },
        createCanvasServer: async (options) => { serverOptions = options; return fakeServer; },
        ExecutableError: class extends Error {},
        admitTwigExecutable: async ({ canvas }) => { calls.push(`admit:${canvas}`); return fakeExecutable(); },
        createNativeDraftReader: ({ executable }) => executable.path === FAKE_TWIG ? "draft-reader" : "unpinned",
        createNativeDetailReader: () => "detail-reader",
        createNativeEditingAdapter: () => "editing-adapter",
        createNativeBenchReader: () => "bench-reader",
        createNativeBenchDetailReader: () => "bench-detail-reader",
    });
    await new Script("(async () => {\n" + source.replace(/^import .*;\r?$/gm, "") + "\n})()").runInContext(context);
    assert.deepEqual(Array.from(declarations, (canvas) => canvas.id), ["twig-item-companion", "twig-workbench", "twig-proposal-review"]);
    const companion = declarations[0];
    const action = companion.actions.find((value) => value.name === "inspect_draft");
    assert.deepEqual(JSON.parse(JSON.stringify(action.inputSchema)), { type: "object", properties: {}, additionalProperties: false });
    assert.match(action.description, /May write local observation\/conflict metadata/);
    const canvasContext = { instanceId: "fictional", session: { workingDirectory: workspacePath } };
    await companion.open(canvasContext);
    assert.equal(serverOptions.workspacePath, await realpath(workspacePath));
    assert.equal(serverOptions.readNativeDraft, "draft-reader");
    assert.deepEqual(calls, ["admit:item"]);
    await action.handler({ ...canvasContext, input: {} });
    assert.deepEqual(calls, ["admit:item", "inspect"]);
    assert.equal(declarations[1].actions.some((value) => value.name === "inspect_draft"), false);
    await companion.onClose({ instanceId: "fictional" });
    await assert.rejects(action.handler({ instanceId: "fictional", input: {} }), (error) => {
        assert.equal(error.code, "canvas_not_open");
        assert.equal(error.message, "Open the Twig item companion before loading detail.");
        return true;
    });
});
