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
    return { status, canStage: true, captureHandle: status + "-handle", observedRevision: revision,
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
