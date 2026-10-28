import assert from "node:assert/strict";
import { readFile, realpath, stat } from "node:fs/promises";
import path from "node:path";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { createNativeProposalReader } from "../native-proposal.mjs";
import { createReviewServer } from "../review-server.mjs";
import { reviewClient } from "../review-page.mjs";
import { renderPage } from "../render.mjs";
import { mountRetainedReview } from "../retained-review.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";

const workspacePath = process.cwd();
const digest = "a".repeat(64);
const workspace = { organization: "fictional-org", project: "fictional-project" };
const input = () => ({ file: "proposal.json", digest, workspace: { ...workspace } });
function fixture() {
    return {
        digest, canApply: true, issues: [], pendingChanges: [],
        operations: [{ ordinal: 0, id: "op-0", kind: "Batch" }],
        reviewModel: {
            model: "twig.change-proposal.review", modelVersion: 1, digest, workspace: { ...workspace },
            rationale: "Native fixture, not authenticated host evidence", affectedItems: [], contextItems: [],
            blockers: [{ kind: "unknown", workItemId: 42, detail: "Coverage unknown" }],
            operations: [{
                ordinal: 0, opId: "op-0", kind: "batch", summary: "Exact field changes",
                target: { workItemId: 42, stagedIdentity: null },
                preconditions: [{ kind: "expectedRevision", value: "3" }],
                consequences: [
                    { kind: "field-set", field: "System.Description", to: "<script>untrusted()</script>" + "x".repeat(300),
                        relation: null, otherId: null,
                        before: { state: "unknown", value: null, source: "cache", revision: 3, reason: "revision-mismatch" } },
                    { kind: "field-clear", field: "Custom.Clear", to: null, relation: null, otherId: null },
                ],
            }],
        },
    };
}
function reader(calls, payload = () => JSON.stringify(fixture()), realpath = async (value) => value) {
    return createNativeProposalReader({ executable: fakeExecutable(),
        realpath,
        execFile(exe, args, options, callback) {
            calls.push({ exe, args, options });
            const result = payload();
            if (result instanceof Error) callback(result, "", "native exact refusal");
            else callback(null, result, "");
        },
    });
}
async function open(readNativeProposal) {
    const server = await createReviewServer({ workspacePath, readNativeProposal });
    const html = await (await fetch(server.url)).text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
    const state = async () => (await fetch(new URL("/api/state", server.url), { headers })).json();
    return { server, html, headers, state, token };
}
const deferred = () => {
    let resolve;
    const promise = new Promise((r) => { resolve = r; });
    return { promise, resolve };
};

test("one native preview pins physical file/cwd and expected digest then retains exact frozen evidence", async () => {
    const calls = [];
    const value = await reader(calls)({ input: input(), workspacePath });
    assert.equal(calls.length, 1);
    assert.equal(calls[0].exe, FAKE_TWIG);
    assert.deepEqual(calls[0].args, ["proposal", "preview", "--file", path.join(workspacePath, "proposal.json"),
        "--expect-digest", digest, "-o", "json"]);
    assert.equal(calls[0].options.cwd, workspacePath);
    assert.equal(calls[0].options.shell, false);
    assert.equal(calls[0].options.timeout, 15000);
    assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
    assert.deepEqual(value.retained.envelope, fixture());
    assert.ok(Object.isFrozen(value.retained.envelope.reviewModel.operations[0].consequences));
    assert.equal(value.expected.captureId, value.expected.expectedCaptureId);
    assert.match(value.acquisition.originCoverage, /full native origin is not exported/);
    assert.match(value.acquisition.localEffects, /last-preview ordering/);
    const explicitEmpty = fixture();
    explicitEmpty.reviewModel.providerAffectedItems = [];
    const emptyCalls = [];
    const empty = await reader(emptyCalls, () => JSON.stringify(explicitEmpty))({ input: input(), workspacePath });
    assert.equal(emptyCalls.length, 1);
    assert.deepEqual(empty.retained.envelope, explicitEmpty);
});

test("bad input and physical path escape refuse without launching a child", async () => {
    const calls = [];
    for (const request of [{ ...input(), digest: "A".repeat(64) }, { ...input(), authorize: true },
        { ...input(), workspace: { ...workspace, bindingId: "invented" } }, { ...input(), file: "" }]) {
        await assert.rejects(reader(calls)({ input: request, workspacePath }), /Review requires/);
    }
    await assert.rejects(reader(calls)({ input: { ...input(), file: ".." }, workspacePath }), /physical proposal/);
    const external = path.resolve(workspacePath, "..", "external.json");
    await assert.rejects(reader(calls, undefined, async (v) => v === workspacePath ? v : external)(
        { input: input(), workspacePath }), /physical proposal/);
    assert.equal(calls.length, 0);
});

test("native failure, malformed JSON, unknown model/malformed provider evidence and contradictory identity refuse once without retry", async () => {
    const values = [
        new Error("child exit 1"), '{"digest":', "x".repeat(4 * 1024 * 1024 + 1),
        { ...fixture(), reviewModel: { ...fixture().reviewModel, modelVersion: 3 } },
        { ...fixture(), reviewModel: { ...fixture().reviewModel, modelVersion: 2 } },
        { ...fixture(), reviewModel: { ...fixture().reviewModel, providerAffectedItems: {} } },
        { ...fixture(), digest: "b".repeat(64) },
        { ...fixture(), reviewModel: { ...fixture().reviewModel, workspace: { ...workspace, project: "different" } } },
    ];
    for (const value of values) {
        const calls = [];
        const payload = value instanceof Error || typeof value === "string" ? value : JSON.stringify(value);
        await assert.rejects(reader(calls, () => payload)({ input: input(), workspacePath }), /refusal|unavailable/);
        assert.equal(calls.length, 1);
    }
});

test("cancelled path resolution refuses before any process starts", async () => {
    const calls = [], controller = new AbortController();
    controller.abort();
    await assert.rejects(reader(calls)({ input: input(), workspacePath, signal: controller.signal }), /abort/i);
    assert.equal(calls.length, 0);
});

test("empty open/state/module are inert; explicit acquisition persists same observation through iframe reload", async () => {
    const calls = [], { server, headers, state, token } = await open(reader(calls));
    try {
        assert.equal((await state()).observation, null);
        assert.equal(calls.length, 0);
        const response = await fetch(new URL("/api/acquire", server.url), { method: "POST", headers, body: JSON.stringify(input()) });
        assert.equal(response.status, 200);
        const first = await response.json();
        assert.equal(first.busy, false);
        await fetch(server.url);
        const restored = await state();
        assert.deepEqual(restored.observation, first.observation);
        assert.equal(calls.length, 1);
        const module = await fetch(new URL(`/review-module.mjs?token=${token}`, server.url));
        assert.equal(module.status, 200);
        assert.match(await module.text(), /export function mountRetainedReview/);
        assert.equal(calls.length, 1);
    } finally { await server.close(); }
});

test("protected state/module, cross-origin actions and malformed apply requests never preview", async () => {
    const calls = [], { server, headers } = await open(reader(calls));
    try {
        assert.equal((await fetch(new URL("/api/state", server.url))).status, 403);
        assert.equal((await fetch(new URL("/review-module.mjs?token=wrong", server.url))).status, 403);
        assert.equal((await fetch(new URL("/api/acquire", server.url), {
            method: "POST", headers: { ...headers, Origin: "https://untrusted.invalid" }, body: JSON.stringify(input()),
        })).status, 403);
        assert.equal((await fetch(new URL("/api/apply", server.url), { method: "POST", headers, body: "{}" })).status, 400);
        assert.equal(calls.length, 0);
    } finally { await server.close(); }
});

test("a failed or invalid replacement clears prior success and keeps the actual refusal", async () => {
    let fail = false;
    const calls = [], { server, state } = await open(reader(calls, () => fail ? '{"digest":' : JSON.stringify(fixture())));
    try {
        await server.acquire(input());
        fail = true;
        await assert.rejects(server.acquire(input()), /truncated/);
        assert.equal((await state()).observation, null);
        assert.match((await state()).error, /truncated/);
        await assert.rejects(server.acquire({ ...input(), digest: "bad" }), /Review requires/);
        assert.equal((await state()).observation, null);
        assert.match((await state()).error, /Review requires/);
        assert.equal(calls.length, 2);
    } finally { await server.close(); }
});

test("single-flight refuses overlap and close aborts owned work and suppresses late publication", async () => {
    const pending = deferred();
    let signal, calls = 0;
    const { server } = await open(async (request) => { calls++; signal = request.signal; return pending.promise; });
    const first = server.acquire(input());
    await assert.rejects(server.acquire(input()), /already in progress/);
    assert.equal(calls, 1);
    await server.close();
    assert.equal(signal.aborted, true);
    pending.resolve({});
    await assert.rejects(first, /closed or superseded/);
    await assert.rejects(server.acquire(input()), /closed/);
    await server.close();
});

class Element {
    children = []; textContent = ""; disabled = false; value = ""; listeners = {};
    constructor(tag, ownerDocument) { this.tag = tag; this.ownerDocument = ownerDocument; }
    setAttribute(name, value) { this[name] = value; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(name, handler) { this.listeners[name] = handler; }
    set innerHTML(_) { throw new Error("Untrusted HTML is not permitted."); }
}
const all = (node) => [node, ...node.children.flatMap(all)];
async function client(request, loadModule = async () => ({ mountRetainedReview })) {
    const nodes = new Map();
    const document = { createElement: (tag) => new Element(tag, document), querySelector(selector) {
        if (!nodes.has(selector)) nodes.set(selector, new Element("div", document));
        return nodes.get(selector);
    } };
    await reviewClient({ document, request, loadModule });
    for (const selector of ["#proposal-file", "#proposal-digest", "#proposal-org", "#proposal-project"]) {
        document.querySelector(selector);
    }
    return { nodes, document };
}
const text = (root) => all(root).map((node) => node.textContent).join("\n");

test("actual retained renderer shows full effects; Details/Back and reload use state only", async () => {
    const observation = await reader([])({ input: input(), workspacePath });
    const calls = [];
    const { nodes } = await client(async (route) => { calls.push(route); return { observation }; });
    const root = nodes.get("#review-output");
    assert.match(text(root), /Coverage unknown/);
    assert.match(text(root), /revision-mismatch/);
    assert.match(text(root), /field-clear/);
    const details = all(root).find((node) => node.tag === "button" && node.textContent === "Details");
    details.listeners.click();
    assert.ok(text(root).includes(fixture().reviewModel.operations[0].consequences[0].to));
    all(root).find((node) => node.tag === "button" && node.textContent === "Back").listeners.click();
    assert.deepEqual(calls, ["/api/state"]);
    assert.equal(all(root).some((node) => ["script", "input", "a"].includes(node.tag)), false);
    assert.match(nodes.get("#review-status").textContent, /full native origin is not exported/);
    nodes.get("#retained-review").listeners.click();
    await new Promise((resolve) => setImmediate(resolve));
    assert.deepEqual(calls, ["/api/state", "/api/state"]);
});

test("form sends one explicit request with exact input, and failures never auto-retry", async () => {
    const calls = [];
    const { nodes } = await client(async (route, options) => {
        calls.push({ route, options });
        if (options) throw new Error("native mismatch");
        return { observation: null };
    });
    nodes.get("#proposal-file").value = "proposal.json";
    nodes.get("#proposal-digest").value = digest;
    nodes.get("#proposal-org").value = workspace.organization;
    nodes.get("#proposal-project").value = workspace.project;
    nodes.get("#review-form").listeners.submit({ preventDefault() {} });
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(calls.length, 2);
    assert.equal(calls[1].route, "/api/acquire");
    assert.deepEqual(JSON.parse(calls[1].options.body), input());
    assert.match(nodes.get("#review-status").textContent, /native mismatch.*No automatic retry/);
    assert.equal(nodes.get("#review-output").children.length, 0);
});

test("rendered review page shares theme and nonce, has no automatic acquisition or pasted JSON input", () => {
    const html = renderPage({ token: "fictional", nonce: "test", review: true });
    assert.match(html, /Twig proposal review/);
    assert.match(html, /theme-mode/);
    assert.match(html, /local proposal journal/);
    assert.match(html, /<script nonce="test">/);
    assert.doesNotMatch(html, /node:|<textarea|setInterval\(|unsafe-eval|unsafe-inline/);
});

test("supported SDK declares a third canvas with acquisition only; human Apply stays in the review page", async () => {
    const source = await readFile(new URL("../extension.mjs", import.meta.url), "utf8");
    let declarations, options, creations = 0;
    const calls = [], admitted = [];
    class CanvasError extends Error {
        constructor(code, message) { super(message); this.code = code; }
    }
    const context = createContext({
        CanvasError, realpath, stat, path, process, createCanvas: (value) => value,
        joinSession: async ({ canvases }) => { declarations = canvases; return { workspacePath }; },
        createNativeDetailReader() {}, createNativeBenchReader() {}, createNativeBenchDetailReader() {},
        createNativeDraftReader() {}, ExecutableError: class extends Error {},
        admitTwigExecutable: async ({ canvas }) => { admitted.push(canvas); return fakeExecutable(); },
        createNativeProposalReader: ({ executable }) => { assert.equal(executable.path, FAKE_TWIG); return "native-preview-reader"; },
        createNativeProposalApplier: ({ executable }) => { assert.equal(executable.path, FAKE_TWIG); return "native-applier"; },
        createReviewServer: async (value) => {
            options = value; creations++;
            return { url: "http://127.0.0.1:1/", acquire: async (value) => { calls.push(value); return { retained: true }; },
                apply: async (value) => { calls.push(value); return { applied: true }; },
                close: async () => { calls.push("close"); } };
        },
    });
    await new Script(`(async () => { ${source.replace(/^import .*;\r?\n/gm, "")} })()`).runInContext(context);
    const canvas = declarations.find((value) => value.id === "twig-proposal-review");
    assert.ok(canvas);
    assert.deepEqual(Array.from(canvas.actions, (value) => value.name), ["acquire_review"]);
    assert.equal(canvas.actions[0].inputSchema.additionalProperties, false);
    assert.deepEqual(Array.from(canvas.actions[0].inputSchema.required), ["file", "digest", "workspace"]);
    await assert.rejects(canvas.actions[0].handler({ instanceId: "missing", input: input() }),
        { code: "canvas_not_open", message: "Open Twig proposal review before explicit acquisition." });
    const canvasContext = { instanceId: "review", session: { workingDirectory: workspacePath } };
    await canvas.open(canvasContext);
    await canvas.open(canvasContext);
    assert.equal(creations, 1);
    assert.deepEqual(admitted, ["review"], "one admission per opened instance, scoped to the review manifest");
    assert.equal(options.workspacePath, await realpath(workspacePath));
    assert.equal(options.readNativeProposal, "native-preview-reader");
    assert.equal(options.applyNativeProposal, "native-applier");
    assert.deepEqual(calls, []);
    await canvas.actions[0].handler({ ...canvasContext, input: input() });
    assert.deepEqual(calls, [input()]);
    await canvas.onClose({ instanceId: "review" });
    assert.deepEqual(calls, [input(), "close"]);
});

test("Apply form is inert until separate consent and sends retained capture, not edited acquisition inputs", async () => {
    const payload = fixture();
    payload.reviewModel.blockers = [];
    const observation = await reader([], () => JSON.stringify(payload))({ input: input(), workspacePath });
    const calls = [];
    const { nodes } = await client(async (route, options) => {
        calls.push({ route, options });
        return { observation, applyAvailable: true,
            applyAttempt: options ? { status: "verified", native: { marker: "<script>unsafe</script>" } } : null };
    });
    assert.equal(nodes.get("#apply-proposal").disabled, false);
    const submit = () => nodes.get("#apply-form").listeners.submit({ preventDefault() {} });
    submit();
    assert.equal(calls.length, 1, "no consent means no invocation");
    nodes.get("#proposal-file").value = "different.json";
    nodes.get("#proposal-digest").value = "b".repeat(64);
    nodes.get("#apply-consent").checked = true;
    submit();
    submit();
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(calls.length, 2, "rapid repeat cannot submit twice");
    assert.equal(calls[1].route, "/api/apply");
    assert.deepEqual(JSON.parse(calls[1].options.body), {
        captureId: observation.expected.captureId, confirm: digest,
    });
    assert.match(nodes.get("#apply-outcome").textContent, /every declared operation Verified/);
    assert.ok(nodes.get("#apply-outcome").textContent.includes("<script>unsafe</script>"));
    assert.equal(nodes.get("#apply-proposal").disabled, true);
});

test("blocked previews and replaced captures never carry prior consent forward", async () => {
    const first = await reader([])({ input: input(), workspacePath });
    const second = await reader([])({ input: input(), workspacePath });
    let observation = first;
    const { nodes } = await client(async () => ({ observation, applyAvailable: true }));
    assert.equal(nodes.get("#apply-proposal").disabled, true, "model blockers refuse despite canApply=true");
    nodes.get("#apply-consent").checked = true;
    observation = second;
    nodes.get("#retained-review").listeners.click();
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(nodes.get("#apply-consent").checked, false);
});
