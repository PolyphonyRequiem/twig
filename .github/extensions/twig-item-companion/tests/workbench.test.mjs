import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createNativeBenchReader } from "../native-bench.mjs";
import { renderPage } from "../render.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/bench-v2.json", import.meta.url), "utf8"));
const workspacePath = fixture.browser.origin.worktreeRoot;
function reader(payload = fixture, calls = [], fail = () => null) {
    return createNativeBenchReader({
        realpath: async (path) => path,
        execFile(exe, args, options, callback) {
            calls.push({ exe, args, options });
            callback(fail(), JSON.stringify(payload), "");
        },
    });
}

class Element {
    children = [];
    textContent = "";
    value = "";
    hidden = false;
    disabled = false;
    dataset = {};
    attributes = {};
    listeners = {};
    classList = { add() {}, remove() {} };
    getAttribute() { return null; }
    setAttribute(name, value) { this.attributes[name] = value; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(event, callback) { this.listeners[event] = callback; }
    focus() { this.focused = true; }
    get childElementCount() { return this.children.length; }
}
function dom(html, url) {
    const nodes = new Map();
    const document = {
        documentElement: new Element(), body: new Element(),
        createElement: () => new Element(),
        querySelector(selector) {
            if (!nodes.has(selector)) nodes.set(selector, new Element());
            return nodes.get(selector);
        },
    };
    const pending = new Set();
    const context = createContext({
        document,
        window: { scrollY: 120, scrollTo() {}, matchMedia: () => ({ matches: false, addEventListener() {} }) },
        MutationObserver: class { observe() {} },
        fetch: (route, options) => {
            const request = fetch(new URL(route, url), options);
            pending.add(request);
            return request.finally(() => pending.delete(request));
        },
    });
    const source = html.match(/<script nonce="[^"]+">([\s\S]*?)<\/script>/)[1];
    new Script(source).runInContext(context);
    return {
        nodes,
        async settled() {
            for (let attempt = 0; attempt < 30; attempt++) {
                await Promise.all([...pending]);
                await new Promise((resolve) => setTimeout(resolve, 5));
                if (!pending.size && !nodes.get("#bench-load")?.disabled) return;
            }
            assert.fail("Client did not settle");
        },
    };
}
function flatten(node) { return [node.textContent, ...node.children.flatMap(flatten)].join("\n"); }
function descendants(node) { return [node, ...node.children.flatMap(descendants)]; }
async function open(payload = fixture, fail) {
    const calls = [];
    const server = await createCanvasServer({
        workspacePath, workbench: true, readNativeBench: reader(payload, calls, fail),
    });
    const html = await (await fetch(server.url)).text();
    const ui = dom(html, server.url);
    await ui.settled();
    return { ...ui, server, calls, html };
}
async function load(ui, bench = fixture.browser.requestedBench) {
    ui.nodes.get("#bench-name").value = bench;
    ui.nodes.get("#bench-form").listeners.submit({ preventDefault() {} });
    await ui.settled();
}

test("HTTP named-Bench route returns native v2 evidence without changing context", async () => {
    const calls = [];
    const server = await createCanvasServer({
        workspacePath, workbench: true, readNativeBench: reader(fixture, calls),
        readNativeDetail: async () => { throw new Error("Item detail is not a named-Bench read"); },
    });
    try {
        const html = await (await fetch(server.url)).text();
        const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
        const response = await fetch(new URL("/api/bench", server.url), {
            method: "POST",
            headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
            body: JSON.stringify({ action: "load", bench: "Release focus" }),
        });
        assert.equal(response.status, 200, "A named-Bench read must return its native observation, not reject or redirect the route");
        assert.deepEqual((await response.json()).benchObservation, fixture.browser);
        assert.equal(calls.length, 1);
    } finally { await server.close(); }
});

test("workbench real client -> loopback -> native reader loads exact named Bench, raw counts and qualified selection", async () => {
    const ui = await open();
    try {
        await load(ui);
        assert.deepEqual(ui.calls[0].args, [
            "workspace", "--bench", "Release focus", "--include-browser", "--browser-version", "2", "--view", "tree", "-o", "json",
        ]);
        assert.equal(ui.calls[0].options.shell, false);
        assert.equal(ui.calls[0].options.cwd, workspacePath);
        assert.equal(ui.calls[0].options.timeout, 15000);
        assert.ok(ui.calls[0].options.signal instanceof AbortSignal);
        assert.match(flatten(ui.nodes.get("#bench-facts")), /raw-mirror/);
        assert.match(flatten(ui.nodes.get("#bench-facts")), /fixture-connection/);
        assert.match(ui.nodes.get("#bench-counts").textContent, /2 loaded \/ 3 known, 1 known not cached/);
        assert.match(ui.nodes.get("#bench-counts").textContent, /unknown/);
        const row = descendants(ui.nodes.get("#bench-rows")).find((node) => node.textContent.startsWith("#42"));
        row.listeners.click();
        assert.match(flatten(ui.nodes.get("#bench-selection")), /No revision or editing authority/);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /<script>not executable<\/script>/);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /not effective local values/);
        assert.equal(ui.nodes.get("#bench-browse").hidden, true);
        ui.nodes.get("#bench-selection").listeners.keydown({ key: "Escape", preventDefault() {} });
        assert.equal(ui.nodes.get("#bench-browse").hidden, false);
        assert.equal(row.focused, true);
        assert.ok(ui.calls.every(({ args }) => !args.includes("switch") && !args.includes("detail")));
    } finally { await ui.server.close(); }
});

test("local loaded filtering preserves hierarchy ancestors, member order, omitted members and back focus", async () => {
    const ui = await open();
    try {
        await load(ui);
        assert.ok(flatten(ui.nodes.get("#bench-rows")).indexOf("#42") < flatten(ui.nodes.get("#bench-rows")).indexOf("#43"));
        ui.nodes.get("#bench-filter").value = "script";
        ui.nodes.get("#bench-filter").listeners.input();
        assert.doesNotMatch(flatten(ui.nodes.get("#bench-rows")), /#43|#44/);
        ui.nodes.get("#bench-view").value = "hierarchy";
        ui.nodes.get("#bench-view").listeners.change();
        assert.match(flatten(ui.nodes.get("#bench-rows")), /#1 Container - Context only/);
        assert.doesNotMatch(flatten(ui.nodes.get("#bench-rows")), /Never parse ANSI label/);
        assert.match(ui.nodes.get("#bench-counts").textContent, /partial/);
        const row = descendants(ui.nodes.get("#bench-rows")).find((node) => node.textContent.startsWith("#42"));
        row.listeners.click();
        ui.nodes.get("#bench-selection").children[0].listeners.click();
        assert.equal(ui.nodes.get("#bench-filter").value, "script");
        assert.equal(ui.nodes.get("#bench-view").value, "hierarchy");
        assert.equal(row.focused, true);
        assert.equal(ui.calls.length, 1);
    } finally { await ui.server.close(); }
});

test("empty mirror never claims empty source; failed refresh retains previous qualified result", async () => {
    const payload = structuredClone(fixture);
    payload.browser.members = [];
    payload.browser.roots = [];
    Object.assign(payload.browser.completeness, {
        knownMemberCount: 0, loadedMemberCount: 0, unloadedKnownMemberCount: 0,
        renderedNodeCount: 0, omittedLoadedMemberIds: [], depthOmittedIds: [], projectionComplete: true,
    });
    let failure = null;
    const ui = await open(payload, () => failure);
    try {
        await load(ui);
        assert.match(flatten(ui.nodes.get("#bench-rows")), /not proof of an empty source/);
        failure = new Error("Native refused");
        ui.nodes.get("#bench-refresh").listeners.click();
        await ui.settled();
        assert.match(ui.nodes.get("#bench-status").textContent, /previous qualified observation retained.*Native refused/);
        assert.match(flatten(ui.nodes.get("#bench-facts")), /Release focus/);
        assert.deepEqual(ui.calls[1].args.slice(-6), [
            "--expect-bench", "7", "--expect-binding", "fixture-binding", "--expect-identity", "fixture-identity",
        ]);
        assert.equal(ui.calls[1].args[2], "7");
    } finally { await ui.server.close(); }
});

test("reader rejects unsupported schemas, contradictory completeness/rows, and wrong checkout without fallback", async () => {
    const cases = [
        (b) => { b.version = 1; }, (b) => { b.observation = "effective"; },
        (b) => { delete b.origin.connectionRef; }, (b) => { b.origin.worktreeRoot += "\\other"; },
        (b) => { b.requestedBench = "other"; }, (b) => { b.members.push(b.members[0]); },
        (b) => { b.completeness.knownMemberCount = 99; }, (b) => { b.completeness.sourceComplete = true; },
        (b) => { delete b.completeness.sourceMemberCount; }, (b) => { b.completeness.projectionComplete = true; },
        (b) => { b.completeness.renderedNodeCount = 3; }, (b) => { b.completeness.omittedLoadedMemberIds = []; },
        (b) => { b.roots[0].children[0].title = "effective overlay"; },
        (b) => { b.members[2].title = "invented uncached value"; },
    ];
    for (const mutate of cases) {
        const payload = structuredClone(fixture);
        mutate(payload.browser);
        await assert.rejects(reader(payload)({ bench: "Release focus", workspacePath }), /Bench|bench/);
    }
});

test("refresh continuity rejects a different connection or Bench even with same binding/identity", async () => {
    for (const mutate of [
        (b) => { b.origin.connectionRef = "other"; },
        (b) => { b.benchId = "9"; },
        (b) => { b.origin.attachmentRevision++; },
    ]) {
        const payload = structuredClone(fixture);
        mutate(payload.browser);
        await assert.rejects(reader(payload)({
            bench: "Release focus", workspacePath,
            expected: { benchId: fixture.browser.benchId, origin: fixture.browser.origin },
        }), /changed/);
    }
});

test("mid-session origin drift refuses refresh and retains the original qualified observation", async () => {
    const payload = structuredClone(fixture);
    const calls = [];
    const server = await createCanvasServer({
        workspacePath, workbench: true, readNativeBench: reader(payload, calls),
    });
    try {
        const initial = await server.loadBench("Release focus");
        payload.browser.requestedBench = "7";
        assert.deepEqual((await server.refreshBench()).origin, initial.origin);
        payload.browser.origin.connectionRef = "different-connection";
        await assert.rejects(server.refreshBench(), /origin or captured target changed/);
        const html = await (await fetch(server.url)).text();
        const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
        const state = await (await fetch(new URL("/api/state", server.url), {
            headers: { Authorization: `Bearer ${token}` },
        })).json();
        assert.deepEqual(state.benchObservation.origin, initial.origin);
        assert.match(state.error, /origin or captured target changed/);
        assert.deepEqual(calls[2].args.slice(-6), [
            "--expect-bench", "7", "--expect-binding", "fixture-binding", "--expect-identity", "fixture-identity",
        ]);
    } finally { await server.close(); }
});

test("server serializes reads, aborts owned read on close and isolates detail/workbench routes", async () => {
    let release;
    let signal;
    const server = await createCanvasServer({
        workspacePath, workbench: true,
        readNativeBench: async (request) => {
            signal = request.signal;
            await new Promise((resolve) => { release = resolve; });
            return fixture.browser;
        },
    });
    try {
        const html = await (await fetch(server.url)).text();
        const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
        const options = (body) => ({
            method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        assert.equal((await fetch(new URL("/api/bench", server.url), options({ action: "refresh" }))).status, 500);
        assert.equal((await fetch(new URL("/api/detail", server.url), options({ action: "load", workItemId: 42 }))).status, 404);
        const loading = server.loadBench("Release focus");
        await assert.rejects(server.loadBench("other"), /already in progress/);
        await server.close();
        assert.equal(signal.aborted, true);
        release();
        await assert.rejects(loading, /closed/);
    } finally { release?.(); await server.close(); }
});

test("workbench page has named target, shared theme and keyboard controls without editing affordances", () => {
    const page = renderPage({ token: "fixture", nonce: "nonce", workbench: true });
    assert.match(page, /Twig workbench/);
    assert.match(page, /for="bench-name"/);
    assert.match(page, /for="theme-mode"/);
    assert.match(page, /for="bench-filter"/);
    assert.match(page, /for="bench-view"/);
    assert.doesNotMatch(page, /id="load-form"|>Save<|>Apply<|>Start</);
});

test("actual extension wiring registers both canvases and routes workbench actions to its owned server", async () => {
    const calls = [];
    let declarations;
    const source = await readFile(new URL("../extension.mjs", import.meta.url), "utf8");
    const context = createContext({
        CanvasError: class extends Error { constructor(code, message) { super(message); this.code = code; } },
        createCanvas: (declaration) => declaration,
        joinSession: async (config) => { declarations = config.canvases; return { workspacePath }; },
        createCanvasServer,
        createNativeDetailReader: () => { throw new Error("Detail should not be read by a workbench action"); },
        createNativeBenchReader: () => reader(fixture, calls),
    });
    // Only SDK/import boundaries are replaced; the production declaration and callbacks run unchanged.
    const script = source.replace(/^import .*;$/gm, "")
        .replace("const readNativeDetail = createNativeDetailReader();", "const readNativeDetail = async () => { throw new Error('Detail not admitted here'); };");
    await new Script(`(async () => { ${script}\n })()`).runInContext(context);
    assert.deepEqual(Array.from(declarations, (canvas) => canvas.id), ["twig-item-companion", "twig-workbench"]);
    const workbench = declarations.find((canvas) => canvas.id === "twig-workbench");
    const ctx = { instanceId: "fixture-workbench", input: { bench: "Release focus" } };
    const opened = await workbench.open(ctx);
    try {
        assert.equal((await workbench.open(ctx)).url, opened.url);
        const loaded = await workbench.actions.find((action) => action.name === "load_bench").handler(ctx);
        assert.deepEqual(loaded, fixture.browser);
        const html = await (await fetch(opened.url)).text();
        assert.match(html, /Twig workbench/);
        assert.equal(calls.length, 1);
    } finally { await workbench.onClose(ctx); }
    await assert.rejects(workbench.actions[0].handler(ctx), /Open the Twig workbench/);
});

test("invalid explicit targets never launch Twig, and a valid read keeps the cancellation/output bounds", async () => {
    const calls = [];
    const read = reader(fixture, calls);
    for (const bench of [null, "", "  ", "x".repeat(513), 7]) {
        await assert.rejects(read({ bench, workspacePath }), /saved Bench/);
    }
    assert.equal(calls.length, 0);
    await read({ bench: "Release focus", workspacePath });
    assert.equal(calls[0].exe, "twig");
    assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
});

function deferredDetail(calls) {
    const gates = [];
    const read = (request) => new Promise((resolve) => {
        calls.push(request);
        gates.push((label) => resolve({
            benchName: fixture.browser.benchName, benchId: fixture.browser.benchId,
            membership: { selected: true, seed: false, pending: false },
            completeness: { sourceComplete: false, sourceChildCount: null, cachedChildCount: 0, parentCached: null, linksVerified: false, reasons: ["source-unknown"] },
            document: { systemControls: [{ id: "x", label, value: { state: 0, full: label } }], pages: [] },
        }));
    });
    return { read, release: (index, label) => gates[index](label) };
}
async function until(check) {
    for (let attempt = 0; attempt < 200; attempt++) {
        if (check()) return;
        await new Promise((resolve) => setTimeout(resolve, 5));
    }
    assert.fail("Condition not reached");
}
async function openDeferred(calls) {
    const detail = deferredDetail(calls);
    const server = await createCanvasServer({
        workspacePath, workbench: true, readNativeBench: reader(), readNativeBenchDetail: detail.read,
    });
    const ui = dom(await (await fetch(server.url)).text(), server.url);
    await ui.settled();
    await load(ui);
    return { ...ui, server, detail };
}
const row42 = (ui) => descendants(ui.nodes.get("#bench-rows")).find((node) => node.textContent.startsWith("#42"));
const back = (ui) => descendants(ui.nodes.get("#bench-selection")).find((node) => node.textContent === "Back to Bench");
const occurrences = (ui, label) => flatten(ui.nodes.get("#bench-selection")).split(label).length - 1;

test("detail: back then reselect the SAME item while a read is pending never accepts the old response and issues no duplicate read", async () => {
    const calls = [];
    const ui = await openDeferred(calls);
    try {
        row42(ui).listeners.click();
        await until(() => calls.length === 1);
        assert.equal(ui.nodes.get("#bench-load").disabled, true, "pending detail must be reflected as busy");
        back(ui).listeners.click();
        row42(ui).listeners.click();
        await new Promise((resolve) => setTimeout(resolve, 30));
        assert.equal(calls.length, 1, "a second detail read must not start while one is in flight");
        assert.match(flatten(ui.nodes.get("#bench-selection")), /Waiting for the previous detail read/);
        ui.detail.release(0, "OLD-RESPONSE");
        await until(() => calls.length === 2);
        assert.equal(occurrences(ui, "OLD-RESPONSE"), 0, "the stale response must not publish into the new selection");
        ui.detail.release(1, "NEW-RESPONSE");
        await ui.settled();
        await until(() => occurrences(ui, "NEW-RESPONSE") > 0);
        assert.equal(occurrences(ui, "OLD-RESPONSE"), 0);
        assert.equal(occurrences(ui, "Native detail (read-only)"), 1, "exactly one detail box");
    } finally { await ui.server.close(); }
});

test("detail: a stale response after back and a reloaded Bench with the same item id is not published", async () => {
    const calls = [];
    const ui = await openDeferred(calls);
    try {
        row42(ui).listeners.click();
        await until(() => calls.length === 1);
        back(ui).listeners.click();
        ui.detail.release(0, "OLD-RESPONSE");
        await ui.settled();
        assert.equal(occurrences(ui, "OLD-RESPONSE"), 0, "stale response must not touch the abandoned selection");
        await load(ui);
        row42(ui).listeners.click();
        await until(() => calls.length === 2);
        ui.detail.release(1, "NEW-RESPONSE");
        await ui.settled();
        await until(() => occurrences(ui, "NEW-RESPONSE") > 0);
        assert.equal(occurrences(ui, "OLD-RESPONSE"), 0);
        assert.equal(occurrences(ui, "Native detail (read-only)"), 1);
    } finally { await ui.server.close(); }
});
