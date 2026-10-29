import assert from "node:assert/strict";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createAttachmentOpener } from "../attachment-opener.mjs";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";

const id = "4dab0171-49dc-4da5-8f18-35c923fbdb0a";
const body = Buffer.from([0, 255, 13, 10, 128]);
const detail = workItemId => ({
    itemKey: { workItemId, bindingId: "binding", connectionId: "connection" },
    benchId: "12", identityId: "identity", benchName: "Sprint",
    origin: { bindingRevision: 4, selectionRevision: 8, attachmentRevision: 3,
        selectionSource: "bench", storageGeneration: "9" },
    membership: { seed: false, selected: true },
});
async function setup({ download, fileName = "report.pdf", attachmentOpener } = {}) {
    const calls = [];
    const server = await createCanvasServer({
        workspacePath: "unused",
        workbench: true,
        attachmentOpener,
        readNativeBench: async () => ({ benchId: "12", benchName: "Sprint" }),
        readNativeBenchDetail: async ({ workItemId }) => detail(workItemId),
        readNativeAttachments: async request => {
            calls.push(request);
            if (request.action === "inventory") return {
                status: "inventory", context: { workItemRevision: 7 },
                attachments: [{ id, fileName, reportedSize: String(body.length) }],
            };
            if (download) return download();
            return { status: "download", attachment: { id, fileName },
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

async function observe(h, downloadHandle, targetHandle = h.attachmentTargetHandle, headers = h.headers) {
    return fetch(new URL("/api/attachments", h.origin), {
        method: "POST", headers,
        body: JSON.stringify({ action: "download-status", targetHandle, downloadHandle }),
    });
}

test("default-app opening requires a distinct current single-use handle, token and POST", async () => {
    const launches = [];
    const h = await setup({ attachmentOpener: {
        available: true, open: async input => { launches.push(input); },
    } });
    try {
        const entry = h.inventory.attachments[0];
        assert.equal(entry.fileName, "report.pdf", "fixture must be a document, not a preview-only text file");
        assert.match(entry.openHandle ?? "", /^[A-Za-z0-9_-]{43}$/);
        assert.notEqual(entry.openHandle, entry.downloadHandle);
        const url = new URL("/api/attachment-open/" + entry.openHandle, h.origin);
        assert.equal((await fetch(url, { method: "POST", headers: { Origin: h.origin } })).status, 401);
        assert.equal((await fetch(url, { headers: h.headers })).status, 404);
        assert.equal((await fetch(url, { method: "POST", headers: { ...h.headers, Origin: "https://example.invalid" } })).status, 403);
        assert.equal((await fetch(new URL("/api/attachment-open/" + entry.downloadHandle, h.origin),
            { method: "POST", headers: h.headers })).status, 409);
        assert.equal(launches.length, 0);
        const result = await fetch(url, { method: "POST", headers: h.headers });
        assert.equal(result.status, 200);
        assert.deepEqual(await result.json(), { status: "handed-off" });
        assert.equal(launches.length, 1);
        assert.equal(launches[0].fileName, entry.fileName);
        assert.deepEqual(launches[0].content, body);
        assert.equal(typeof launches[0].assertCurrent, "function");
        assert.equal((await fetch(url, { method: "POST", headers: h.headers })).status, 409);
        assert.equal(launches.length, 1);
        assert.deepEqual(await (await observe(h, entry.downloadHandle)).json(), { status: "not-requested" });
    } finally { await h.server.close(); }
});

test("default-app opening rejects stale selections, unsafe types and launcher failures without retry", async () => {
    const launches = [];
    const opener = { available: true, open: async () => { launches.push(true); throw new Error("local path must not leak"); } };
    const unsafe = await setup({ fileName: "run.cmd", attachmentOpener: opener });
    try { assert.equal(unsafe.inventory.attachments[0].openHandle, undefined); }
    finally { await unsafe.server.close(); }
    const unavailable = await setup({ attachmentOpener: { available: false } });
    try { assert.equal(unavailable.inventory.attachments[0].openHandle, undefined); }
    finally { await unavailable.server.close(); }
    const h = await setup({ attachmentOpener: opener });
    try {
        const handle = h.inventory.attachments[0].openHandle;
        const url = new URL("/api/attachment-open/" + handle, h.origin);
        const response = await fetch(url, { method: "POST", headers: h.headers });
        assert.equal(response.status, 502);
        assert.match((await response.json()).error, /default app.*unavailable/i);
        assert.equal((await fetch(url, { method: "POST", headers: h.headers })).status, 409);
        assert.equal(launches.length, 1);
        await h.server.loadBenchDetail(43);
        assert.equal((await fetch(url, { method: "POST", headers: h.headers })).status, 409);
        assert.equal(launches.length, 1);
    } finally { await h.server.close(); }
});

test("qualified native download reaches real marked local copy before default-app launcher", {
    skip: process.platform !== "win32",
}, async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "twig-open-route-test-"));
    let launches = 0;
    let h;
    try {
        const attachmentOpener = createAttachmentOpener({ tempRoot: root, execFile: async (_executable, _args, options) => {
            const file = options.env.TWIG_ATTACHMENT_OPEN_FILE;
            assert.deepEqual(await readFile(file), body);
            assert.equal(await readFile(file + ":Zone.Identifier", "utf8"), "[ZoneTransfer]\r\nZoneId=3\r\n");
            launches++;
        } });
        assert.equal(attachmentOpener.available, true, "fixture needs a local Windows host");
        h = await setup({ fileName: "notes.txt", attachmentOpener });
        const entry = h.inventory.attachments[0];
        assert.match(entry.openHandle, /^[A-Za-z0-9_-]{43}$/);
        const response = await fetch(new URL("/api/attachment-open/" + entry.openHandle, h.origin),
            { method: "POST", headers: h.headers });
        assert.equal(response.status, 200);
        assert.equal(launches, 1);
        assert.equal(h.calls.filter(call => call.action === "download").length, 1);
    } finally {
        if (h) await h.server.close();
        await rm(root, { recursive: true, force: true });
    }
});

test("download observations are authenticated, target-bound and never invoke the native reader", async () => {
    const h = await setup();
    try {
        const handle = h.inventory.attachments[0].downloadHandle;
        assert.deepEqual(await (await observe(h, handle)).json(), { status: "not-requested" });
        assert.equal((await observe(h, handle, "x".repeat(43))).status, 400);
        assert.equal((await observe(h, handle, h.attachmentTargetHandle,
            { Origin: h.origin, "Content-Type": "application/json" })).status, 401);
        assert.equal(h.calls.length, 1);
        await h.server.loadBenchDetail(43);
        assert.notEqual((await observe(h, handle)).status, 200);
        assert.equal(h.calls.length, 1);
    } finally { await h.server.close(); }
});

test("native read progress and refusal remain observable without another native invocation", async () => {
    let finish;
    const h = await setup({ download: () => new Promise(resolve => { finish = resolve; }) });
    try {
        const handle = h.inventory.attachments[0].downloadHandle;
        const pending = fetch(new URL("/api/attachment-download/" + handle, h.origin), {
            headers: { Origin: h.origin, "Sec-Fetch-Site": "same-origin" },
        });
        for (let attempt = 0; attempt < 50 && !finish; attempt++)
            await new Promise(resolve => setTimeout(resolve, 10));
        assert.equal(typeof finish, "function", "native download must be in progress");
        assert.deepEqual(await (await observe(h, handle)).json(), { status: "reading" });
        finish({ status: "refused", code: "attachment-stale" });
        assert.equal((await pending).status, 409);
        const observation = await (await observe(h, handle)).json();
        assert.equal(observation.status, "refused");
        assert.match(observation.message, /attachment-stale/);
        assert.equal(h.calls.filter(call => call.action === "download").length, 1);
    } finally { await h.server.close(); }
});

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
        assert.deepEqual(await (await observe(h, entry.downloadHandle)).json(), { status: "sent" });
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
        const observation = await (await observe(h, entry.downloadHandle)).json();
        assert.equal(observation.status, "refused");
        assert.match(observation.message, /same-origin/);
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

test("preview handles exist only for allowlisted names and are token-gated, inline, sandboxed and single use", async () => {
    const pdf = await setup();
    try {
        assert.equal(pdf.inventory.attachments[0].preview, null);
        assert.equal("previewHandle" in pdf.inventory.attachments[0], false);
    } finally { await pdf.server.close(); }

    const h = await setup({ fileName: "notes.txt" });
    try {
        const entry = h.inventory.attachments[0];
        assert.equal(entry.preview, "text");
        assert.match(entry.previewHandle, /^[A-Za-z0-9_-]{43}$/);
        assert.notEqual(entry.previewHandle, entry.downloadHandle);
        const url = new URL("/api/attachment-preview/" + entry.previewHandle, h.origin);
        assert.equal((await fetch(url, { headers: { Origin: h.origin } })).status, 401);
        const crossed = await fetch(new URL("/api/attachment-preview/" + entry.downloadHandle, h.origin),
            { headers: { Authorization: h.headers.Authorization, Origin: h.origin } });
        assert.equal(crossed.status, 409, "download handles cannot open previews");
        assert.equal(h.calls.filter(call => call.action === "download").length, 0);
        const response = await fetch(url, { headers: { Authorization: h.headers.Authorization, Origin: h.origin } });
        assert.equal(response.status, 200);
        assert.equal(response.headers.get("content-type"), "text/plain; charset=utf-8");
        assert.equal(response.headers.get("content-disposition"), "inline");
        assert.equal(response.headers.get("content-security-policy"), "sandbox; default-src 'none'");
        assert.equal(response.headers.get("content-length"), String(body.length));
        assert.deepEqual(Buffer.from(await response.arrayBuffer()), body);
        const repeated = await fetch(url, { headers: { Authorization: h.headers.Authorization, Origin: h.origin } });
        assert.equal(repeated.status, 409);
        assert.equal(h.calls.filter(call => call.action === "download").length, 1);
        assert.deepEqual(await (await observe(h, entry.downloadHandle)).json(), { status: "not-requested" },
            "previewing does not consume or alter the download");
    } finally { await h.server.close(); }
});
