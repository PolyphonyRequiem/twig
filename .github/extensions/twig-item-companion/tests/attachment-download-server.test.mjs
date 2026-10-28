import assert from "node:assert/strict";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";

const id = "4dab0171-49dc-4da5-8f18-35c923fbdb0a";
const body = Buffer.from([0, 255, 13, 10, 128]);
const detail = workItemId => ({
    itemKey: { workItemId, bindingId: "binding", connectionId: "connection" },
    benchId: "12", identityId: "identity", benchName: "Sprint",
    origin: { bindingRevision: 4, selectionRevision: 8, attachmentRevision: 3,
        selectionSource: "bench", storageGeneration: "9" },
    membership: { seed: false, selected: true },
});
async function setup() {
    const calls = [];
    const server = await createCanvasServer({
        workspacePath: "unused",
        workbench: true,
        readNativeBench: async () => ({ benchId: "12", benchName: "Sprint" }),
        readNativeBenchDetail: async ({ workItemId }) => detail(workItemId),
        readNativeAttachments: async request => {
            calls.push(request);
            if (request.action === "inventory") return {
                status: "inventory", context: { workItemRevision: 7 },
                attachments: [{ id, fileName: "report.pdf", reportedSize: String(body.length) }],
            };
            return { status: "download", attachment: { id, fileName: "report.pdf" },
                contentType: "application/pdf", contentLength: body.length, sha256: "a".repeat(64), content: body };
        },
    });
    const page = await fetch(server.url);
    const html = await page.text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const origin = new URL(server.url).origin;
    const headers = { Authorization: `Bearer ${token}`, Origin: origin, "Content-Type": "application/json" };
    await server.loadBench("Sprint");
    const detailResponse = await fetch(new URL("/api/bench", server.url), {
        method: "POST", headers, body: JSON.stringify({ action: "detail", workItemId: 42 }),
    });
    const { attachmentTargetHandle } = await detailResponse.json();
    const inventoryResponse = await fetch(new URL("/api/attachments", server.url), {
        method: "POST", headers, body: JSON.stringify({ action: "inventory", targetHandle: attachmentTargetHandle }),
    });
    return { server, calls, origin, headers, attachmentTargetHandle, inventory: await inventoryResponse.json() };
}

test("attachment download is a single-use same-origin browser response with bounded safe headers", async () => {
    const h = await setup();
    try {
        assert.equal(h.inventory.status, "inventory");
        const entry = h.inventory.attachments[0];
        const url = new URL("/api/attachment-download/" + entry.downloadHandle, h.origin);
        const response = await fetch(url, { headers: { Origin: h.origin, "Sec-Fetch-Site": "same-origin" } });
        assert.equal(response.status, 200);
        assert.equal(response.headers.get("content-type"), "application/pdf");
        assert.equal(response.headers.get("content-disposition"), "attachment; filename=\"report.pdf\"; filename*=UTF-8''report.pdf");
        assert.equal(response.headers.get("content-length"), String(body.length));
        assert.equal(response.headers.get("x-content-type-options"), "nosniff");
        assert.equal(response.headers.get("cache-control"), "no-store");
        assert.deepEqual(Buffer.from(await response.arrayBuffer()), body);
        assert.equal(h.calls.filter(call => call.action === "download").length, 1);
        const repeated = await fetch(url, { headers: { Origin: h.origin, "Sec-Fetch-Site": "same-origin" } });
        assert.equal(repeated.status, 409);
        assert.equal(h.calls.filter(call => call.action === "download").length, 1);
    } finally { await h.server.close(); }
});

test("cross-origin and cross-site downloads refuse before reaching the native reader", async () => {
    const h = await setup();
    try {
        const entry = h.inventory.attachments[0];
        const url = new URL("/api/attachment-download/" + entry.downloadHandle, h.origin);
        const crossOrigin = await fetch(url, { headers: { Origin: "https://example.invalid", "Sec-Fetch-Site": "cross-site" } });
        assert.equal(crossOrigin.status, 403);
        const crossSite = await fetch(url, { headers: { Origin: h.origin, "Sec-Fetch-Site": "cross-site" } });
        assert.equal(crossSite.status, 403);
        assert.equal(h.calls.filter(call => call.action === "download").length, 0);
    } finally { await h.server.close(); }
});

test("changing qualified detail invalidates outstanding opaque download handles", async () => {
    const h = await setup();
    try {
        const entry = h.inventory.attachments[0];
        await h.server.loadBenchDetail(43);
        const response = await fetch(new URL("/api/attachment-download/" + entry.downloadHandle, h.origin), {
            headers: { Origin: h.origin, "Sec-Fetch-Site": "same-origin" },
        });
        assert.equal(response.status, 409);
        assert.equal(h.calls.filter(call => call.action === "download").length, 0);
    } finally { await h.server.close(); }
});
