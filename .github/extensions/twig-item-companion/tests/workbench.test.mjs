import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { EventEmitter } from "node:events";
import { readFile } from "node:fs/promises";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createNativeBenchReader } from "../native-bench.mjs";
import { createNativeBenchDetailReader } from "../native-bench-detail.mjs";
import { renderPage } from "../render.mjs";
import { createNativeDiscussionReader } from "../native-discussion.mjs";
import { createNativeDiscussionPoster } from "../native-discussion-post.mjs";
import { createNativeEditingAdapter } from "../native-editing.mjs";
import { discussionTarget, discussionPage, discussionRefusal, childHarness } from "./discussion-fixture.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";
import { mkdtemp, rm, stat, utimes, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { admitTwigExecutable, ExecutableError } from "../native-executable.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/bench-v2.json", import.meta.url), "utf8"));
const detailFixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
const workspacePath = fixture.browser.origin.worktreeRoot;
function reader(payload = fixture, calls = [], fail = () => null) {
    return createNativeBenchReader({ executable: fakeExecutable(),
        realpath: async (path) => path,
        execFile(exe, args, options, callback) {
            calls.push({ exe, args, options });
            callback(fail(), JSON.stringify(payload), "");
        },
    });
}

test("legacy detail without a discussion reader does not require discussion-only membership", async () => {
    const detail = structuredClone(detailFixture);
    assert.equal(Object.hasOwn(detail, "membership"), false);
    const server = await createCanvasServer({
        workspacePath: "fixture", workbench: true,
        readNativeBench: async () => fixture.browser,
        readNativeBenchDetail: async () => detail,
    });
    try {
        await server.loadBench("7");
        let loaded;
        await assert.doesNotReject(async () => {
            loaded = await server.loadBenchDetail(42);
        }, "legacy detail without a discussion reader must not require discussion-only membership");
        assert.equal(loaded, detail);
    } finally {
        await server.close();
    }
});

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
function dom(html, url, transformResponse) {
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
            const response = fetch(new URL(route, url), options);
            const request = transformResponse ? response.then(value => transformResponse(route, options, value)) : response;
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
                await Promise.allSettled([...pending]);
                await new Promise((resolve) => setTimeout(resolve, 5));
                if (!pending.size && !nodes.get("#bench-load")?.disabled) return;
            }
            assert.fail("Client did not settle");
        },
    };
}
function flatten(node) { return [node.textContent, ...node.children.flatMap(flatten)].join("\n"); }
function descendants(node) { return [node, ...node.children.flatMap(descendants)]; }
async function open(payload = fixture, fail, detailPayload, nativeEditing, handoffSession, transformResponse, readNativeDiscussion, nativeDiscussionPost) {
    const calls = [];
    const server = await createCanvasServer({
        workspacePath: payload.browser.origin.worktreeRoot, workbench: true, readNativeBench: reader(payload, calls, fail),
        nativeEditing, handoffSession, readNativeDiscussion, nativeDiscussionPost,
        readNativeBenchDetail: detailPayload && createNativeBenchDetailReader({ executable: fakeExecutable(),
            realpath: async (path) => path,
            execFile(exe, args, options, callback) {
                calls.push({ exe, args, options });
                callback(null, JSON.stringify(detailPayload), "");
            },
        }),
    });
    const html = await (await fetch(server.url)).text();
    const ui = dom(html, server.url, transformResponse);
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
        find("Open local draft (not published)").listeners.click();
        await ui.settled();
        assert.deepEqual(nativeCalls.map(call => call.action), ["open"]);
        assert.deepEqual(nativeCalls[0].input.expectedKey, detail.itemKey);
        const editor = descendants(ui.nodes.get("#bench-selection")).find(node => node.listeners.input && !node.listeners.change);
        assert.ok(editor, "real rendered client exposes the admitted text input");
        editor.value = "Explicit draft text";
        editor.listeners.input();
        find("Stage local draft only").listeners.click();
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
        find("Stage local draft only").listeners.click();
        await ui.settled();
        assert.equal(nativeCalls[2].input.request.changes[0].proposedValue, "");
        assert.equal(nativeCalls[2].input.request.generation, "9007199254740994");
        const clear = descendants(ui.nodes.get("#bench-selection")).find(node => node.type === "checkbox");
        clear.checked = true;
        clear.listeners.change();
        find("Stage local draft only").listeners.click();
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

async function discussionSetup(handler = () => ({ value: discussionPage(), code: 0 }), transformResponse, nativeEditing, handoffSession, executable = fakeExecutable(), nativeDiscussionPost) {
    const detail = structuredClone(detailFixture);
    Object.assign(detail, {
        benchName: fixture.browser.benchName, observation: "raw-mirror", origin: { ...fixture.browser.origin },
        layoutStatus: "unavailable", layoutSource: "twig-fallback",
        membership: { selected: true, seed: false, pending: true },
        completeness: { sourceComplete: false, sourceChildCount: null, cachedChildCount: 0,
            parentCached: null, linksVerified: false, reasons: ["source-unknown", "cache-only"] },
    });
    detail.document.processId = "twig.fallback";
    const h = childHarness(handler);
    const readNativeDiscussion = createNativeDiscussionReader({ executable, ...h, realpath: async value => value });
    const ui = await open(fixture, undefined, detail, nativeEditing, handoffSession, transformResponse, readNativeDiscussion, nativeDiscussionPost);
    const find = label => descendants(ui.nodes.get("#bench-selection")).find(node => node.textContent === label);
    const token = JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]);
    const http = (body, extraHeaders = {}, method = "POST") => fetch(new URL("/api/discussion", ui.server.url), {
        method, headers: { Authorization: "Bearer " + token, "Content-Type": "application/json", ...extraHeaders },
        ...(method === "POST" ? { body: typeof body === "string" ? body : JSON.stringify(body) } : {}),
    });
    await load(ui);
    row42(ui).listeners.click();
    await ui.settled();
    return { ...ui, native: h, find, http };
}

test("discussion post real VM HTTP prepares inert exact review and only trusted explicit click submits digest once", async () => {
    const calls = [];
    const nativeDiscussionPost = {
        async prepare(input) {
            calls.push({ action: "prepare", input });
            return { digest: "a".repeat(64), target: input.context.itemKey, bench: input.context.benchName,
                identity: input.context.identityId, workspace: { organization: "fixture", project: "Work" },
                text: input.text, format: input.format, effectiveBody: "<p>&lt;script&gt;inert&lt;/script&gt;</p>\n" };
        },
        async apply(input) {
            calls.push({ action: "apply", input });
            return { status: "verified", commentId: "9007199254740993" };
        },
    };
    const ui = await discussionSetup(undefined, undefined, undefined, undefined, fakeExecutable(), nativeDiscussionPost);
    try {
        const prepare = ui.find("Prepare discussion proposal");
        assert.ok(prepare, "real production workbench must expose separate explicit discussion preparation");
        assert.equal(calls.length, 0);
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        const editor = descendants(ui.nodes.get("#bench-selection")).find(node =>
            node.attributes["aria-label"] === "Discussion comment plain text");
        assert.ok(editor);
        editor.value = "<script>inert</script>";
        editor.listeners.input();
        prepare.listeners.click({ isTrusted: true }); await ui.settled();
        assert.deepEqual(calls.map(call => call.action), ["prepare"]);
        assert.equal(calls[0].input.context.itemKey.workItemId, 42);
        assert.equal(calls[0].input.text, "<script>inert</script>");
        assert.match(flatten(ui.nodes.get("#bench-selection")), /<script>inert<\/script>/);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /a{64}/);
        const apply = ui.find("Authorize and post exact digest once");
        assert.ok(apply);
        apply.listeners.click({ isTrusted: false }); await ui.settled();
        assert.equal(calls.length, 1, "synthetic click cannot authorize");
        apply.listeners.click({ isTrusted: true }); await ui.settled();
        apply.listeners.click({ isTrusted: true }); await ui.settled();
        assert.deepEqual(calls.map(call => call.action), ["prepare", "apply"]);
        assert.equal(calls[1].input.confirm, "a".repeat(64));
        assert.match(flatten(ui.nodes.get("#bench-selection")), /9007199254740993/);
        const post = ui.browserRequests.find(call => call.route === "/api/discussion-post" && call.body.action === "apply");
        assert.deepEqual(Object.keys(post.body).sort(), ["action", "captureHandle", "confirm", "targetHandle"]);
    } finally { await ui.server.close(); }
});

test("discussion post real HTTP refuses browser authority, stale context and duplicates; unknown survives navigation", async () => {
    const calls = [];
    const ui = await discussionSetup(undefined, undefined, undefined, undefined, fakeExecutable(), {
        async prepare(input) {
            calls.push("prepare");
            return { digest: "a".repeat(64), target: input.context.itemKey, bench: input.context.benchName,
                identity: input.context.identityId, workspace: { organization: "fixture", project: "Work" },
                text: input.text, format: input.format, effectiveBody: "<p>exact</p>\n" };
        },
        async apply() { calls.push("apply"); throw new Error("PRIVATE acknowledgment lost"); },
    });
    const token = JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]);
    const post = (body, extra = {}) => fetch(new URL("/api/discussion-post", ui.server.url), {
        method: "POST", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json", ...extra },
        body: JSON.stringify(body),
    });
    try {
        const handle = (await (await post({ action: "prepare", targetHandle: "a".repeat(43), format: "plain", text: "exact" })).json());
        assert.match(handle.error, /Read current qualified discussion/);
        assert.equal((await post({ action: "prepare", targetHandle: "a".repeat(43), format: "plain", text: "exact", workItemId: 99 })).status, 400);
        assert.equal((await post({}, { Origin: "https://evil.invalid" })).status, 403);
        assert.deepEqual(calls, []);
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        const editor = descendants(ui.nodes.get("#bench-selection")).find(node =>
            node.attributes["aria-label"] === "Discussion comment plain text");
        editor.value = "exact"; editor.listeners.input();
        ui.find("Prepare discussion proposal").listeners.click({ isTrusted: true }); await ui.settled();
        const capture = ui.browserRequests.find(call => call.route === "/api/discussion-post").body;
        const state = await (await fetch(new URL("/api/state", ui.server.url), {
            headers: { Authorization: "Bearer " + token },
        })).json();
        assert.deepEqual(state.discussionPostOutcomes, []);
        ui.find("Authorize and post exact digest once").listeners.click({ isTrusted: true }); await ui.settled();
        assert.deepEqual(calls, ["prepare", "apply"]);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /outcome is unknown/);
        assert.doesNotMatch(flatten(ui.nodes.get("#bench-selection")), /PRIVATE/);
        ui.find("Back to Bench").listeners.click(); await ui.settled();
        row42(ui).listeners.click(); await ui.settled();
        assert.match(flatten(ui.nodes.get("#bench-selection")), /Previous post.*outcome-unknown/);
        assert.equal(ui.find("Prepare discussion proposal").disabled, true);
        const duplicate = ui.browserRequests.find(call => call.route === "/api/discussion-post" && call.body.action === "apply").body;
        assert.equal((await post(duplicate)).status, 409);
        assert.deepEqual(calls, ["prepare", "apply"]);
        assert.equal(capture.format, "plain");
    } finally { await ui.server.close(); }
});

test("discussion real VM HTTP native adapter reads only on explicit online Read with inert qualified page", async () => {
    const ui = await discussionSetup();
    try {
        assert.ok(ui.find("Read discussion (online)"), "explicit online Read discussion button must exist");
        assert.equal(ui.native.calls.length, 0, "selection and cached detail must not read discussion");
        ui.find("Read discussion (online)").listeners.click();
        await ui.settled();
        assert.equal(ui.native.calls.length, 1);
        const call = ui.native.calls[0];
        assert.deepEqual(call.args, ["bench", "discussion", "--stdin", "-o", "json"]);
        assert.deepEqual(call.input, { version: 1, itemKey: discussionTarget().expectedKey,
            bench: "7", expectBench: "7", expectIdentity: fixture.browser.origin.identityId, cursor: null });
        assert.equal(call.options.shell, false);
        assert.equal(call.options.cwd, fixture.browser.origin.worktreeRoot);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /<script>evil\(\)<\/script>/);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /deleted: true; format \(inert\): html/);
        const body = ui.browserRequests.find(call => call.route === "/api/discussion").body;
        assert.deepEqual(Object.keys(body).sort(), ["action", "targetHandle"]);
        assert.equal(body.targetHandle.length, 43);
        assert.doesNotMatch(JSON.stringify(ui.browserRequests), /providerToken|private\+token|expectIdentity|checkoutRoot/);
        const state = await (await fetch(new URL("/api/state", ui.server.url), {
            headers: { Authorization: "Bearer " + JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]) },
        })).text();
        assert.doesNotMatch(state, /private\+token|providerToken|nextCursor|benchContentsDigest/);
    } finally { await ui.server.close(); }
});

test("discussion real VM HTTP Next keeps native cursor server-only and replaces page at exhausted traversal", async () => {
    const ui = await discussionSetup((call, number) => {
        const value = discussionPage(undefined, number === 1);
        if (number === 2) { value.comments = []; value.paging.count = "0"; }
        return { value };
    });
    try {
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        assert.equal(ui.native.calls.length, 1);
        ui.find("Next discussion page").listeners.click(); await ui.settled();
        assert.equal(ui.native.calls.length, 2);
        assert.deepEqual(ui.native.calls[1].input.cursor, discussionPage().paging.nextCursor);
        assert.equal(ui.find("Next discussion page").disabled, true);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /contains no entries/);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /End of this traversal, not a complete snapshot/);
        assert.doesNotMatch(flatten(ui.nodes.get("#bench-selection")), /Comment 9007199254740993/);
        assert.deepEqual(Object.keys(ui.browserRequests.filter(call => call.route === "/api/discussion")[1].body).sort(),
            ["action", "pageHandle", "targetHandle"]);
    } finally { await ui.server.close(); }
});

test("discussion real HTTP rejects method/origin/auth and browser target/cursor authority before native read", async () => {
    const ui = await discussionSetup();
    try {
        assert.equal((await ui.http({}, {}, "GET")).status, 404);
        assert.equal((await ui.http({}, { Origin: "https://evil.invalid" })).status, 403);
        assert.equal((await ui.http({}, { Authorization: "wrong" })).status, 401);
        assert.equal((await ui.http({ action: "read", targetHandle: "a".repeat(43), cursor: discussionPage().paging.nextCursor })).status, 400);
        assert.equal((await ui.http({ action: "read", targetHandle: "a".repeat(43), workItemId: 99 })).status, 400);
        assert.equal((await ui.http('{"action":"read","action":"next","targetHandle":"' + "a".repeat(43) + '"}')).status, 400);
        assert.equal((await ui.http({ action: "read", targetHandle: "a".repeat(43) })).status, 409);
        assert.equal(ui.native.calls.length, 0);
    } finally { await ui.server.close(); }
});

test("discussion real VM native refusal or lost HTTP keeps uncertainty honest without automatic second read", async () => {
    for (const loss of [false, true]) {
        const ui = await discussionSetup(() => loss ? { value: discussionPage() } :
            { value: discussionRefusal(), code: 1, stderr: "PRIVATE PROVIDER RESPONSE" },
        loss ? async (route, options, response) => {
            if (route === "/api/discussion" && JSON.parse(options.body).action === "read") {
                await response.text(); throw new Error("PRIVATE ACK");
            }
            return response;
        } : undefined);
        try {
            ui.find("Read discussion (online)").listeners.click(); await ui.settled();
            assert.equal(ui.native.calls.length, 1);
            assert.match(flatten(ui.nodes.get("#bench-selection")), loss ? /unavailable.*no page was accepted/ : /discussion-offline/);
            assert.doesNotMatch(flatten(ui.nodes.get("#bench-selection")), /PRIVATE|private diagnostic|private token/);
            assert.equal(ui.find("Next discussion page").disabled, true);
        } finally { await ui.server.close(); }
    }
});

// A real admitted handle over a temporary file; only the help probe is faked, so ExecutableError
// messages are produced by the production helper rather than by the test.
async function admittedDiscussionExecutable(discussionHelp) {
    const root = await mkdtemp(path.join(os.tmpdir(), "twig-discussion-exec-"));
    const file = path.join(root, process.platform === "win32" ? "twig.exe" : "twig");
    await writeFile(file, "fake-twig-binary-v1", { mode: 0o755 });
    const probes = [];
    const execFile = (exe, args, options, callback) => {
        probes.push(args);
        queueMicrotask(() => callback(null, discussionHelp, ""));
        return {};
    };
    const executable = await admitTwigExecutable({ canvas: "workbench", env: { PATH: root, Path: root }, execFile });
    return { root, file, executable, probes };
}

test("discussion real VM HTTP surfaces a missing native discussion capability with its path, hash and reopen recovery", async () => {
    const pinned = await admittedDiscussionExecutable("Usage: bench discussion [options...]\n\nOptions:\n  -o, --output <string>    Output format.\n  --stdin-file <string>    Prefix only.\n");
    const ui = await discussionSetup(undefined, undefined, undefined, undefined, pinned.executable);
    try {
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        assert.equal(ui.native.calls.length, 0, "a refused capability must not start the native discussion command");
        assert.deepEqual(pinned.probes, [["bench", "discussion", "--help"]]);
        const shown = flatten(ui.nodes.get("#bench-selection"));
        assert.match(shown, /twig bench discussion --help does not offer --stdin/);
        assert.ok(shown.includes(pinned.executable.path), "the refused executable path must be visible");
        assert.ok(shown.includes(pinned.executable.sha256.slice(0, 12)), "the refused executable hash must be visible");
        assert.match(shown, /reopen the canvas/i);
        assert.equal(ui.find("Read discussion (online)").disabled, true, "same-handle read again cannot succeed");
        assert.equal(ui.find("Next discussion page").disabled, true);
    } finally { await ui.server.close(); await rm(pinned.root, { recursive: true, force: true }); }
});

test("discussion real VM HTTP executable_changed keeps the previous page labelled and requires reopen without spawning", async () => {
    const pinned = await admittedDiscussionExecutable("Usage: bench discussion [options...]\n\nOptions:\n  --stdin    Read JSON.\n  -o, --output <string>    Output format.\n");
    const ui = await discussionSetup(undefined, undefined, undefined, undefined, pinned.executable);
    try {
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        assert.equal(ui.native.calls.length, 1);
        assert.equal(ui.native.calls[0].exe, pinned.executable.path);
        const before = await stat(pinned.file);
        await writeFile(pinned.file, "fake-twig-binary-v2");
        await utimes(pinned.file, before.atime, before.mtime);
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        assert.equal(ui.native.calls.length, 1, "a changed executable must not be launched");
        const shown = flatten(ui.nodes.get("#bench-selection"));
        assert.match(shown, /changed after this canvas opened/);
        assert.ok(shown.includes(pinned.executable.path));
        assert.match(shown, /Reopen the canvas/);
        assert.match(shown, /Displayed page is a previous observation/);
        assert.match(shown, /Comment 9007199254740993/, "the earlier page remains as a labelled previous observation");
        assert.equal(ui.find("Read discussion (online)").disabled, true);
        assert.equal(ui.find("Next discussion page").disabled, true);
    } finally { await ui.server.close(); await rm(pinned.root, { recursive: true, force: true }); }
});

test("discussion real VM HTTP keeps untyped errors that merely claim an executable code redacted", async () => {
    const forged = Object.assign(new Error("PRIVATE PROVIDER STDERR"), { code: "executable_changed" });
    const ui = await discussionSetup(undefined, undefined, undefined, undefined, fakeExecutable({ refuse: forged }));
    try {
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        assert.equal(ui.native.calls.length, 0);
        const shown = flatten(ui.nodes.get("#bench-selection"));
        assert.match(shown, /Discussion read unavailable; no page was accepted/);
        assert.doesNotMatch(shown, /PRIVATE/);
        assert.equal(ui.find("Read discussion (online)").disabled, false, "an untyped failure keeps explicit read again");
    } finally { await ui.server.close(); }
});
test("discussion real HTTP invalidation cancels owned read and old handle cannot revoke same-item new selection", async () => {
    const ui = await discussionSetup(() => null);
    try {
        ui.find("Read discussion (online)").listeners.click();
        await until(() => ui.native.calls.length === 1);
        const old = ui.browserRequests.find(call => call.route === "/api/discussion").body.targetHandle;
        const readButton = ui.find("Read discussion (online)");
        readButton.listeners.click();
        assert.equal(ui.native.calls.length, 1);
        ui.find("Back to Bench").listeners.click();
        await ui.settled();
        assert.deepEqual(ui.native.stops, [12345]);
        row42(ui).listeners.click(); await ui.settled();
        assert.ok(ui.find("Read discussion (online)"));
        const lateInvalidate = await ui.http({ action: "invalidate", targetHandle: old });
        assert.equal(lateInvalidate.status, 200);
        const oldRead = await ui.http({ action: "read", targetHandle: old });
        assert.equal(oldRead.status, 409);
        ui.find("Read discussion (online)").listeners.click();
        await until(() => ui.native.calls.length === 2);
        ui.find("Back to Bench").listeners.click(); await ui.settled();
        assert.equal(ui.native.stops.length, 2, "new selection remained usable after stale invalidation");
    } finally { await ui.server.close(); }
});

test("discussion real HTTP close aborts and reaps an active native read without exposing a late page", async () => {
    const ui = await discussionSetup(() => null);
    ui.find("Read discussion (online)").listeners.click();
    await until(() => ui.native.calls.length === 1);
    await ui.server.close();
    await ui.settled();
    assert.deepEqual(ui.native.stops, [12345]);
    assert.doesNotMatch(flatten(ui.nodes.get("#bench-selection")), /Comment 9007199254740993/);
});

test("discussion real HTTP read blocks editing and handoff while owned provider read is active", async () => {
    const ui = await discussionSetup(() => null);
    try {
        ui.find("Read discussion (online)").listeners.click();
        await until(() => ui.native.calls.length === 1);
        const token = JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]);
        for (const route of ["editing", "handoff"]) {
            const response = await fetch(new URL("/api/" + route, ui.server.url), { method: "POST",
                headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
                body: JSON.stringify({ action: route === "editing" ? "open" : "prepare" }) });
            assert.equal(response.status, 409);
        }
        assert.equal(ui.native.calls.length, 1);
        ui.find("Back to Bench").listeners.click(); await ui.settled();
    } finally { await ui.server.close(); }
});

test("discussion real HTTP read refuses reverse overlap with native editing or handoff preflight", async () => {
    for (const operation of ["editing", "handoff"]) {
        let release, entered = false;
        const gate = new Promise(resolve => { release = resolve; });
        const adapter = { open: async () => { entered = true; await gate; throw new Error("fixture editing stopped"); } };
        const session = { sessionId: "fixture-session", send: async () => assert.fail("No SDK send admitted"),
            rpc: { metadata: { snapshot: async () => { entered = true; await gate;
                throw new Error("fixture preflight stopped"); } } } };
        const ui = await discussionSetup(undefined, undefined, adapter, session);
        let pending;
        try {
            ui.find("Read discussion (online)").listeners.click(); await ui.settled();
            const targetHandle = ui.browserRequests.find(call => call.route === "/api/discussion").body.targetHandle;
            const token = JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]);
            const post = (route, body) => fetch(new URL("/api/" + route, ui.server.url), { method: "POST",
                headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
                body: JSON.stringify(body) });
            if (operation === "editing") pending = post("editing", { action: "open" });
            else {
                const prepared = await (await post("handoff", { action: "prepare" })).json();
                pending = post("handoff", { action: "start", handle: prepared.handoff.handle, brief: "fixture" });
            }
            await until(() => entered);
            assert.equal((await ui.http({ action: "read", targetHandle })).status, 409);
            assert.equal(ui.native.calls.length, 1);
        } finally { release(); await pending; await ui.server.close(); }
    }
});

test("discussion real HTTP disconnect aborts its owned child and cannot return a late provider page", async () => {
    const ui = await discussionSetup((call, number) => number === 1 ? { value: discussionPage() } : null);
    try {
        ui.find("Read discussion (online)").listeners.click(); await ui.settled();
        const targetHandle = ui.browserRequests.find(call => call.route === "/api/discussion").body.targetHandle;
        const token = JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]);
        const controller = new AbortController();
        const pending = fetch(new URL("/api/discussion", ui.server.url), { method: "POST", signal: controller.signal,
            headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
            body: JSON.stringify({ action: "read", targetHandle }) });
        const rejected = assert.rejects(pending);
        await until(() => ui.native.calls.length === 2);
        controller.abort();
        await rejected;
        await until(() => ui.native.stops.length === 1);
        assert.deepEqual(ui.native.stops, [12345]);
    } finally { await ui.server.close(); }
});

async function handoffSetup(send, transformResponse) {
    const bench = structuredClone(fixture);
    bench.browser.origin.worktreeRoot = process.cwd();
    const detail = structuredClone(detailFixture);
    Object.assign(detail, {
        benchName: bench.browser.benchName, observation: "raw-mirror",
        origin: { ...bench.browser.origin }, layoutStatus: "unavailable", layoutSource: "twig-fallback",
        membership: { selected: true, seed: false, pending: true },
        completeness: { sourceComplete: false, sourceChildCount: null, cachedChildCount: 0,
            parentCached: null, linksVerified: false, reasons: ["source-unknown", "cache-only"] },
    });
    detail.itemKey.checkoutRoot = process.cwd();
    detail.document.processId = "twig.fallback";
    const session = { sessionId: "fixture-session", rpc: { metadata: { snapshot: async () => ({
        sessionId: "fixture-session", workingDirectory: process.cwd(), isRemote: false,
    }) } }, send };
    return open(bench, undefined, detail, undefined, session, transformResponse);
}

test("rendered VM crosses HTTP handoff only after explicit Prepare and Start with edited brief", async () => {
    const sends = [];
    const ui = await handoffSetup(async options => { sends.push(options); return "fixture-message"; });
    const find = label => descendants(ui.nodes.get("#bench-selection")).find(node => node.textContent === label);
    try {
        await load(ui);
        row42(ui).listeners.click();
        await ui.settled();
        assert.ok(find("Start in this session"), "explicit in-session Start button must exist");
        assert.equal(sends.length, 0);
        assert.equal(find("Start in this session").disabled, true);
        const token = JSON.parse(ui.html.match(/const token = ("[^"]+");/)[1]);
        const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
        const post = (body, overrides = {}) => fetch(new URL("/api/handoff", ui.server.url), {
            method: "POST", headers: { ...headers, ...overrides }, body: typeof body === "string" ? body : JSON.stringify(body),
        });
        assert.equal((await post({ action: "prepare" }, { Authorization: "" })).status, 401);
        assert.equal((await post({ action: "prepare" }, { Origin: "https://invalid.example" })).status, 403);
        assert.equal((await post('{"action":"prepare","action":"start"}')).status, 400);
        assert.equal((await post({ action: "prepare", padding: "x".repeat(16384) })).status, 413);
        for (const extra of [{ sessionId: "forged" }, { nativeContext: {} }, { workItemId: 43 },
            { agent: "other" }, { model: "other" }, { authorize: true }]) {
            assert.equal((await post({ action: "prepare", ...extra })).status, 400);
            assert.equal((await post({ action: "start", handle: "forged", brief: "task", ...extra })).status, 400);
        }
        assert.equal(sends.length, 0);
        find("Prepare handoff brief").listeners.click();
        await ui.settled();
        assert.equal(sends.length, 0, "preparation does not send");
        const brief = descendants(ui.nodes.get("#bench-selection")).find(node => node.rows === 5);
        brief.value = "<script>My explicit task</script>";
        brief.listeners.input();
        find("Start in this session").listeners.click();
        await ui.settled();
        assert.equal(sends.length, 1);
        assert.ok(sends[0].prompt.endsWith(brief.value));
        assert.equal(sends[0].mode, "enqueue");
        const submitted = ui.browserRequests.find(call => call.body.action === "start");
        assert.equal(submitted.route, "/api/handoff");
        assert.deepEqual(Object.keys(submitted.body).sort(), ["action", "brief", "handle"]);
        assert.match(flatten(ui.nodes.get("#bench-selection")), /fixture-message/);
        find("Back to Bench").listeners.click();
        await ui.settled();
        row42(ui).listeners.click();
        await ui.settled();
        assert.equal(find("Start in this session").disabled, true);
        assert.equal(sends.length, 1);
    } finally { await ui.server.close(); }
});

test("real VM HTTP status reconciles delayed admission after server deadline without another Start", async () => {
    let resolve;
    const pending = new Promise(yes => { resolve = yes; });
    let sends = 0;
    const ui = await handoffSetup(async () => { sends++; return pending; });
    const find = label => descendants(ui.nodes.get("#bench-selection")).find(node => node.textContent === label);
    try {
        await load(ui);
        row42(ui).listeners.click();
        await ui.settled();
        find("Prepare handoff brief").listeners.click();
        await ui.settled();
        find("Start in this session").listeners.click();
        await ui.settled();
        assert.match(flatten(ui.nodes.get("#bench-selection")), /outcome unknown/);
        const brief = descendants(ui.nodes.get("#bench-selection")).find(node => node.rows === 5);
        brief.value = "newer text after deadline";
        brief.listeners.input();
        resolve("late-admission-id");
        await new Promise(yes => setImmediate(yes));
        find("Check handoff status").listeners.click();
        await ui.settled();
        assert.match(flatten(ui.nodes.get("#bench-selection")), /admitted.*late-admission-id/);
        assert.equal(brief.value, "newer text after deadline");
        assert.equal(find("Start in this session").disabled, true);
        assert.equal(find("Prepare handoff brief").disabled, true);
        assert.equal(sends, 1);
        assert.equal(ui.browserRequests.filter(call => call.body.action === "start").length, 1);
    } finally { resolve("cleanup"); await ui.server.close(); }
});

test("real VM HTTP status recovers lost HTTP acknowledgement under original target only", async () => {
    let sends = 0;
    const ui = await handoffSetup(async () => { sends++; return "lost-http-message"; }, async (route, options, response) => {
        if (route === "/api/handoff" && JSON.parse(options.body).action === "start") {
            await response.text();
            throw new Error("Simulated lost HTTP acknowledgement");
        }
        return response;
    });
    const find = label => descendants(ui.nodes.get("#bench-selection")).find(node => node.textContent === label);
    try {
        await load(ui);
        row42(ui).listeners.click();
        await ui.settled();
        find("Prepare handoff brief").listeners.click();
        await ui.settled();
        find("Start in this session").listeners.click();
        await ui.settled();
        assert.match(flatten(ui.nodes.get("#bench-selection")), /acknowledgement unavailable/);
        find("Back to Bench").listeners.click();
        await ui.settled();
        row42(ui).listeners.click();
        await ui.settled();
        find("Check handoff status").listeners.click();
        await ui.settled();
        assert.match(flatten(ui.nodes.get("#bench-selection")), /admitted.*lost-http-message/);
        assert.equal(find("Start in this session").disabled, true);
        assert.equal(find("Prepare handoff brief").disabled, true);
        assert.equal(sends, 1);
        assert.equal(ui.browserRequests.filter(call => call.body.action === "start").length, 1);
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

test("actual extension actions explicitly open and stage a local draft through the owned server bridge", async () => {
    const calls = [], nativeCalls = [], admitted = [];
    const memberDetail = structuredClone(detailFixture);
    Object.assign(memberDetail, {
        benchName: fixture.browser.benchName, observation: "raw-mirror",
        origin: { ...fixture.browser.origin }, layoutStatus: "unavailable", layoutSource: "twig-fallback",
        membership: { selected: true, seed: false, pending: true },
        completeness: { sourceComplete: false, sourceChildCount: null, cachedChildCount: 0,
            parentCached: null, linksVerified: false, reasons: ["source-unknown", "cache-only"] },
    });
    memberDetail.benchId = fixture.browser.benchId;
    memberDetail.document.processId = "twig.fallback";
    let conflictOnOpen = false;
    let staleOriginOnStage = false;
    let nativeDocument = {
        nativeContext: {
            itemKey: memberDetail.itemKey,
            externalIdentity: { providerKind: "ado", resourceScope: "fixture-resource", nativeId: "42" },
            benchId: memberDetail.benchId, benchName: memberDetail.benchName, benchContentsDigest: "digest",
            identityId: memberDetail.identityId, bindingRevision: "1", selectionSource: "checkout",
            selectionRevision: "1", attachmentRevision: "1", storageGeneration: null,
            originDigest: "origin", evidenceDigest: "evidence",
        },
        generation: "9007199254740993", baselineRevision: "17", observedRevision: "17", currentRevision: "17",
        hasConflicts: false, changes: [], conflicts: [],
        systemControls: memberDetail.document.systemControls, pages: memberDetail.document.pages,
        fields: [{ fieldRef: "System.Title", editable: true }],
        authorized: false, serverFinalAuthority: true,
    };
    const nativeEditing = createNativeEditingAdapter({
        executable: fakeExecutable(),
        realpath: async value => value,
        launch(executable, args, options, callback) {
            const action = args[1];
            const input = new EventEmitter();
            const child = new EventEmitter();
            const call = { action, args, input: null };
            nativeCalls.push(call);
            child.stdin = input;
            child.pid = 123;
            input.end = value => { call.input = value; };
            queueMicrotask(() => {
                const document = structuredClone(nativeDocument);
                let result;
                if (action === "open") {
                    if (conflictOnOpen) {
                        document.observedRevision = document.currentRevision = "18";
                        document.hasConflicts = true;
                        document.conflicts = [{ fieldRef: "System.Title", baselineValue: "Read-only fixture",
                            proposedValue: "Local intent", observedValue: "External change" }];
                    }
                    result = { version: 1, status: "opened", committed: true, document };
                } else {
                    const request = JSON.parse(call.input);
                    if (staleOriginOnStage) {
                        result = { version: 1, status: "refused", committed: false,
                            error: { code: "origin-changed", message: "The captured origin changed.",
                                recovery: "Explicitly reopen the local draft." } };
                    } else {
                        document.generation = (BigInt(request.generation) + BigInt(request.changes.length)).toString();
                        document.changes = request.changes.map(change => ({ ...change, baselineValue: "Read-only fixture" }));
                        nativeDocument = structuredClone(document);
                        result = { version: 1, status: "staged", committed: true, document };
                    }
                }
                const exitCode = result.status === "refused" ? 1 : 0;
                const error = exitCode ? Object.assign(new Error("Native editing refusal."), { code: exitCode }) : null;
                callback(error, JSON.stringify(result), "");
                child.emit("close", exitCode, null);
            });
            return child;
        },
    });
    let declarations;
    const source = await readFile(new URL("../extension.mjs", import.meta.url), "utf8");
    const context = createContext({
        Error, path, process,
        realpath: async () => workspacePath,
        stat: async () => ({ isDirectory: () => true }),
        CanvasError: class extends Error { constructor(code, message) { super(message); this.code = code; } },
        createCanvas: (declaration) => declaration,
        joinSession: async (config) => { declarations = config.canvases; return { workspacePath }; },
        createCanvasServer,
        ExecutableError: class extends Error {},
        admitTwigExecutable: async ({ canvas }) => { admitted.push(canvas); return fakeExecutable(); },
        createNativeDetailReader: () => { throw new Error("Detail should not be read by a workbench action"); },
        createNativeBenchReader: () => reader(fixture, calls),
        createNativeBenchDetailReader: () => async ({ workItemId }) => {
            calls.push({ action: "bench-detail", workItemId });
            return structuredClone(memberDetail);
        },
        createNativeDiscussionReader: () => async () => { throw new Error("Browsing must not read online discussion"); },
        createNativeDiscussionPoster,
        createNativeAttachmentReader: () => async () => { throw new Error("Browsing must not transfer attachments"); },
        createNativeEditingAdapter: () => nativeEditing,
    });
    // Only SDK/import boundaries are replaced; the production declaration and callbacks run unchanged.
    const script = source.replace(/^import .*;$/gm, "");
    await new Script(`(async () => { ${script}\n })()`).runInContext(context);
    assert.deepEqual(Array.from(declarations, (canvas) => canvas.id), ["twig-item-companion", "twig-workbench", "twig-proposal-review"]);
    const workbench = declarations.find((canvas) => canvas.id === "twig-workbench");
    const ctx = { instanceId: "fixture-workbench", session: { workingDirectory: process.cwd() },
        input: { bench: "Release focus" } };
    const opened = await workbench.open(ctx);
    try {
        assert.equal((await workbench.open(ctx)).url, opened.url);
        assert.deepEqual(admitted, ["workbench"], "reopening an instance does not re-admit");
        const loaded = await workbench.actions.find((action) => action.name === "load_bench").handler(ctx);
        assert.deepEqual(loaded, fixture.browser);
        assert.deepEqual(nativeCalls, [], "Bench reads never OPEN or STAGE a local draft");
        const openAction = workbench.actions.find(action => action.name === "open_local_draft");
        const stageAction = workbench.actions.find(action => action.name === "stage_local_draft");
        assert.ok(openAction && stageAction, "both explicit local draft actions are registered");
        assert.deepEqual(JSON.parse(JSON.stringify(openAction.inputSchema)), {
            type: "object", properties: { workItemId: { type: "integer", minimum: 1, maximum: 2147483647 } },
            required: ["workItemId"], additionalProperties: false,
        });
        assert.equal(stageAction.inputSchema.additionalProperties, false);
        assert.deepEqual(nativeCalls, [], "declaring actions does not acquire native draft state");
        const openedDraft = await openAction.handler({ ...ctx, input: { workItemId: 42 } });
        assert.deepEqual(nativeCalls.map(call => call.action), ["open"]);
        assert.equal(openedDraft.status, "opened");
        assert.equal(openedDraft.effects.observationCommitted, true);
        assert.equal(openedDraft.effects.localOnly, true);
        assert.equal(openedDraft.generation, "9007199254740993");
        assert.equal(openedDraft.baselineRevision, "17");
        assert.equal(openedDraft.hasConflicts, false);
        assert.equal(openedDraft.canStage, true);
        assert.equal(openedDraft.authorized, false);
        assert.equal(openedDraft.published, false);
        assert.equal(openedDraft.canApply, false);
        assert.equal(openedDraft.captureHandle, undefined);
        assert.equal(openedDraft.nativeContext, undefined);
        const expected = {
            expectedItemKey: openedDraft.itemKey,
            expectedBenchId: openedDraft.benchId,
            expectedIdentityId: openedDraft.identityId,
            expectedGeneration: openedDraft.generation,
            expectedBaselineRevision: openedDraft.baselineRevision,
            expectedObservedRevision: openedDraft.observedRevision,
            changes: [{ fieldRef: "System.Title", proposedValue: "Explicit local value" }],
        };
        const mismatchCases = [
            { expectedItemKey: { ...expected.expectedItemKey, checkoutRoot: "C:\\other\\twig" } },
            { expectedItemKey: { ...expected.expectedItemKey, connectionId: "other-connection" } },
            { expectedItemKey: { ...expected.expectedItemKey, bindingId: "other-binding" } },
            { expectedItemKey: { ...expected.expectedItemKey, workItemId: 43 } },
            { expectedBenchId: "8" },
            { expectedIdentityId: "other-identity" },
            { expectedGeneration: "1" },
            { expectedBaselineRevision: "18" },
            { expectedObservedRevision: "18" },
            { changes: [] },
            { changes: [
                { fieldRef: "System.Title", proposedValue: "first value" },
                { fieldRef: "system.title", proposedValue: "duplicate field" },
            ] },
            { changes: [{ fieldRef: "Custom.Unsupported", proposedValue: "not editable" }] },
        ];
        for (const mismatch of mismatchCases) {
            const refusal = await stageAction.handler({ ...ctx, input: { ...expected, ...mismatch } });
            assert.equal(refusal.status, "refused");
            assert.equal(refusal.committed, false);
            assert.equal(refusal.authorized, false);
            assert.equal(refusal.published, false);
            assert.equal(refusal.canApply, false);
            assert.equal(nativeCalls.filter(call => call.action === "stage").length, 0);
        }
        const stagedDraft = await stageAction.handler({ ...ctx, input: expected });
        assert.equal(stagedDraft.status, "staged");
        assert.equal(stagedDraft.committed, true);
        assert.equal(stagedDraft.effects.localOnly, true);
        assert.deepEqual(stagedDraft.effects.stagedChanges, [
            { fieldRef: "System.Title", proposedValue: "Explicit local value", baselineValue: "Read-only fixture" },
        ]);
        assert.equal(stagedDraft.captureHandle, undefined);
        assert.deepEqual(nativeCalls.map(call => call.action), ["open", "stage"]);
        const stagedRequest = JSON.parse(nativeCalls[1].input);
        assert.deepEqual(stagedRequest.nativeContext, nativeDocument.nativeContext);
        assert.equal(stagedRequest.generation, "9007199254740993");

        await workbench.actions.find(action => action.name === "load_bench").handler(ctx);
        const beforeStaleStage = nativeCalls.filter(call => call.action === "stage").length;
        await assert.rejects(stageAction.handler({ ...ctx, input: expected }));
        assert.equal(nativeCalls.filter(call => call.action === "stage").length, beforeStaleStage,
            "refresh revokes the server-held capture and stale preconditions cannot restage it");

        const originDraft = await openAction.handler({ ...ctx, input: { workItemId: 42 } });
        staleOriginOnStage = true;
        const originRefusal = await stageAction.handler({ ...ctx, input: {
            expectedItemKey: originDraft.itemKey, expectedBenchId: originDraft.benchId,
            expectedIdentityId: originDraft.identityId, expectedGeneration: originDraft.generation,
            expectedBaselineRevision: originDraft.baselineRevision,
            expectedObservedRevision: originDraft.observedRevision,
            changes: [{ fieldRef: "System.Title", proposedValue: "Changed origin value" }],
        } });
        assert.equal(originRefusal.status, "refused");
        assert.equal(originRefusal.committed, false);
        assert.equal(originRefusal.canStage, false);
        assert.equal(originRefusal.published, false);
        staleOriginOnStage = false;

        conflictOnOpen = true;
        const conflictDraft = await openAction.handler({ ...ctx, input: { workItemId: 42 } });
        assert.equal(conflictDraft.status, "opened");
        assert.equal(conflictDraft.hasConflicts, true);
        assert.equal(conflictDraft.canStage, false);
        const beforeConflictStage = nativeCalls.filter(call => call.action === "stage").length;
        const conflictRefusal = await stageAction.handler({ ...ctx, input: {
            expectedItemKey: conflictDraft.itemKey, expectedBenchId: conflictDraft.benchId,
            expectedIdentityId: conflictDraft.identityId, expectedGeneration: conflictDraft.generation,
            expectedBaselineRevision: conflictDraft.baselineRevision,
            expectedObservedRevision: conflictDraft.observedRevision,
            changes: [{ fieldRef: "System.Title", proposedValue: "Conflicting local value" }],
        } });
        assert.equal(conflictRefusal.status, "refused");
        assert.equal(nativeCalls.filter(call => call.action === "stage").length, beforeConflictStage,
            "revision conflicts are refused before invoking the native adapter");
        const html = await (await fetch(opened.url)).text();
        assert.match(html, /Twig workbench/);
        const stagesBeforeReconnect = nativeCalls.filter(call => call.action === "stage").length;
        await workbench.onClose(ctx);
        await workbench.open(ctx);
        await assert.rejects(stageAction.handler({ ...ctx, input: expected }));
        assert.equal(nativeCalls.filter(call => call.action === "stage").length, stagesBeforeReconnect,
            "a replacement provider instance cannot reuse the prior in-memory capture");
        assert.equal(calls.filter(call => call.args).length, 2, "Bench loads only read twice");
        assert.deepEqual(calls.filter(call => call.action === "bench-detail").map(call => call.workItemId), [42, 42, 42],
            "each explicit OPEN re-reads qualified detail");
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
    assert.equal(calls[0].exe, FAKE_TWIG);
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
