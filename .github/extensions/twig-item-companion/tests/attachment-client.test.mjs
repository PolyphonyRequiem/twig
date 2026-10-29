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
    removeAttribute(name) { delete this.attributes[name]; delete this[name]; }
}
const descendants = node => typeof node === "string" ? [] : [node, ...node.children.flatMap(descendants)];
const settle = () => new Promise(resolve => setImmediate(resolve));
const pdf = { id: "4dab0171-49dc-4da5-8f18-35c923fbdb0a", fileName: "report.pdf",
    reportedSize: "5", downloadHandle: "b".repeat(43), preview: null };
function harness({ outcomes, wait = () => new Promise(() => {}), attachments = [pdf], fetchBytes, urls,
    open = async () => ({ status: "handed-off" }) } = {}) {
    const calls = [];
    const navigator = { userActivation: { isActive: false } };
    const client = createAttachmentClient({
        document: { createElement: tag => new Element(tag) },
        text: (tag, textContent) => Object.assign(new Element(tag), { textContent }),
        navigator,
        wait,
        fetchBytes,
        urls,
        request: async (route, options) => {
            const body = options.body ? JSON.parse(options.body) : {};
            calls.push({ route, body });
            if (route.startsWith("/api/attachment-open/")) return open(options);
            if (body.action === "download-status") return outcomes.shift();
            return body.action === "inventory" ? { status: "inventory", attachments } : { invalidated: true };
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
    assert.match(descendants(h.root).map(item => item.textContent).join("\n"), /sent report\.pdf to the host.*Downloads folder.*cannot confirm the saved location/);
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

const textLines = root => descendants(root).map(item => item.textContent).join("\n");
const bytesResponse = (chunks, type = "text/plain; charset=utf-8", onRead = () => {}) => {
    const total = chunks.reduce((sum, chunk) => sum + chunk.byteLength, 0);
    const queue = [...chunks];
    return { headers: { get: name => ({ "content-length": String(total), "content-type": type })[name] },
        body: { getReader: () => ({ read: async () => (onRead(), queue.length) ? { done: false, value: queue.shift() } : { done: true },
            cancel: async () => {} }) } };
};
const notes = { id: "5dab0171-49dc-4da5-8f18-35c923fbdb0a", fileName: "notes.txt", reportedSize: "11",
    downloadHandle: "c".repeat(43), preview: "text", previewHandle: "d".repeat(43) };

test("download shows an indeterminate progress bar that completes on sent and hides on refusal", async () => {
    for (const [outcome, visible] of [[{ status: "sent" }, true], [{ status: "interrupted" }, false]]) {
        let resume;
        const h = harness({ outcomes: [outcome], wait: () => new Promise(resolve => { resume = resolve; }) });
        h.navigator.userActivation.isActive = true;
        await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
        const progress = descendants(h.root).find(item => item.tag === "progress");
        assert.equal(progress.hidden, true, "progress is hidden before any click");
        descendants(h.root).find(item => item.tag === "a").listeners.click({ isTrusted: true });
        assert.equal(progress.hidden, false);
        assert.equal("value" in progress, false, "progress is indeterminate while Twig reads");
        resume();
        await settle(); await settle();
        assert.equal(progress.hidden, !visible);
        if (visible) assert.equal(progress.value, 1);
    }
});

test("Preview is offered only for previewable entries and renders text in-canvas with byte progress", async () => {
    const fetches = [], sampled = [];
    let h;
    const sample = () => {
        const bar = descendants(h.root).find(item => item.tag === "progress" && /^Preview/.test(item.attributes["aria-label"]));
        sampled.push([bar.value, bar.max]);
    };
    h = harness({ attachments: [pdf, notes], fetchBytes: async (path, options) => {
        fetches.push(path);
        assert.ok(options.signal);
        return bytesResponse([new TextEncoder().encode("hello "), new TextEncoder().encode("world")], undefined, sample);
    } });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    assert.equal(h.button("Preview report.pdf"), undefined);
    const open = h.button("Preview notes.txt");
    await open.listeners.click({ isTrusted: false });
    assert.equal(fetches.length, 0, "synthetic activation never reads");
    await open.listeners.click({ isTrusted: true });
    assert.deepEqual(fetches, ["/api/attachment-preview/" + "d".repeat(43)]);
    const pre = descendants(h.root).find(item => item.tag === "pre");
    assert.equal(pre.textContent, "hello world");
    assert.deepEqual(sampled, [[0, 11], [6, 11], [11, 11]], "byte progress advances toward Content-Length");
    assert.match(textLines(h.root), /Previewed notes\.txt \(11 bytes\).*Nothing was saved or run/);
    assert.equal(open.disabled, true, "preview handles are single use");
    await open.listeners.click({ isTrusted: true });
    assert.equal(fetches.length, 1);
    assert.equal(h.calls.some(call => call.body.action === "download-status"), false);
    h.button("Close preview").listeners.click({ isTrusted: true });
    assert.equal(descendants(h.root).some(item => item.tag === "pre"), false);
});

test("image preview uses a blob URL that is revoked when leaving the item", async () => {
    const created = [], revoked = [];
    const image = { ...notes, fileName: "shot.png", preview: "image" };
    const h = harness({ attachments: [image],
        fetchBytes: async () => bytesResponse([new Uint8Array([137, 80, 78, 71])], "image/png"),
        urls: { createObjectURL: blob => { created.push(blob.type); return "blob:one"; },
            revokeObjectURL: url => revoked.push(url) } });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    await h.button("Preview shot.png").listeners.click({ isTrusted: true });
    const img = descendants(h.root).find(item => item.tag === "img");
    assert.equal(img.src, "blob:one");
    assert.deepEqual(created, ["image/png"]);
    await h.client.leave();
    assert.deepEqual(revoked, ["blob:one"]);
});

test("preview refusal is reported without retry", async () => {
    let attempts = 0;
    const h = harness({ attachments: [notes], fetchBytes: async () => { attempts++; throw new Error("Attachment download refused (attachment-stale)."); } });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    await h.button("Preview notes.txt").listeners.click({ isTrusted: true });
    assert.match(textLines(h.root), /Preview refused or unavailable: .*attachment-stale.*No automatic retry/);
    assert.equal(attempts, 1);
});

test("default app button requires two trusted clicks and never reuses preview or download", async () => {
    const h = harness({ attachments: [{ ...notes, openHandle: "e".repeat(43) }] });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    const open = h.button("Open in default app notes.txt");
    await open.listeners.click({ isTrusted: false });
    assert.equal(h.button("Confirm open in default app"), undefined);
    await open.listeners.click({ isTrusted: true });
    assert.equal(h.calls.length, 1, "showing confirmation must not read or launch");
    assert.match(textLines(h.root), /separate copy.*temporary folder.*untrusted/);
    const confirm = h.button("Confirm open in default app");
    await confirm.listeners.click({ isTrusted: false });
    assert.equal(h.calls.length, 1);
    await confirm.listeners.click({ isTrusted: true });
    assert.deepEqual(h.calls[1], { route: "/api/attachment-open/" + "e".repeat(43), body: {} });
    assert.match(textLines(h.root), /Windows accepted.*cannot confirm the app displayed/);
    await confirm.listeners.click({ isTrusted: true });
    assert.equal(h.calls.length, 2, "one confirmation must not launch twice");
});

test("canceled or stale confirmation cannot open; failed launch has no automatic retry", async () => {
    for (const action of ["cancel", "refresh", "leave"]) {
        const h = harness({ attachments: [{ ...pdf, openHandle: "e".repeat(43) }] });
        h.navigator.userActivation.isActive = true;
        await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
        await h.button("Open in default app report.pdf").listeners.click({ isTrusted: true });
        const confirm = h.button("Confirm open in default app");
        if (action === "cancel") h.button("Cancel open").listeners.click({ isTrusted: true });
        if (action === "refresh") await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
        if (action === "leave") await h.client.leave();
        await confirm.listeners.click({ isTrusted: true });
        assert.equal(h.calls.some(call => call.route.startsWith("/api/attachment-open/")), false);
    }
    const h = harness({ attachments: [{ ...pdf, openHandle: "e".repeat(43) }],
        open: async () => { throw new Error("launch unavailable"); } });
    h.navigator.userActivation.isActive = true;
    await h.button("Show attachments (online)").listeners.click({ isTrusted: true });
    await h.button("Open in default app report.pdf").listeners.click({ isTrusted: true });
    await h.button("Confirm open in default app").listeners.click({ isTrusted: true });
    assert.match(textLines(h.root), /outcome unknown.*launch unavailable.*no automatic retry/);
    assert.equal(h.calls.filter(call => call.route.startsWith("/api/attachment-open/")).length, 1);
});
