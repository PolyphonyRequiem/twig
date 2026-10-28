import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { EventEmitter } from "node:events";
import { readFile } from "node:fs/promises";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createNativeDetailReader } from "../native-detail.mjs";
import { createNativeEditingAdapter } from "../native-editing.mjs";
import { fakeExecutable } from "./executable-fixture.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
const workItem = () => {
    const result = structuredClone(fixture);
    result.itemKey.checkoutRoot = process.cwd();
    return result;
};
async function setup({ conflicts = false, saveStatus = "saved", unavailable = false, refreshFailure = false } = {}) {
    const detail = workItem();
    const calls = [];
    let reads = 0;
    let draft = {
        nativeContext: {
            itemKey: detail.itemKey, externalIdentity: { providerKind: "ado", resourceScope: "fixture-resource", nativeId: "42" },
            benchId: detail.benchId, benchName: detail.benchName, benchContentsDigest: "digest", identityId: detail.identityId,
            bindingRevision: "1", selectionSource: "checkout", selectionRevision: "1", attachmentRevision: "1",
            storageGeneration: null, originDigest: "origin", evidenceDigest: "evidence",
        },
        generation: "1", baselineRevision: "17", observedRevision: "17", currentRevision: "17",
        hasConflicts: conflicts, changes: [], conflicts: conflicts ? [{ fieldRef: "System.Title",
            baselineValue: "old", proposedValue: "local", observedValue: "remote" }] : [],
        systemControls: detail.document.systemControls, pages: detail.document.pages,
        fields: [{ fieldRef: "System.Title", editable: true }], authorized: false, serverFinalAuthority: true,
    };
    const canonicalJson = '{"native":"capture"}';
    const digest = createHash("sha256").update(canonicalJson).digest("hex");
    const operationId = "draft-" + "b".repeat(64);
    const adapter = createNativeEditingAdapter({
        executable: fakeExecutable(), realpath: async value => value,
        launch(exe, args, options, callback) {
            const child = new EventEmitter();
            child.stdin = new EventEmitter();
            child.stdin.end = source => {
                const request = source ? JSON.parse(source) : null;
                calls.push({ action: args[1], request, args, options });
                let result;
                let code = 0;
                if (args[1] === "open") result = { version: 1, status: "opened", committed: true, document: draft };
                if (args[1] === "stage") {
                    draft = { ...draft, generation: (BigInt(draft.generation) + 1n).toString(),
                        changes: request.changes.map(change => ({ ...change, baselineValue: "Read-only fixture" })) };
                    result = { version: 1, status: "staged", committed: true, document: draft };
                }
                if (args[1] === "retain") {
                    const { version, ...capture } = request;
                    result = { version: 1, status: "retained", committed: true, capture: {
                        ...capture, currentRevision: draft.currentRevision, digest, canonicalJson, operationId,
                        effects: draft.changes, originCoverage: "editing-context-and-native-origin-digest",
                        authorized: false, canApply: false,
                    } };
                }
                if (args[1] === "save") {
                    const verified = saveStatus !== "not-verified";
                    const settled = saveStatus === "saved";
                    result = { version: 1, digest, callerIdentity: draft.nativeContext.identityId,
                        surface: "twig-workbench-explicit-save", hostAttested: false, verified, settled, status: saveStatus,
                        operations: [{ opId: operationId, ordinal: 0, state: verified ? "Verified" : "Failed", error: null }],
                        unsettledAcknowledgments: {}, error: null,
                        draftSettlement: settled ? "settled" : null, draftSettlementError: settled ? null : "Not settled",
                    };
                    code = settled ? 0 : 1;
                }
                queueMicrotask(() => { callback(code ? { code } : null, JSON.stringify(result)); child.emit("close", code); });
            };
            if (args[1] === "open") queueMicrotask(() => child.stdin.end());
            return child;
        },
    });
    const readNativeDetail = createNativeDetailReader({
        executable: fakeExecutable(), realpath: async value => value,
        execFile(exe, args, options, callback) {
            reads++;
            if (refreshFailure && reads > 1) return callback(new Error("failed"), "", "Native refresh refused.");
            const observation = structuredClone(detail);
            if (reads > 1) observation.item.revision = observation.document.revision = 18;
            callback(null, JSON.stringify(observation), "");
        },
    });
    const server = await createCanvasServer({
        workspacePath: process.cwd(), readNativeDetail, nativeEditing: unavailable ? undefined : adapter,
    });
    const html = await (await fetch(server.url)).text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
    const post = (body, extra = {}) => fetch(new URL("/api/editing", server.url), {
        method: "POST", headers: { ...headers, ...extra }, body: JSON.stringify(body),
    });
    const edit = async body => {
        const response = await post(body);
        assert.equal(response.status, 200, await response.clone().text());
        return (await response.json()).editing;
    };
    const state = async () => (await fetch(new URL("/api/state", server.url), { headers })).json();
    return { server, html, headers, calls, post, edit, state, detail, get reads() { return reads; } };
}
async function retain(h) {
    const opened = await h.edit({ action: "open" });
    const staged = await h.edit({ action: "stage", captureHandle: opened.captureHandle,
        changes: [{ fieldRef: "System.Title", proposedValue: "<b>explicit</b>" }] });
    return h.edit({ action: "retain", captureHandle: staged.captureHandle });
}

test("companion production composition reuses qualified native OPEN/STAGE/RETAIN/Save; only verified Save refreshes", async () => {
    const h = await setup({});
    try {
        await h.server.load(42);
        assert.deepEqual(h.calls, [], "detail display never edits or saves");
        const held = await retain(h);
        assert.deepEqual(held.receipt.effects, [{ fieldRef: "System.Title", baselineValue: "Read-only fixture",
            proposedValue: "<b>explicit</b>" }]);
        assert.equal(h.reads, 1);
        const response = await h.post({ action: "save", saveHandle: held.saveHandle });
        assert.equal(response.status, 200);
        const saved = await response.json();
        assert.equal(saved.editing.status, "saved");
        assert.equal(saved.detailRefresh.status, "current");
        assert.deepEqual(saved.observation.itemKey, h.detail.itemKey);
        assert.equal(saved.observation.item.revision, 18);
        assert.deepEqual(h.calls.map(call => call.action), ["open", "stage", "retain", "save"]);
        assert.equal(h.calls[0].args.includes(h.detail.benchId), true);
        assert.deepEqual(h.calls[1].request.nativeContext.itemKey, h.detail.itemKey);
        assert.equal(h.calls[3].request.digest, held.receipt.digest);
        assert.equal(h.reads, 2);
        assert.notEqual((await h.post({ action: "save", saveHandle: held.saveHandle })).status, 200);
        assert.equal(h.calls.length, 4);
        assert.equal(h.server.save, undefined, "Save is never an SDK/server action");
    } finally { await h.server.close(); }
});

test("companion native conflicts, unavailable authority and invalid origin fail without staging", async () => {
    const h = await setup({ conflicts: true });
    try {
        await h.server.load(42);
        const opened = await h.edit({ action: "open" });
        assert.equal(opened.canStage, false);
        assert.notEqual((await h.post({ action: "stage", captureHandle: opened.captureHandle,
            changes: [{ fieldRef: "System.Title", proposedValue: "wrong" }] })).status, 200);
        assert.equal(h.calls.length, 1);
        assert.equal((await h.post({ action: "open" }, { Origin: "https://invalid.example" })).status, 403);
    } finally { await h.server.close(); }
    const absent = await setup({ unavailable: true });
    try {
        await absent.server.load(42);
        const response = await absent.post({ action: "open" });
        assert.equal(response.status, 500);
        assert.match((await response.json()).error, /unavailable/);
        assert.equal(absent.calls.length, 0);
    } finally { await absent.server.close(); }
});

test("unverified and unsettled native Save never trigger a detail refresh or replay", async () => {
    for (const saveStatus of ["not-verified", "verified-draft-retained"]) {
        const h = await setup({ saveStatus });
        try {
            await h.server.load(42);
            const held = await retain(h);
            const response = await h.post({ action: "save", saveHandle: held.saveHandle });
            const result = await response.json();
            assert.equal(result.editing.status, saveStatus);
            assert.equal(result.detailRefresh, undefined);
            assert.equal(h.reads, 1);
            const reopened = await h.edit({ action: "open" });
            assert.equal(reopened.inspectFirst, true);
            assert.equal(reopened.canSave, false);
            assert.equal(h.calls.length, 4);
        } finally { await h.server.close(); }
    }
});

test("verified Save remains verified when the following cache refresh fails", async () => {
    const h = await setup({ refreshFailure: true });
    try {
        await h.server.load(42);
        const held = await retain(h);
        const response = await h.post({ action: "save", saveHandle: held.saveHandle });
        const result = await response.json();
        assert.equal(result.editing.status, "saved");
        assert.equal(result.detailRefresh.status, "failed");
        assert.match(result.detailRefresh.error, /Native refresh refused/);
        assert.equal(result.observation, undefined);
        assert.deepEqual((await h.state()).observation.itemKey, h.detail.itemKey);
        assert.equal(h.calls.length, 4, "failed cache read cannot replay verified effects");
    } finally { await h.server.close(); }
});

class Element {
    children = []; listeners = {}; dataset = {}; value = ""; textContent = ""; disabled = false;
    classList = { add() {}, remove() {} };
    constructor(tag) { this.tag = tag; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    getAttribute() { return null; }
    setAttribute(name, value) { this[name] = value; }
    get childElementCount() { return this.children.length; }
}
const descendants = node => node ? [node, ...node.children.flatMap(descendants)] : [];
test("real companion iframe mounts shared editing only after detail and rejects untrusted Save gestures", async () => {
    const h = await setup({});
    const nodes = new Map();
    const document = { documentElement: new Element("html"), body: new Element("body"),
        createElement: tag => new Element(tag),
        querySelector(selector) {
            if (!nodes.has(selector)) nodes.set(selector, new Element("div"));
            return nodes.get(selector);
        },
    };
    const pending = new Set();
    const context = createContext({
        document, window: { matchMedia: () => ({ matches: false, addEventListener() {} }) },
        MutationObserver: class { observe() {} },
        fetch: (route, options) => {
            const promise = fetch(new URL(route, h.server.url), options);
            pending.add(promise);
            promise.finally(() => pending.delete(promise));
            return promise;
        },
    });
    const settle = async () => {
        for (let n = 0; n < 30; n++) {
            await Promise.all([...pending]);
            await new Promise(resolve => setImmediate(resolve));
            if (!pending.size) { await new Promise(resolve => setTimeout(resolve, 5)); if (!pending.size) return; }
        }
        assert.fail("Companion browser did not settle");
    };
    const find = label => descendants(nodes.get("#companion-editing")).find(node => node.textContent === label);
    try {
        new Script(h.html.match(/<script nonce="[^"]+">([\s\S]*?)<\/script>/)[1]).runInContext(context);
        await settle();
        await new Script("perform('load', 42)").runInContext(context);
        assert.equal(h.calls.length, 0);
        assert.ok(find("Open local draft (not published)"), "real companion must mount shared editing UI");
        find("Open local draft (not published)").listeners.click();
        await settle();
        const input = descendants(nodes.get("#companion-editing")).find(node => node.tag === "textarea");
        input.value = "<script>inert</script>";
        input.listeners.input();
        find("Stage local draft only").listeners.click();
        await settle();
        find("Retain staged draft").listeners.click();
        await settle();
        const save = find("Save exact retained capture (publish)");
        save.listeners.click({ isTrusted: false });
        await settle();
        assert.equal(h.calls.length, 3);
        save.listeners.click({ isTrusted: true });
        await settle();
        assert.equal(h.calls.at(-1).action, "save");
        assert.equal(h.reads, 2);
        assert.match(nodes.get("#status").textContent, /verified.*refreshed/i);
        assert.match(descendants(nodes.get("#observation")).map(node => node.textContent).join("\n"), /\b18\b/);
    } finally { await h.server.close(); }
});
