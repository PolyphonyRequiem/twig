import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createNativeDetailReader } from "../native-detail.mjs";
import { createCanvasServer } from "../canvas-server.mjs";
import { renderPage } from "../render.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
const workspacePath = fixture.itemKey.checkoutRoot;

function reader(payload, calls = []) {
    return createNativeDetailReader({ executable: fakeExecutable(),
        realpath: async (value) => value,
        execFile: (executable, args, options, callback) => {
            calls.push({ executable, args, options });
            callback(null, JSON.stringify(payload), "");
        },
    });
}

test("refuses contradictory or incomplete native v2 observations instead of rendering guesses", async () => {
    const cases = [
        (v) => { v.item.id = 99; },
        (v) => { v.document.workItemId = 99; },
        (v) => { v.document.revision = 18; },
        (v) => { delete v.itemKey.connectionId; },
        (v) => { delete v.identityId; },
        (v) => { v.layoutStatus = "unavailable"; },
        (v) => { v.document.pages = {}; },
        (v) => { delete v.document.systemControls[0].value; },
        (v) => { v.document.systemControls[0].value.state = 99; },
        (v) => { v.hasLocalChanges = "false"; },
        (v) => { v.editing = "available"; },
    ];
    for (const mutate of cases) {
        const payload = structuredClone(fixture);
        mutate(payload);
        await assert.rejects(reader(payload)({ workItemId: 42, workspacePath }), /semantic detail|capability contract/);
    }
});

test("fixture JSON round-trips through native reader and loopback API; revision is not item identity", async () => {
    const calls = [];
    const payload = structuredClone(fixture);
    const server = await createCanvasServer({ workspacePath, readNativeDetail: reader(payload, calls) });
    try {
        const page = await fetch(server.url);
        assert.equal(page.status, 200);
        const html = await page.text();
        const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
        const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
        const response = await fetch(new URL("/api/detail", server.url), {
            method: "POST", headers, body: JSON.stringify({ action: "load", workItemId: 42 }),
        });
        assert.equal(response.status, 200);
        const initial = (await response.json()).observation;
        assert.deepEqual(initial, fixture);
        payload.item.revision = payload.document.revision = 18;
        const refreshed = await server.refresh();
        assert.deepEqual(refreshed.itemKey, initial.itemKey);
        assert.equal(refreshed.document.revision, 18);
        assert.deepEqual(calls[1].args, [
            "bench", "detail", "42", "--document", "-o", "json",
            "--expect-bench", "7", "--expect-binding", "fixture-binding",
            "--expect-identity", "fixture-identity",
        ]);
        assert.ok(calls.every((call) => call.options.shell === false && call.options.cwd === workspacePath));
    } finally {
        await server.close();
    }
});

test("empty served layout, locked fallback and unavailable fallback remain distinct", async () => {
    for (const status of ["served", "locked", "unavailable"]) {
        const payload = structuredClone(fixture);
        payload.layoutStatus = status;
        payload.layoutSource = status === "served" ? "server" : "twig-fallback";
        payload.document.processId = status === "served" ? "fixture-process" : "twig.fallback";
        payload.document.pages = [];
        payload.lockedTypeReferenceName = status === "locked" ? "Custom.DeliverySlice" : undefined;
        const result = await reader(payload)({ workItemId: 42, workspacePath });
        const text = rendered(result);
        assert.ok(text.includes(status));
        assert.ok(text.includes(payload.layoutSource));
        assert.equal(result.document.pages.length, 0);
        assert.equal(result.editing, "unavailable");
    }
});

test("refresh refuses a changed connection even if Bench, binding and identity strings match", async () => {
    const payload = structuredClone(fixture);
    payload.itemKey.connectionId = "other-connection";
    await assert.rejects(reader(payload)({
        workItemId: 42, workspacePath,
        expected: {
            benchId: fixture.benchId, bindingId: fixture.itemKey.bindingId,
            identityId: fixture.identityId, connectionId: fixture.itemKey.connectionId,
        },
    }), /no longer matches/);
});

class Element {
    children = [];
    textContent = "";
    disabled = false;
    classList = { add() {}, remove() {} };
    dataset = {};
    getAttribute() { return null; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener() {}
    get childElementCount() { return this.children.length; }
}

function rendered(payload) {
    const nodes = new Map();
    const document = {
        documentElement: new Element(),
        body: new Element(),
        createElement: () => new Element(),
        querySelector: (selector) => {
            if (!nodes.has(selector)) nodes.set(selector, new Element());
            return nodes.get(selector);
        },
    };
    const context = createContext({
        document,
        window: { matchMedia: () => ({ matches: false, addEventListener() {} }) },
        MutationObserver: class { observe() {} },
        fetch: async () => ({ ok: true, json: async () => ({ busy: false }) }),
        payload,
    });
    const html = renderPage({ token: "fixture-token", nonce: "fixture-nonce" });
    const source = html.match(/<script nonce="fixture-nonce">([\s\S]*?)<\/script>/)[1];
    new Script(source + "\nrenderObservation(payload);").runInContext(context);
    const flatten = (node) => [node.textContent, ...node.children.flatMap(flatten)];
    return flatten(nodes.get("#observation")).join("\n");
}

test("real client renderer shows stable qualification, provenance and inert values without edit authority", () => {
    const text = rendered(fixture);
    for (const expected of [
        workspacePath, "fixture-connection", "fixture-binding", "Revision", "17",
        "server", "served", "unavailable", "not-exported", "Empty on server.",
        "Not carried by Twig.", "Not visible in native layout.", "contribution control",
        fixture.item.fields["Custom.Body"],
    ]) assert.ok(text.includes(expected), `Missing rendered fact: ${expected}`);
    assert.ok(text.includes("Layout flags are not editing permission"));
    assert.ok(text.includes("History content was not exported"));
});
