import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import test from "node:test";
import { createDiscussionClient } from "../discussion-client.mjs";
import { discussionPage } from "./discussion-fixture.mjs";
class Element {
    constructor(tag) { this.tag = tag; }
    children = [];
    textContent = "";
    listeners = {};
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    setAttribute(name, value) { this[name] = value; }
    addEventListener(name, handler) { this.listeners[name] = handler; }
    set innerHTML(value) { assert.fail("Discussion must never render HTML: " + value); }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const flatten = node => descendants(node).map(child => child.textContent).join("\n");
async function settle() { await setImmediate(); await setImmediate(); }
const targetHandle = "a".repeat(43);
function response(more = true) {
    const value = discussionPage(undefined, more);
    return { discussion: { status: "read", page: { comments: value.comments, count: "1",
        reportedTotalCount: null, traversal: more ? "more" : "exhausted", scope: "provider-page",
        snapshotConsistency: "not-guaranteed" }, pageHandle: more ? "b".repeat(43) : null } };
}
function harness(handler = () => response()) {
    const calls = [];
    const client = createDiscussionClient({ document: { createElement: tag => new Element(tag) },
        text: (tag, textContent) => Object.assign(new Element(tag), { textContent }),
        request: async (route, options) => {
            const body = JSON.parse(options.body); calls.push({ route, body });
            return handler(body);
        } });
    let root;
    const mount = (handle = targetHandle) => { root = new Element("section"); client.mount(root, handle); return root; };
    const button = label => descendants(root).find(node => node.tag === "button" && node.textContent === label);
    const click = async label => { button(label).listeners.click(); await settle(); };
    return { client, calls, mount, button, click, get root() { return root; } };
}
test("discussion rendering is inert until explicit online Read and sends only target handle", async () => {
    const h = harness(); h.mount();
    assert.equal(h.calls.length, 0);
    assert.equal(h.button("Next discussion page").disabled, true);
    await h.click("Read discussion (online)");
    assert.deepEqual(h.calls, [{ route: "/api/discussion", body: { action: "read", targetHandle } }]);
    assert.match(flatten(h.root), /<script>evil\(\)<\/script>/);
    assert.match(flatten(h.root), /format \(inert\): html/);
    assert.match(flatten(h.root), /deleted: true/);
    assert.match(flatten(h.root), /not guaranteed/);
    assert.doesNotMatch(flatten(h.root), /private\+token/);
});
test("Next replaces rather than accumulates pages, supports empty-more and exhausted, and never walks automatically", async () => {
    const h = harness(body => {
        const value = response(body.action === "read");
        if (body.action === "next") { value.discussion.page.comments = []; value.discussion.page.count = "0"; }
        return value;
    });
    h.mount(); await h.click("Read discussion (online)");
    assert.equal(h.calls.length, 1);
    await h.click("Next discussion page");
    assert.deepEqual(h.calls[1].body, { action: "next", targetHandle, pageHandle: "b".repeat(43) });
    assert.doesNotMatch(flatten(h.root), /Comment 9007199254740993/);
    assert.match(flatten(h.root), /End of this traversal, not a complete snapshot/);
    assert.match(flatten(h.root), /contains no entries/);
    assert.equal(h.button("Next discussion page").disabled, true);
});
test("null body, empty body and literal null stay distinct without invented metadata", async () => {
    const value = response(false);
    value.discussion.page.comments = [null, "", "null"].map((body, index) => ({
        ...value.discussion.page.comments[0], id: String(index + 1), body, isDeleted: null, format: null,
    }));
    value.discussion.page.count = "3";
    const h = harness(() => value); h.mount(); await h.click("Read discussion (online)");
    assert.deepEqual(descendants(h.root).filter(node => node.tag === "pre").map(node => node.textContent),
        ["(unavailable)", "(empty)", "null"]);
});
test("failed next page retains labeled previous observation but drops Next and never retries automatically", async () => {
    const h = harness(body => { if (body.action === "next") throw new Error("SECRET"); return response(); });
    h.mount(); await h.click("Read discussion (online)"); await h.click("Next discussion page");
    assert.match(flatten(h.root), /previous observation/);
    assert.match(flatten(h.root), /Comment 9007199254740993/);
    assert.doesNotMatch(flatten(h.root), /SECRET/);
    assert.equal(h.button("Next discussion page").disabled, true);
    assert.equal(h.calls.length, 2);
});
test("authority-invalidated clears previous data/cursor and requires detail reload, not another read", async () => {
    const h = harness(body => body.action === "next" ? { discussion: { status: "refused", code: "authority-invalidated" } } : response());
    h.mount(); await h.click("Read discussion (online)"); await h.click("Next discussion page");
    assert.doesNotMatch(flatten(h.root), /Comment 9007199254740993/);
    assert.match(flatten(h.root), /authority-invalidated/);
    assert.equal(h.button("Read discussion (online)").disabled, true);
    await h.click("Read discussion (online)");
    assert.equal(h.calls.length, 2);
});
test("late same-ID revisit cannot render or retain an old page handle, and leave carries old target only", async () => {
    let resolve;
    const pending = new Promise(yes => { resolve = yes; });
    const h = harness(body => body.action === "invalidate" ? {} : pending);
    h.mount();
    await h.click("Read discussion (online)");
    await h.client.leave();
    const current = h.mount("c".repeat(43));
    resolve(response()); await settle();
    assert.doesNotMatch(flatten(current), /Comment 9007199254740993/);
    assert.equal(h.button("Next discussion page").disabled, true);
    assert.deepEqual(h.calls[1].body, { action: "invalidate", targetHandle });
});
test("double clicks coalesce while a read is pending and no-target seeds show explicit unavailability", async () => {
    let resolve;
    const h = harness(() => new Promise(yes => { resolve = yes; }));
    h.mount(); await h.click("Read discussion (online)"); await h.click("Read discussion (online)");
    assert.equal(h.calls.length, 1);
    resolve(response(false)); await settle();
    h.mount(null);
    assert.match(flatten(h.root), /published cached member/);
});
test("extension output cap is visible while arbitrary transport diagnostics remain private", async () => {
    const h = harness(() => { throw new Error("Discussion exceeds the extension 4MiB output limit; no page was accepted."); });
    h.mount(); await h.click("Read discussion (online)");
    assert.match(flatten(h.root), /extension 4MiB output limit/);
});
