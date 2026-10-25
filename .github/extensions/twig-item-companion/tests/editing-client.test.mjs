import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import test from "node:test";
import { createEditingClient } from "../editing-client.mjs";

class Element {
    constructor(tag) { this.tag = tag; }
    children = [];
    textContent = "";
    value = "";
    disabled = false;
    hidden = false;
    checked = false;
    listeners = {};
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = [...nodes]; }
    addEventListener(name, handler) { this.listeners[name] = handler; }
    setAttribute(name, value) { this[name] = value; }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const flattened = node => descendants(node).map(child => child.textContent).join("\n");
const button = (root, label) => descendants(root).find(node => node.tag === "button" && node.textContent === label);
const input = root => descendants(root).find(node => node.tag === "textarea");
const detail = (override = {}) => ({
    itemKey: { checkoutRoot: "fixture", connectionId: "connection", bindingId: "binding", workItemId: 42 },
    benchId: "7", identityId: "identity", ...override,
});
function editing(status = "opened", revision = "17") {
    return { status, canStage: true, canRetain: status === "staged", captureHandle: status + "-handle", observedRevision: revision,
        editors: [{ fieldRef: "System.Title", label: "Title", value: "Native title" }], changes: [] };
}
async function settle() { await setImmediate(); await setImmediate(); }
function harness(handler = () => ({ editing: editing() })) {
    const calls = [];
    const document = { createElement: tag => new Element(tag) };
    const text = (tag, value) => Object.assign(new Element(tag), { textContent: value });
    const client = createEditingClient({ document, text, request: async (route, options) => {
        const body = JSON.parse(options.body);
        calls.push(body);
        return handler(body, route);
    } });
    const root = new Element("section");
    const mount = target => {
        root.replaceChildren();
        client.mount(root, target);
    };
    const type = value => {
        input(root).value = value;
        input(root).listeners.input();
    };
    const click = async label => { button(root, label).listeners.click(); await settle(); };
    return { client, root, calls, mount, type, click };
}

test("mount/navigation never OPEN/STAGE; explicit inputs use text and send only handle plus nullable intent", async () => {
    const h = harness(body => ({ editing: editing(body.action === "stage" ? "staged" : "opened") }));
    h.mount(detail());
    assert.equal(h.calls.length, 0);
    await h.click("Open native draft");
    h.type("<script>inert</script>");
    await h.click("Stage local changes");
    assert.deepEqual(h.calls[1], { action: "stage", captureHandle: "opened-handle",
        changes: [{ fieldRef: "System.Title", proposedValue: "<script>inert</script>" }] });
    assert.equal(button(h.root, "Stage local changes").disabled, true);
    await h.client.leave();
    assert.deepEqual(h.calls.map(call => call.action), ["open", "stage", "invalidate"]);
});

test("OPEN failure and unknown STAGE retain unsent input and disable stage until explicit reopen", async () => {
    let failOpen = false;
    const h = harness(body => {
        if (body.action === "stage" || (body.action === "open" && failOpen)) throw new Error("Unknown native envelope");
        return { editing: editing() };
    });
    h.mount(detail());
    await h.click("Open native draft");
    h.type("unsent");
    await h.click("Stage local changes");
    assert.match(flattened(h.root), /unsent/);
    assert.equal(button(h.root, "Stage local changes").disabled, true);
    const count = h.calls.length;
    await h.click("Stage local changes");
    assert.equal(h.calls.length, count);
    failOpen = true;
    await h.click("Open native draft");
    assert.match(flattened(h.root), /unsent/);
    assert.match(flattened(h.root), /Unknown native envelope/);
});

test("null is an explicit separate choice; empty text remains empty and missing values are not synthesized", async () => {
    const h = harness(body => ({ editing: editing(body.action === "stage" ? "staged" : "opened") }));
    h.mount(detail());
    await h.click("Open native draft");
    h.type("");
    await h.click("Stage local changes");
    assert.equal(h.calls[1].changes[0].proposedValue, "");
    const clear = descendants(h.root).find(node => node.tag === "input" && node.type === "checkbox");
    clear.checked = true;
    clear.listeners.change();
    await h.click("Stage local changes");
    assert.equal(h.calls[2].changes[0].proposedValue, null);
});

test("same visible ID with different qualified key, Bench or identity neither receives input nor stale OPEN result", async () => {
    for (const other of [
        detail({ itemKey: { ...detail().itemKey, bindingId: "another-binding" } }),
        detail({ benchId: "8" }), detail({ identityId: "another-identity" }),
    ]) {
        let release;
        let pending = false;
        const h = harness(body => {
            if (pending && body.action === "open") return new Promise(resolve => { release = resolve; });
            return { editing: editing() };
        });
        h.mount(detail());
        await h.click("Open native draft");
        h.type("belongs to first qualified item");
        pending = true;
        button(h.root, "Open native draft").listeners.click();
        await h.client.leave();
        h.mount(other);
        release({ editing: editing("opened", "18") });
        await settle();
        assert.doesNotMatch(flattened(h.root), /belongs to first qualified item/);
        assert.equal(input(h.root), undefined, "stale OPEN must not create another item's inputs");
        pending = false;
        await h.click("Open native draft");
        assert.equal(input(h.root).value, "Native title");
        await h.client.leave();
        h.mount(detail());
        assert.match(flattened(h.root), /belongs to first qualified item/);
    }
});

test("reopened changed revision preserves but marks old intent stale; human discard is required before reconciled entry", async () => {
    let revision = "17";
    const h = harness(() => ({ editing: editing("opened", revision) }));
    h.mount(detail());
    await h.click("Open native draft");
    h.type("old revision intent");
    await h.client.leave();
    h.mount(detail());
    revision = "18";
    await h.click("Open native draft");
    assert.match(flattened(h.root), /different observed revision/);
    assert.match(flattened(h.root), /old revision intent/);
    assert.equal(input(h.root).value, "Native title");
    assert.equal(input(h.root).disabled, true);
    assert.equal(button(h.root, "Stage local changes").disabled, true);
    await h.click("Discard retained stale input");
    h.type("manually reconciled");
    assert.equal(button(h.root, "Stage local changes").disabled, false);
});

test("exact successful acknowledgement clears submitted intent, not newer user edits, regardless of empty effect list", async () => {
    let release;
    const h = harness(body => body.action === "stage"
        ? new Promise(resolve => { release = resolve; }) : { editing: editing() });
    h.mount(detail());
    await h.click("Open native draft");
    h.type("submitted");
    button(h.root, "Stage local changes").listeners.click();
    h.type("newer unsent");
    release({ editing: editing("staged") }); // Native baseline-equal effects may be absent.
    await settle();
    assert.equal(input(h.root).value, "newer unsent");
    assert.match(flattened(h.root), /newer unsent/);
    assert.equal(button(h.root, "Stage local changes").disabled, false);
});

function captureOutcome(status = "retained") {
    return { status, committed: true, canStage: false, canRetain: false,
        inspectFirst: status === "indeterminate", receipt: {
            digest: "a".repeat(64), operationId: "draft-" + "b".repeat(64),
            effects: [
                { fieldRef: "Custom.Absent", proposedValue: null },
                { fieldRef: "Custom.Empty", baselineValue: null, proposedValue: "" },
                { fieldRef: "Custom.Inert", baselineValue: "", proposedValue: "<script>inert</script>" },
            ], originCoverage: "editing-context-and-native-origin-digest", authorized: false, canApply: false,
        } };
}
test("RETAIN requires explicit click after staging with no unsent input and sends only capture handle", async () => {
    const h = harness(body => ({ editing: body.action === "retain" ? captureOutcome() :
        editing(body.action === "stage" ? "staged" : "opened") }));
    h.mount(detail());
    assert.equal(button(h.root, "Retain staged draft").disabled, true);
    assert.equal(h.calls.length, 0);
    await h.click("Open native draft");
    await h.click("Retain staged draft");
    assert.equal(h.calls.length, 1);
    h.type("staged value");
    await h.click("Stage local changes");
    assert.equal(button(h.root, "Retain staged draft").disabled, false);
    h.type("unsent");
    await h.click("Retain staged draft");
    assert.equal(h.calls.length, 2);
    assert.match(flattened(h.root), /Stage unsent input first/);
    await h.click("Stage local changes");
    await h.click("Retain staged draft");
    assert.deepEqual(h.calls[3], { action: "retain", captureHandle: "staged-handle" });
    assert.equal(button(h.root, "Retain staged draft").disabled, true);
    assert.equal(button(h.root, "Stage local changes").disabled, true);
    assert.match(flattened(h.root), /Captured locally, not authorized or applied/);
    assert.match(flattened(h.root), /Baseline: \(absent \/ unknown\)/);
    assert.match(flattened(h.root), /Baseline: \(null\)/);
    assert.match(flattened(h.root), /Proposed: \(empty text ""\)/);
    assert.match(flattened(h.root), /<script>inert<\/script>/);
    assert.equal(descendants(h.root).some(node => node.tag === "script"), false);
});

test("pending RETAIN preserves newer unsent input on success and never auto-stages it", async () => {
    let release;
    const h = harness(body => body.action === "retain" ? new Promise(resolve => { release = resolve; }) :
        { editing: editing(body.action === "stage" ? "staged" : "opened") });
    h.mount(detail());
    await h.click("Open native draft");
    h.type("staged");
    await h.click("Stage local changes");
    button(h.root, "Retain staged draft").listeners.click();
    h.type("newer unsent");
    assert.equal(button(h.root, "Open native draft").disabled, true);
    release({ editing: captureOutcome() });
    await settle();
    assert.equal(input(h.root).value, "newer unsent");
    assert.match(flattened(h.root), /newer unsent/);
    assert.deepEqual(h.calls.map(call => call.action), ["open", "stage", "retain"]);
});

test("RETAIN unknown and committed-indeterminate impose sticky inspect-first hold across navigation", async () => {
    for (const failure of ["unknown", "indeterminate"]) {
        const h = harness(body => {
            if (body.action === "retain") {
                if (failure === "unknown") throw new Error("No native stdout");
                return { editing: captureOutcome("indeterminate") };
            }
            return { editing: editing(body.action === "stage" ? "staged" : "opened") };
        });
        h.mount(detail());
        await h.click("Open native draft");
        h.type("staged");
        await h.click("Stage local changes");
        await h.click("Retain staged draft");
        const count = h.calls.length;
        assert.match(flattened(h.root), /Inspect the local proposal journal/);
        await h.click("Open native draft");
        assert.equal(h.calls.length, count);
        await h.client.leave();
        h.mount(detail());
        assert.equal(button(h.root, "Open native draft").disabled, true);
        await h.click("Open native draft");
        assert.equal(h.calls.length, count + 1, "only navigation invalidate was sent");
        assert.match(flattened(h.root), /Inspect the local proposal journal/);
    }
});

test("late RETAIN cannot render old-target receipt and retains unresolved hold for the original qualified identity", async () => {
    let release;
    const h = harness(body => body.action === "retain" ? new Promise(resolve => { release = resolve; }) :
        { editing: editing(body.action === "stage" ? "staged" : "opened") });
    h.mount(detail());
    await h.click("Open native draft");
    h.type("staged");
    await h.click("Stage local changes");
    button(h.root, "Retain staged draft").listeners.click();
    h.type("belongs to old identity");
    await h.client.leave();
    h.mount(detail({ identityId: "another-identity" }));
    release({ editing: captureOutcome("indeterminate") });
    await settle();
    assert.doesNotMatch(flattened(h.root), /Native capture receipt|belongs to old identity/);
    assert.equal(button(h.root, "Open native draft").disabled, false);
    await h.client.leave();
    h.mount(detail());
    assert.equal(button(h.root, "Open native draft").disabled, true);
    assert.match(flattened(h.root), /belongs to old identity/);
    assert.match(flattened(h.root), /Inspect the local proposal journal/);
});

test("known RETAIN refusal consumes handle but permits explicit reopen without clearing unsent input", async () => {
    const h = harness(body => ({ editing: body.action === "retain"
        ? { status: "refused", committed: false, canStage: false, canRetain: false,
            error: { message: "Native draft is empty" } }
        : editing(body.action === "stage" ? "staged" : "opened") }));
    h.mount(detail());
    await h.click("Open native draft");
    h.type("staged");
    await h.click("Stage local changes");
    await h.click("Retain staged draft");
    assert.equal(button(h.root, "Retain staged draft").disabled, true);
    assert.equal(button(h.root, "Open native draft").disabled, false);
    h.type("unsent after refusal");
    await h.click("Open native draft");
    assert.equal(h.calls.at(-1).action, "open");
    assert.equal(button(h.root, "Retain staged draft").disabled, true);
    assert.equal(input(h.root).value, "unsent after refusal");
});
