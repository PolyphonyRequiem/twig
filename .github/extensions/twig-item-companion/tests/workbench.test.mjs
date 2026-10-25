import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createNativeBenchReader } from "../native-bench.mjs";
import { createNativeBenchDetailReader } from "../native-bench-detail.mjs";
import { renderPage } from "../render.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/bench-v2.json", import.meta.url), "utf8"));
const detailFixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
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
    scrollTop = 0;
    style = { values: {}, setProperty(name, value) { this.values[name] = value; } };
    classList = { values: new Set(), add(value) { this.values.add(value); }, remove(value) { this.values.delete(value); } };
    getAttribute(name) { return this.attributes[name] ?? null; }
    setAttribute(name, value) { this.attributes[name] = value; }
    append(...nodes) {
        for (const node of nodes) {
            if (node.parentElement) node.parentElement.children = node.parentElement.children.filter((child) => child !== node);
            node.parentElement = this;
            this.children.push(node);
        }
    }
    replaceChildren(...nodes) {
        for (const child of this.children) child.parentElement = null;
        this.children = [];
        this.append(...nodes);
    }
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
    const browserRequests = [];
    const window = {
        scrollY: 120,
        scrollTo(x, y) { this.scrollY = y; },
        matchMedia: () => ({ matches: false, addEventListener() {} }),
    };
    const context = createContext({
        document,
        window,
        MutationObserver: class { observe() {} },
        fetch: (route, options) => {
            if (options?.body) browserRequests.push({ route, body: JSON.parse(options.body) });
            const request = fetch(new URL(route, url), options);
            pending.add(request);
            return request.finally(() => pending.delete(request));
        },
    });
    const source = html.match(/<script nonce="[^"]+">([\s\S]*?)<\/script>/)[1];
    new Script(source).runInContext(context);
    return {
        nodes,
        browserRequests,
        window,
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
async function open(payload = fixture, fail, detailPayload, nativeEditing) {
    const calls = [];
    const server = await createCanvasServer({
        workspacePath, workbench: true, readNativeBench: reader(payload, calls, fail),
        nativeEditing,
        readNativeBenchDetail: detailPayload && createNativeBenchDetailReader({
            realpath: async (path) => path,
            execFile(exe, args, options, callback) {
                calls.push({ exe, args, options });
                callback(null, JSON.stringify(detailPayload), "");
            },
        }),
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
        ui.nodes.get("#bench-view").value = "results";
        ui.nodes.get("#bench-view").listeners.change();
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
        ui.nodes.get("#bench-view").value = "results";
        ui.nodes.get("#bench-view").listeners.change();
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

test("tree is the initial view and preserves native hierarchy omissions without claiming source completeness", async () => {
    const ui = await open();
    try {
        await load(ui);
        assert.equal(ui.nodes.get("#bench-view").value, "hierarchy");
        assert.equal(ui.nodes.get("#bench-layout").value, "page");
        const rows = flatten(ui.nodes.get("#bench-rows"));
        assert.ok(rows.indexOf("#1 Container") < rows.indexOf("#42 Delivery slice"));
        assert.doesNotMatch(rows, /#43|#44|Never parse ANSI/);
        assert.match(ui.nodes.get("#bench-counts").textContent, /2 rendered nodes.*partial.*unknown/);
        assert.match(flatten(ui.nodes.get("#bench-facts")), /omittedLoadedMemberIds\n43/);
        assert.equal(ui.nodes.get("#bench-selection").hidden, true);
        assert.equal(ui.nodes.get("#bench-divider").hidden, true);
        assert.equal(ui.calls.length, 1);
    } finally { await ui.server.close(); }
});

test("tree expansion, filtering, page Back, selection and scroll remain local to the same observation", async () => {
    const ui = await open();
    try {
        await load(ui);
        const toggle = () => descendants(ui.nodes.get("#bench-rows"))
            .find((node) => node.attributes["aria-expanded"] !== undefined);
        toggle().listeners.click();
        assert.equal(toggle().attributes["aria-expanded"], "false");
        ui.nodes.get("#bench-filter").value = "script";
        ui.nodes.get("#bench-filter").listeners.input();
        assert.equal(toggle().attributes["aria-expanded"], "true");
        ui.nodes.get("#bench-filter").value = "";
        ui.nodes.get("#bench-filter").listeners.input();
        assert.equal(toggle().attributes["aria-expanded"], "false");
        toggle().listeners.click();
        ui.nodes.get("#bench-browse").scrollTop = 73;
        const row = row42(ui);
        row.listeners.click();
        assert.equal(row.attributes["aria-pressed"], "true");
        ui.window.scrollY = 450;
        back(ui).listeners.click();
        assert.equal(ui.nodes.get("#bench-browse").hidden, false);
        assert.equal(ui.nodes.get("#bench-browse").scrollTop, 73);
        assert.equal(ui.window.scrollY, 120);
        assert.equal(row.focused, true);
        assert.equal(row.attributes["aria-pressed"], "true");
        assert.equal(toggle().attributes["aria-expanded"], "true");
        ui.nodes.get("#bench-filter").value = "script";
        ui.nodes.get("#bench-filter").listeners.input();
        assert.equal(row42(ui).attributes["aria-pressed"], "true", "Back retains identity across tree rerenders");
        assert.equal(ui.nodes.get("#bench-selection").hidden, true, "retained selection does not reopen detail");
        ui.nodes.get("#bench-layout").value = "split";
        ui.nodes.get("#bench-layout").listeners.change();
        assert.equal(ui.nodes.get("#bench-selection").hidden, true, "changing mode does not reopen retained selection");
        assert.equal(ui.calls.length, 1, "navigation never re-reads or changes the native Bench");
    } finally { await ui.server.close(); }
});

test("split/page switching and bounded keyboard range resize retain the selected detail without native reads", async () => {
    const ui = await open();
    try {
        await load(ui);
        ui.nodes.get("#bench-layout").value = "split";
        ui.nodes.get("#bench-layout").listeners.change();
        assert.equal(ui.nodes.get("#bench-divider").hidden, true, "no empty item pane before selection");
        row42(ui).listeners.click();
        assert.equal(ui.nodes.get("#bench-panes").attributes["data-layout"], "split");
        assert.equal(ui.nodes.get("#bench-browse").hidden, false);
        assert.equal(ui.nodes.get("#bench-selection").hidden, false);
        assert.equal(ui.nodes.get("#bench-divider").hidden, false);
        const height = ui.nodes.get("#bench-height");
        for (const value of ["120", "600"]) {
            height.value = value;
            height.listeners.input();
            assert.equal(ui.nodes.get("#bench-panes").style.values["--bench-tree-height"], value + "px");
        }
        for (const value of ["119", "601", "NaN", "200.5"]) {
            height.value = value;
            height.listeners.input();
            assert.equal(ui.nodes.get("#bench-panes").style.values["--bench-tree-height"], "600px");
        }
        ui.nodes.get("#bench-layout").value = "page";
        ui.nodes.get("#bench-layout").listeners.change();
        assert.equal(ui.nodes.get("#bench-browse").hidden, true);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /#42/);
        ui.nodes.get("#bench-layout").value = "split";
        ui.nodes.get("#bench-layout").listeners.change();
        assert.equal(ui.nodes.get("#bench-browse").hidden, false);
        assert.equal(ui.calls.length, 1);
    } finally { await ui.server.close(); }
});

test("Back and Escape focus the filter when a split-pane selection is filtered out", async () => {
    for (const exit of ["back", "escape"]) {
        const ui = await open();
        try {
            await load(ui);
            ui.nodes.get("#bench-layout").value = "split";
            ui.nodes.get("#bench-layout").listeners.change();
            const selected = row42(ui);
            selected.listeners.click();
            ui.nodes.get("#bench-filter").value = "nothing-matches";
            ui.nodes.get("#bench-filter").listeners.input();
            assert.equal(row42(ui), undefined, "selected row really is absent from current tree");
            if (exit === "back") back(ui).listeners.click();
            else ui.nodes.get("#bench-selection").listeners.keydown({ key: "Escape", preventDefault() {} });
            assert.equal(ui.nodes.get("#bench-filter").focused, true, "focus must not target a detached row");
            assert.equal(selected.focused, undefined);
            assert.equal(ui.nodes.get("#bench-selection").hidden, true);
            ui.nodes.get("#bench-filter").value = "";
            ui.nodes.get("#bench-filter").listeners.input();
            assert.equal(row42(ui).attributes["aria-pressed"], "true");
            assert.equal(ui.calls.length, 1);
        } finally { await ui.server.close(); }
    }
});

test("Back focuses the filter when the retained selected row is in a collapsed branch", async () => {
    const ui = await open();
    try {
        await load(ui);
        ui.nodes.get("#bench-layout").value = "split";
        ui.nodes.get("#bench-layout").listeners.change();
        row42(ui).listeners.click();
        const toggle = descendants(ui.nodes.get("#bench-rows"))
            .find((node) => node.attributes["aria-expanded"] !== undefined);
        toggle.listeners.click();
        assert.equal(toggle.attributes["aria-expanded"], "false");
        back(ui).listeners.click();
        assert.equal(ui.nodes.get("#bench-filter").focused, true);
        assert.equal(row42(ui).focused, undefined, "hidden descendant is not a focus target");
        assert.equal(row42(ui).attributes["aria-pressed"], "true");
        assert.equal(ui.calls.length, 1);
    } finally { await ui.server.close(); }
});

test("native fallback layout order, visibility and unsupported contributions survive the actual reader path", async () => {
    const detail = structuredClone(detailFixture);
    Object.assign(detail, {
        benchName: fixture.browser.benchName, observation: "raw-mirror",
        origin: { ...fixture.browser.origin }, layoutStatus: "unavailable", layoutSource: "twig-fallback",
        membership: { selected: true, seed: false, pending: true },
        completeness: {
            sourceComplete: false, sourceChildCount: null, cachedChildCount: 0,
            parentCached: null, linksVerified: false, reasons: ["source-unknown", "cache-only"],
        },
    });
    detail.document.processId = "twig.fallback";
    const page = detail.document.pages[0];
    page.sections[0].groups[0].visible = true;
    page.sections[0].groups.push({
        id: "hidden", label: "HIDDEN-GROUP", visible: false, isContribution: false,
        controls: [{ ...page.sections[0].groups[0].controls[0], label: "HIDDEN-GROUP-VALUE" }],
    });
    detail.document.pages.push({ ...structuredClone(page), label: "HIDDEN-PAGE", visible: false });
    page.sections[0].groups.push({
        id: "contribution-group", label: "External group", visible: true, isContribution: true, controls: [],
    });
    detail.document.pages.push({
        id: "contribution-page", label: "External page", pageType: "custom",
        visible: true, isContribution: true, sections: [],
    });
    const ui = await open(fixture, undefined, detail);
    try {
        await load(ui);
        row42(ui).listeners.click();
        await ui.settled();
        const rendered = flatten(ui.nodes.get("#bench-selection"));
        assert.match(rendered, /Twig fallback form shown/);
        assert.match(rendered, /<script>untrusted\(\)<\/script><p>Complete body<\/p>/);
        assert.match(rendered, /Unsupported native contribution/);
        assert.match(rendered, /Unsupported native group contribution/);
        assert.match(rendered, /Unsupported native page contribution/);
        assert.match(rendered, /Page content is not exported/);
        assert.doesNotMatch(rendered, /HIDDEN-GROUP|HIDDEN-PAGE|Omitted field/);
        assert.ok(rendered.indexOf("Title") < rendered.indexOf("Fields"));
        assert.ok(rendered.indexOf("Fields") < rendered.indexOf("Empty field"));
        assert.ok(rendered.indexOf("Empty field") < rendered.indexOf("History"));
        assert.equal(ui.calls.length, 2);
        assert.equal(ui.calls[1].args[0], "bench");
    } finally { await ui.server.close(); }
});

test("failed item detail is a visible alert outside collapsed source evidence", async () => {
    const ui = await open();
    try {
        await load(ui);
        row42(ui).listeners.click();
        await ui.settled();
        const alert = ui.nodes.get("#bench-selection").children.find((node) => node.attributes.role === "alert");
        assert.ok(alert, "detail failure must not disappear inside the source disclosure");
        assert.match(alert.textContent, /Explicit-Bench detail failed/);
        assert.equal(ui.calls.length, 1);
    } finally { await ui.server.close(); }
});

test("rendered workbench VM crosses actual HTTP OPEN/STAGE/RETAIN only on explicit buttons with handle-only capture", async () => {
    const detail = structuredClone(detailFixture);
    Object.assign(detail, {
        benchName: fixture.browser.benchName, observation: "raw-mirror",
        origin: { ...fixture.browser.origin }, layoutStatus: "unavailable", layoutSource: "twig-fallback",
        membership: { selected: true, seed: false, pending: true },
        completeness: { sourceComplete: false, sourceChildCount: null, cachedChildCount: 0,
            parentCached: null, linksVerified: false, reasons: ["source-unknown", "cache-only"] },
    });
    detail.document.processId = "twig.fallback";
    const nativeCalls = [];
    let nativeDocument = {
        nativeContext: {
            itemKey: detail.itemKey, externalIdentity: { providerKind: "ado", resourceScope: "fixture-resource", nativeId: "42" },
            benchId: detail.benchId, benchName: detail.benchName, benchContentsDigest: "digest",
            identityId: detail.identityId, bindingRevision: "1", selectionSource: "checkout",
            selectionRevision: "1", attachmentRevision: "1", storageGeneration: null,
            originDigest: "origin", evidenceDigest: "evidence",
        },
        generation: "9007199254740993", baselineRevision: "17", observedRevision: "17", currentRevision: "17",
        hasConflicts: false, changes: [], conflicts: [],
        systemControls: detail.document.systemControls, pages: detail.document.pages,
        fields: [{ fieldRef: "System.Title", editable: true }], authorized: false, serverFinalAuthority: true,
    };
    const adapter = {
        async open(input) {
            nativeCalls.push({ action: "open", input });
            return { version: 1, status: "opened", committed: true, document: structuredClone(nativeDocument) };
        },
        async stage(input) {
            nativeCalls.push({ action: "stage", input });
            nativeDocument = { ...nativeDocument,
                generation: (BigInt(input.request.generation) + BigInt(input.request.changes.length)).toString(),
                changes: input.request.changes.map(change => ({ ...change, baselineValue: "Read-only fixture" })) };
            return { version: 1, status: "staged", committed: true, document: structuredClone(nativeDocument) };
        },
        async retain(input) {
            nativeCalls.push({ action: "retain", input });
            const canonicalJson = '{"opaqueNativeClaim":900719925474099312345}';
            const { version, ...request } = structuredClone(input.request);
            return { version: 1, status: "retained", committed: true, capture: {
                ...request, currentRevision: request.observedRevision,
                canonicalJson, digest: createHash("sha256").update(canonicalJson).digest("hex"),
                operationId: "draft-" + "b".repeat(64), effects: structuredClone(nativeDocument.changes),
                originCoverage: "editing-context-and-native-origin-digest", authorized: false, canApply: false,
            } };
        },
    };
    const ui = await open(fixture, undefined, detail, adapter);
    const find = label => descendants(ui.nodes.get("#bench-selection")).find(node => node.textContent === label);
    try {
        await load(ui);
        row42(ui).listeners.click();
        await ui.settled();
        assert.equal(nativeCalls.length, 0, "initial render, Bench read and automatic cache detail never OPEN/STAGE");
        find("Open native draft").listeners.click();
        await ui.settled();
        assert.deepEqual(nativeCalls.map(call => call.action), ["open"]);
        assert.deepEqual(nativeCalls[0].input.expectedKey, detail.itemKey);
        const editor = descendants(ui.nodes.get("#bench-selection")).find(node => node.listeners.input && !node.listeners.change);
        assert.ok(editor, "real rendered client exposes the admitted text input");
        editor.value = "Explicit draft text";
        editor.listeners.input();
        find("Stage local changes").listeners.click();
        await ui.settled();
        assert.deepEqual(nativeCalls.map(call => call.action), ["open", "stage"]);
        assert.deepEqual(nativeCalls[1].input.request.nativeContext, nativeDocument.nativeContext);
        assert.equal(nativeCalls[1].input.request.generation, "9007199254740993");
        assert.deepEqual(nativeCalls[1].input.request.changes,
            [{ fieldRef: "System.Title", proposedValue: "Explicit draft text" }]);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /staged: local draft only/);
        const emptyEditor = descendants(ui.nodes.get("#bench-selection")).find(node => node.listeners.input && !node.listeners.change);
        emptyEditor.value = "";
        emptyEditor.listeners.input();
        find("Stage local changes").listeners.click();
        await ui.settled();
        assert.equal(nativeCalls[2].input.request.changes[0].proposedValue, "");
        assert.equal(nativeCalls[2].input.request.generation, "9007199254740994");
        const clear = descendants(ui.nodes.get("#bench-selection")).find(node => node.type === "checkbox");
        clear.checked = true;
        clear.listeners.change();
        find("Stage local changes").listeners.click();
        await ui.settled();
        assert.equal(nativeCalls[3].input.request.changes[0].proposedValue, null);
        assert.equal(nativeCalls[3].input.request.generation, "9007199254740995");
        assert.ok(find("Retain staged draft"), "explicit Retain staged draft button must exist in rendered workbench");
        assert.equal(find("Retain staged draft").disabled, false);
        find("Retain staged draft").listeners.click();
        await ui.settled();
        assert.deepEqual(nativeCalls.map(call => call.action), ["open", "stage", "stage", "stage", "retain"]);
        const retainedRequest = nativeCalls[4].input.request;
        assert.equal(retainedRequest.generation, "9007199254740996");
        assert.deepEqual(retainedRequest.nativeContext, nativeDocument.nativeContext);
        assert.deepEqual(Object.keys(retainedRequest).sort(),
            ["baselineRevision", "generation", "nativeContext", "observedRevision", "version"]);
        const browserRetain = ui.browserRequests.find(call => call.body.action === "retain");
        assert.equal(browserRetain.route, "/api/editing");
        assert.deepEqual(Object.keys(browserRetain.body).sort(), ["action", "captureHandle"]);
        assert.equal(browserRetain.body.captureHandle.length, 43);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /Native capture receipt/);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /Proposed: \(null\)/);
        assert.doesNotMatch(flatten(ui.nodes.get("#bench-selection")), /opaqueNativeClaim|canonicalJson/);
        assert.equal(find("Retain staged draft").disabled, true);
        find("Back to Bench").listeners.click();
        await ui.settled();
        assert.equal(nativeCalls.length, 5, "navigation revokes capture without native mutation");
    } finally { await ui.server.close(); }
});

test("workbench styling is scoped and follows the common rules without changing other canvases", () => {
    const workbench = renderPage({ token: "fixture", nonce: "nonce", workbench: true });
    assert.ok(workbench.indexOf(".twig-workbench section") > workbench.indexOf("section, details"));
    assert.match(workbench, /type="range" min="120" max="600"/);
    for (const options of [{}, { review: true }]) {
        const page = renderPage({ token: "fixture", nonce: "nonce", ...options });
        assert.doesNotMatch(page, /\.twig-workbench section|id="bench-panes"/);
    }
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
        createNativeBenchDetailReader: () => async () => { throw new Error("Bench detail should not be read by a workbench action"); },
        createNativeEditingAdapter: () => ({
            open: async () => { throw new Error("Browsing must not OPEN a draft"); },
            stage: async () => { throw new Error("Browsing must not STAGE intent"); },
        }),
    });
    // Only SDK/import boundaries are replaced; the production declaration and callbacks run unchanged.
    const script = source.replace(/^import .*;$/gm, "")
        .replace("const readNativeDetail = createNativeDetailReader();", "const readNativeDetail = async () => { throw new Error('Detail not admitted here'); };");
    await new Script(`(async () => { ${script}\n })()`).runInContext(context);
    assert.deepEqual(Array.from(declarations, (canvas) => canvas.id), ["twig-item-companion", "twig-workbench", "twig-proposal-review"]);
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
            itemKey: structuredClone(detailFixture.itemKey), identityId: detailFixture.identityId,
            membership: { selected: true, seed: false, pending: false },
            completeness: { sourceComplete: false, sourceChildCount: null, cachedChildCount: 0, parentCached: null, linksVerified: false, reasons: ["source-unknown"] },
            document: { systemControls: [{ id: "x", label, visible: true, value: { state: 0, full: label } }], pages: [] },
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
