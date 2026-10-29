import assert from "node:assert/strict";
import test from "node:test";
import { createAttachmentClient } from "../attachment-client.mjs";

class Element {
    constructor(tag) { this.tag = tag; }
    children = [];
    listeners = {};
    attributes = {};
    classList = { add() {} };
    textContent = "";
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(name, handler) { this.listeners[name] = handler; }
    setAttribute(name, value) { this.attributes[name] = value; }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const settle = () => new Promise(resolve => setImmediate(resolve));
function harness({ outcomes, wait = () => new Promise(() => {}) } = {}) {
    const calls = [];
    const navigator = { userActivation: { isActive: false } };
    const client = createAttachmentClient({
        document: { createElement: tag => new Element(tag) },
        text: (tag, textContent) => Object.assign(new Element(tag), { textContent }),
        navigator,
        wait,
        request: async (route, options) => {
            const body = JSON.parse(options.body);
            calls.push({ route, body });
            if (body.action === "download-status") return outcomes.shift();
            return body.action === "inventory" ? { status: "inventory", attachments: [{
                id: "4dab0171-49dc-4da5-8f18-35c923fbdb0a", fileName: "report.pdf",
                reportedSize: "5", downloadHandle: "b".repeat(43),
            }] } : { invalidated: true };
        },
    });

    const root = new Element("section");
    client.mount(root, "a".repeat(43));
    const button = name => descendants(root).find(item => item.tag === "button" && item.textContent === name);
    return { client, calls, navigator, root, button };
}

test("accepted download click never claims receipt and reports server observation without retrying download", async () => {
    const h = harness({ outcomes: [{ status: "not-requested" }, { status: "reading" }, { status: "sent" }],
        wait: async () => {} });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    const link = descendants(h.root).find(item => item.tag === "a");
    link.listeners.click({ isTrusted: true });
    assert.match(descendants(h.root).map(item => item.textContent).join("\n"), /saving is not confirmed/);
    await settle();
    assert.match(descendants(h.root).map(item => item.textContent).join("\n"), /finished sending.*Saving is not confirmed/);
    assert.equal(h.calls.filter(call => call.body.action === "download-status").length, 3);
    let prevented = false;
    link.listeners.click({ isTrusted: true, preventDefault: () => { prevented = true; } });
    assert.equal(prevented, true);
});

test("download refusal is shown instead of a success-shaped click message", async () => {
    const h = harness({ outcomes: [{ status: "refused", message: "Attachment download refused (attachment-stale)." }],
        wait: async () => {} });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    descendants(h.root).find(item => item.tag === "a").listeners.click({ isTrusted: true });
    await settle();
    assert.match(descendants(h.root).map(item => item.textContent).join("\n"), /attachment-stale/);
    assert.equal(h.calls.filter(call => call.body.action === "download-status").length, 1);
});

test("host-blocked navigation produces bounded observation only", async () => {
    const h = harness({ outcomes: Array.from({ length: 40 }, () => ({ status: "not-requested" })),
        wait: async () => {} });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    descendants(h.root).find(item => item.tag === "a").listeners.click({ isTrusted: true });
    await settle();
    assert.match(descendants(h.root).map(item => item.textContent).join("\n"), /no download request has reached Twig.*Observation ended/);
    assert.equal(h.calls.length, 41);
});

test("leaving or refreshing inventory cancels old observations before another request", async () => {
    for (const action of ["leave", "refresh"]) {
        let resume;
        const h = harness({ wait: () => new Promise(resolve => { resume = resolve; }) });
        h.navigator.userActivation.isActive = true;
        await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
        descendants(h.root).find(item => item.tag === "a").listeners.click({ isTrusted: true });
        if (action === "leave") await h.client.leave();
        else await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
        resume();
        await settle();
        assert.equal(h.calls.filter(call => call.body.action === "download-status").length, 0);
    }
});

test("inventory is not automatic and refuses synthetic or inactive activation", async () => {
    const h = harness();
    assert.equal(h.calls.length, 0);
    const handler = h.button("Show attachments (online)").listeners.click;
    await handler({ isTrusted: false });
    assert.equal(h.calls.length, 0);
    assert.match(descendants(h.root).map(item => item.textContent).join("\n"), /synthetic activation was refused/);
    await handler({ isTrusted: true });
    assert.equal(h.calls.length, 0);
});

test("one trusted inventory click produces browser links; only a trusted active download click is allowed", async () => {
    const h = harness();
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    await settle();
    assert.deepEqual(h.calls[0], { route: "/api/attachments", body: { action: "inventory", targetHandle: "a".repeat(43) } });
    const link = descendants(h.root).find(item => item.tag === "a");
    assert.equal(link.href, "/api/attachment-download/" + "b".repeat(43));
    assert.equal(link.download, "report.pdf");
    let prevented = false;
    link.listeners.click({ isTrusted: false, preventDefault: () => { prevented = true; } });
    assert.equal(prevented, true);
    prevented = false;
    link.listeners.click({ isTrusted: true, preventDefault: () => { prevented = true; } });
    assert.equal(prevented, false);
    assert.equal(h.calls.length, 1, "the native download is a browser navigation, not an SDK/API action");
});

test("leave invalidates the target handle and removes rendered links", async () => {
    const h = harness();
    await h.client.leave();
    assert.equal(h.calls.length, 1);
    assert.deepEqual(h.calls[0], { route: "/api/attachments", body: { action: "invalidate", targetHandle: "a".repeat(43) } });
    assert.equal(descendants(h.root).some(item => item.tag === "a"), false);
});
