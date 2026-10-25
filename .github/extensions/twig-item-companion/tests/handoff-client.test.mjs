import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import test from "node:test";
import { createHandoffClient } from "../handoff-client.mjs";

class Element {
    constructor(tag) { this.tag = tag; }
    children = [];
    textContent = "";
    value = "";
    disabled = false;
    listeners = {};
    append(...nodes) { this.children.push(...nodes); }
    addEventListener(name, handler) { this.listeners[name] = handler; }
    setAttribute(name, value) { this[name] = value; }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const flatten = node => descendants(node).map(child => child.textContent).join("\n");
const detail = { itemKey: { checkoutRoot: "root", connectionId: "connection", bindingId: "binding", workItemId: 42 },
    benchId: "7", identityId: "identity" };
async function settle() { await setImmediate(); await setImmediate(); }
function harness(handler = body => ({ handoff: body.action === "prepare" ? { handle: "handle", brief: "Suggested task" } :
    { outcome: { status: "admitted", messageId: "message" } } })) {
    const calls = [];
    const text = (tag, value) => Object.assign(new Element(tag), { textContent: value });
    const client = createHandoffClient({ document: { createElement: tag => new Element(tag) }, text,
        request: async (route, options) => { const body = options?.body ? JSON.parse(options.body) : undefined;
            calls.push({ route, body }); return handler(body, route); } });
    let root;
    const mount = (value = detail) => { root = new Element("section"); client.mount(root, value); return root; };
    const button = label => descendants(root).find(node => node.tag === "button" && node.textContent === label);
    const input = () => descendants(root).find(node => node.tag === "textarea");
    const type = value => { input().value = value; input().listeners.input(); };
    const click = async label => { button(label).listeners.click(); await settle(); };
    return { client, calls, mount, button, input, type, click, get root() { return root; } };
}
test("render/prepare/input are inert; only explicit Start sends handle and edited brief", async () => {
    const h = harness();
    h.mount();
    assert.equal(h.calls.length, 0);
    assert.equal(h.button("Start in this session").disabled, true);
    await h.click("Prepare handoff brief");
    assert.equal(h.input().value, "Suggested task");
    h.type("<script>inert user task</script>");
    assert.equal(h.calls.length, 1);
    await h.click("Start in this session");
    assert.deepEqual(h.calls[1], { route: "/api/handoff",
        body: { action: "start", handle: "handle", brief: "<script>inert user task</script>" } });
    assert.match(flatten(h.root), /admitted.*not yet complete/);
    assert.equal(h.button("Start in this session").disabled, true);
    await h.click("Start in this session");
    assert.equal(h.calls.length, 2);
});
test("brief survives navigation under exact item, Bench and identity, not another target", async () => {
    const h = harness();
    h.mount();
    h.type("my retained brief");
    h.client.leave();
    h.mount({ ...detail, identityId: "another" });
    assert.equal(h.input().value, "");
    h.mount(structuredClone(detail));
    assert.equal(h.input().value, "my retained brief");
    await h.click("Prepare handoff brief");
    assert.equal(h.input().value, "my retained brief");
});
test("lost Start acknowledgement is sticky across leaving and returning, preserving newer edits", async () => {
    const h = harness(body => {
        if (body.action === "start") throw new Error("connection lost");
        return { handoff: { handle: "handle", brief: "task" } };
    });
    h.mount();
    await h.click("Prepare handoff brief");
    await h.click("Start in this session");
    h.type("newer unsent brief");
    assert.match(flatten(h.root), /inspect this conversation/);
    h.client.leave();
    h.mount();
    assert.equal(h.input().value, "newer unsent brief");
    assert.equal(h.button("Prepare handoff brief").disabled, true);
    assert.equal(h.button("Start in this session").disabled, true);
});
test("late old-target result never renders under another item and is retained when returning", async () => {
    let resolve;
    const pending = new Promise(yes => { resolve = yes; });
    const h = harness(body => body.action === "start" ? pending : { handoff: { handle: "h", brief: "task" } });
    h.mount();
    await h.click("Prepare handoff brief");
    await h.click("Start in this session");
    h.type("newer text while pending");
    h.client.leave();
    const other = h.mount({ ...detail, benchId: "another" });
    resolve({ handoff: { outcome: { status: "admitted", messageId: "<b>late-message</b>" } } });
    await settle();
    assert.doesNotMatch(flatten(other), /late-message/);
    h.mount();
    assert.equal(h.input().value, "newer text while pending");
    assert.match(flatten(h.root), /<b>late-message<\/b>/);
});
test("late prepare cannot arm a new target or overwrite edits typed during preparation", async () => {
    let resolve;
    const pending = new Promise(yes => { resolve = yes; });
    const h = harness(() => pending);
    h.mount();
    await h.click("Prepare handoff brief");
    h.type("typed while waiting");
    resolve({ handoff: { handle: "h", brief: "default" } });
    await settle();
    assert.equal(h.input().value, "typed while waiting");
    h.client.leave();
    h.mount({ ...detail, benchId: "another" });
    assert.equal(h.button("Start in this session").disabled, true);
});
test("known pre-send refusal allows explicit preparation; empty input never Starts", async () => {
    const h = harness(body => ({ handoff: body.action === "prepare" ? { handle: "h", brief: "task" } :
        { outcome: { status: "refused", error: "wrong checkout" } } }));
    h.mount();
    await h.click("Prepare handoff brief");
    h.type(" ");
    await h.click("Start in this session");
    assert.equal(h.calls.length, 1);
    h.type("task");
    await h.click("Start in this session");
    assert.match(flatten(h.root), /refused before sending/);
    assert.equal(h.button("Prepare handoff brief").disabled, false);
});

test("explicit status reconciles only exact target and session without sending, retrying or changing brief", async () => {
    const target = { expectedKey: detail.itemKey, workItemId: 42, bench: "7", identityId: "identity" };
    const outcome = { target, sessionId: "session", status: "admitted", messageId: "<b>late</b>" };
    const h = harness((body, route) => {
        if (route === "/api/state") return { handoffSessionId: "session", handoffOutcomes: [outcome] };
        if (body.action === "start") throw new Error("HTTP lost");
        return { handoff: { handle: "h", brief: "task" } };
    });
    h.mount();
    await h.click("Prepare handoff brief");
    await h.click("Start in this session");
    h.type("newer brief");
    await h.click("Check handoff status");
    assert.equal(h.calls.at(-1).route, "/api/state");
    assert.equal(h.calls.at(-1).body, undefined);
    assert.equal(h.input().value, "newer brief");
    assert.match(flatten(h.root), /<b>late<\/b>/);
    assert.equal(h.button("Start in this session").disabled, true);
    assert.equal(h.button("Prepare handoff brief").disabled, true);
    assert.equal(h.calls.filter(call => call.body?.action === "start").length, 1);
});

test("status rejects every qualified target/session mismatch and absent evidence never re-arms", async () => {
    const target = { expectedKey: detail.itemKey, workItemId: 42, bench: "7", identityId: "identity" };
    const original = { target, sessionId: "session", status: "admitted", messageId: "WRONG-RECEIPT" };
    const variants = [
        { ...original, sessionId: "other" },
        ...["workItemId", "bench", "identityId"].map(field => ({ ...original, target: { ...target, [field]: "other" } })),
        ...Object.keys(detail.itemKey).map(field => ({ ...original,
            target: { ...target, expectedKey: { ...detail.itemKey, [field]: "other" } } })),
    ];
    for (const evidence of variants) {
        const h = harness((body, route) => route === "/api/state" ?
            { handoffSessionId: "session", handoffOutcomes: [evidence] } :
            body.action === "prepare" ? { handoff: { handle: "h", brief: "task" } } :
                Promise.reject(new Error("lost")));
        h.mount();
        await h.click("Prepare handoff brief");
        await h.click("Start in this session");
        await h.click("Check handoff status");
        assert.doesNotMatch(flatten(h.root), /WRONG-RECEIPT/);
        assert.match(flatten(h.root), /No matching handoff evidence/);
        assert.equal(h.button("Start in this session").disabled, true);
        assert.equal(h.button("Prepare handoff brief").disabled, true);
    }
});

test("status result for a departed selection never renders under another target", async () => {
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    const h = harness(() => pending);
    h.mount();
    await h.click("Check handoff status");
    h.client.leave();
    const other = h.mount({ ...detail, benchId: "another" });
    release({ handoffSessionId: "session", handoffOutcomes: [{ target: {
        expectedKey: detail.itemKey, workItemId: 42, bench: "7", identityId: "identity",
    }, sessionId: "session", status: "admitted", messageId: "old-receipt" }] });
    await settle();
    assert.doesNotMatch(flatten(other), /old-receipt/);
});
