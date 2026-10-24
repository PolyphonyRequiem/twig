import assert from "node:assert/strict";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";

const observation = {
    itemKey: { workItemId: 42, bindingId: "binding", connectionId: "connection" },
    benchId: "bench",
    identityId: "identity",
};

test("serves the canvas on loopback and requires its per-instance token for API calls", async () => {
    const reads = [];
    const server = await createCanvasServer({
        workspacePath: "unused",
        readNativeDetail: async (request) => {
            reads.push(request);
            return observation;
        },
    });
    try {
        const page = await fetch(server.url);
        assert.equal(page.status, 200);
        assert.match(page.headers.get("content-security-policy"), /default-src 'none'/);
        assert.match(page.headers.get("content-security-policy"), /script-src 'nonce-/);
        const html = await page.text();
        assert.match(html, /Twig item companion/);
        const tokenSource = html.match(/const token = ("[^"]+");/);
        assert.ok(tokenSource);
        const token = JSON.parse(tokenSource[1]);

        const unauthenticated = await fetch(new URL("/api/state", server.url));
        assert.equal(unauthenticated.status, 401);

        const wrongOrigin = await fetch(new URL("/api/state", server.url), {
            headers: { Authorization: "Bearer invalid", Origin: "https://example.com" },
        });
        assert.equal(wrongOrigin.status, 403);

        const state = await fetch(new URL("/api/state", server.url), {
            headers: { Authorization: `Bearer ${token}` },
        });
        assert.equal(state.status, 200);
        assert.equal((await state.json()).observation, null);

        const loaded = await fetch(new URL("/api/detail", server.url), {
            method: "POST",
            headers: {
                Authorization: `Bearer ${token}`,
                "Content-Type": "application/json",
                Origin: new URL(server.url).origin,
            },
            body: JSON.stringify({ action: "load", workItemId: 42 }),
        });
        assert.equal(loaded.status, 200);
        assert.equal((await loaded.json()).observation.itemKey.workItemId, 42);
        assert.equal(reads.length, 1);
        assert.equal(reads[0].workItemId, 42);
    } finally {
        await server.close();
    }
});

test("blocks concurrent native reads and retains the exact captured qualifiers for refresh", async () => {
    let releaseRead;
    let calls = 0;
    const server = await createCanvasServer({
        workspacePath: "unused",
        readNativeDetail: async (request) => {
            calls++;
            if (calls === 1) {
                await new Promise((resolve) => { releaseRead = resolve; });
            }
            if (calls === 2) {
                assert.deepEqual(request.expected, {
                    benchId: "bench",
                    bindingId: "binding",
                    identityId: "identity",
                    connectionId: "connection",
                });
            }
            return observation;
        },
    });
    try {
        const loading = server.load(42);
        await assert.rejects(server.load(42), /already in progress/);
        releaseRead();
        assert.equal(await loading, observation);
        assert.equal(await server.refresh(), observation);
        assert.equal(calls, 2);
    } finally {
        await server.close();
    }
});

test("keeps the last observation visible but reports a failed refresh", async () => {
    let calls = 0;
    const server = await createCanvasServer({
        workspacePath: "unused",
        readNativeDetail: async () => {
            calls++;
            if (calls > 1) throw new Error("Binding changed.");
            return observation;
        },
    });
    try {
        await server.load(42);
        await assert.rejects(server.refresh(), /Binding changed/);
        const page = await fetch(server.url);
        const html = await page.text();
        const tokenSource = html.match(/const token = ("[^"]+");/);
        assert.ok(tokenSource);
        const token = JSON.parse(tokenSource[1]);
        const state = await fetch(new URL("/api/state", server.url), {
            headers: { Authorization: `Bearer ${token}` },
        });
        const result = await state.json();
        assert.deepEqual(result.observation, observation);
        assert.equal(result.error, "Binding changed.");
    } finally {
        await server.close();
    }
});
