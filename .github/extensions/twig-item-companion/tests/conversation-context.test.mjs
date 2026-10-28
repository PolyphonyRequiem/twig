import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
const detail = id => {
    const value = structuredClone(fixture);
    value.itemKey.checkoutRoot = process.cwd();
    value.itemKey.workItemId = value.item.id = value.document.workItemId = id;
    return value;
};
async function setup(snapshot) {
    const reads = [];
    const session = { sessionId: "runtime-session", rpc: { metadata: { snapshot: async () => {
        reads.push("snapshot");
        return snapshot();
    } } } };
    const server = await createCanvasServer({
        workspacePath: process.cwd(), conversationSession: session,
        readNativeDetail: async ({ workItemId }) => { reads.push(workItemId); return detail(workItemId); },
    });
    const html = await (await fetch(server.url)).text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const state = async () => (await fetch(new URL("/api/state", server.url), {
        headers: { Authorization: `Bearer ${token}` },
    })).json();
    return { server, state, reads, token, html };
}
const snapshot = () => ({ sessionId: "runtime-session", workingDirectory: process.cwd(), isRemote: false });

test("production companion exposes explicit item selection separately from unknown conversation/run discovery", async () => {
    const h = await setup(snapshot);
    try {
        assert.deepEqual(h.reads, [], "opening must not infer the active item or inspect the host");
        await h.server.load(42);
        assert.deepEqual(h.reads, [42]);
        const context = await h.server.refreshConversation();
        assert.equal(context.status, "current");
        assert.equal(context.sessionId, "runtime-session");
        assert.deepEqual(context.itemKey, detail(42).itemKey);
        assert.equal(context.itemDiscovery, "unknown");
        assert.equal(context.runDiscovery, "unknown");
        assert.equal(context.navigation, "unavailable");
        assert.deepEqual((await h.state()).conversationContext, context);
        assert.match(h.html, /Conversation context/);
        assert.match(h.html, /Linked run.*unknown/i);
        assert.doesNotMatch(h.html, /ghapp:|session\/new/);
    } finally { await h.server.close(); }
});

test("changed runtime session, remote host and wrong checkout are visible mismatches, never linked-item evidence", async () => {
    for (const change of [{ sessionId: "different" }, { isRemote: true }, { workingDirectory: path.dirname(process.cwd()) }]) {
        const h = await setup(() => ({ ...snapshot(), ...change }));
        try {
            await h.server.load(42);
            const result = await h.server.refreshConversation();
            assert.equal(result.status, "mismatch");
            assert.equal(result.itemDiscovery, "unknown");
            assert.equal(result.runDiscovery, "unknown");
            assert.equal(result.navigation, "unavailable");
        } finally { await h.server.close(); }
    }
});

test("failed host discovery stays unknown and explicit detail refresh preserves stable native identity", async () => {
    const h = await setup(() => { throw new Error("host unavailable"); });
    try {
        await h.server.load(42);
        const result = await h.server.refreshConversation();
        assert.equal(result.status, "unknown");
        assert.match(result.message, /unavailable/i);
        const refreshed = await h.server.refresh();
        assert.deepEqual(refreshed.itemKey, detail(42).itemKey);
        assert.equal((await h.state()).conversationContext.status, "stale");
        assert.deepEqual(h.reads, [42, "snapshot", 42]);
    } finally { await h.server.close(); }
});

test("late metadata cannot attach to another item or revive a closed companion", async () => {
    let release;
    const h = await setup(() => new Promise(resolve => { release = resolve; }));
    try {
        await h.server.load(42);
        const pending = h.server.refreshConversation();
        await new Promise(resolve => setImmediate(resolve));
        await h.server.load(43);
        release(snapshot());
        await assert.rejects(pending, /superseded|changed/i);
        assert.notEqual((await h.state()).conversationContext?.itemKey?.workItemId, 43);
        const closing = h.server.refreshConversation();
        await new Promise(resolve => setImmediate(resolve));
        await h.server.close();
        release(snapshot());
        await assert.rejects(closing, /closed|superseded/i);
    } finally { await h.server.close(); }
});

test("context refresh is authenticated, explicit and read-only; arbitrary linkage and destination input are refused", async () => {
    const h = await setup(snapshot);
    try {
        await h.server.load(42);
        const request = body => fetch(new URL("/api/context", h.server.url), {
            method: "POST", headers: { Authorization: `Bearer ${h.token}`, "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        assert.equal((await request({ action: "refresh", workItemId: 99, sessionId: "forged" })).status, 400);
        assert.equal((await request({ action: "refresh" })).status, 200);
        assert.deepEqual(h.reads, [42, "snapshot"]);
    } finally { await h.server.close(); }
});
