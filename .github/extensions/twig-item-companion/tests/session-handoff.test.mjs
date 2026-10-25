import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import test from "node:test";
import { createSessionHandoff } from "../session-handoff.mjs";

const target = {
    workItemId: 42, bench: "7", identityId: "identity",
    expectedKey: { checkoutRoot: "checkout", connectionId: "connection", bindingId: "binding", workItemId: 42 },
};
const deferred = () => {
    let resolve;
    let reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
};
function harness({ send = async () => "message-1", snapshot, timeoutMs = 1000 } = {}) {
    const calls = [];
    const session = { sessionId: "session-1", rpc: { metadata: {
        snapshot: snapshot || (async () => ({ sessionId: "session-1", workingDirectory: "checkout", isRemote: false })),
    } }, send: async options => { calls.push(options); return send(options); } };
    return { bridge: createSessionHandoff({ session, timeoutMs, resolvePath: async path => path }), calls };
}
test("prepare is inert; Start admits the exact edited brief with server-held target and fixed enqueue options", async () => {
    const { bridge, calls } = harness();
    const prepared = bridge.prepare(target);
    assert.equal(calls.length, 0);
    assert.match(prepared.handle, /^[A-Za-z0-9_-]{43}$/);
    const outcome = await bridge.start({ handle: prepared.handle, brief: "<b>My task</b>" });
    assert.equal(outcome.status, "admitted");
    assert.equal(outcome.messageId, "message-1");
    assert.deepEqual(outcome.target, target);
    assert.equal(calls.length, 1);
    assert.deepEqual(Object.keys(calls[0]).sort(), ["mode", "prompt"]);
    assert.equal(calls[0].mode, "enqueue");
    assert.ok(calls[0].prompt.endsWith("<b>My task</b>"));
    assert.ok(calls[0].prompt.includes(JSON.stringify(target)));
    await assert.rejects(bridge.start({ handle: prepared.handle, brief: "duplicate" }), /Prepare/);
    assert.equal(bridge.prepare(target).outcome.status, "admitted");
    assert.equal(calls.length, 1);
});
test("missing SDK surface refuses prepare without inventing a session", () => {
    assert.throws(() => createSessionHandoff({}).prepare(target), /unavailable/);
});
test("blank and UTF-8 oversized briefs refuse before send", async () => {
    const { bridge, calls } = harness();
    const { handle } = bridge.prepare(target);
    for (const brief of ["", " ", "\u00e9".repeat(4097), null]) {
        await assert.rejects(bridge.start({ handle, brief }), /8192/);
    }
    assert.equal(calls.length, 0);
});
test("invalidated and superseded handles cannot Start", async () => {
    const { bridge, calls } = harness();
    const old = bridge.prepare(target);
    bridge.invalidate();
    await assert.rejects(bridge.start({ handle: old.handle, brief: "task" }), /Prepare/);
    const second = bridge.prepare(target);
    bridge.prepare({ ...target, bench: "8" });
    await assert.rejects(bridge.start({ handle: second.handle, brief: "task" }), /Prepare/);
    assert.equal(calls.length, 0);
});
test("session ID, remote and checkout mismatch refuse before sending", async () => {
    for (const override of [{ sessionId: "another" }, { isRemote: true }, { workingDirectory: "other" }]) {
        const { bridge, calls } = harness({ snapshot: async () => ({
            sessionId: "session-1", workingDirectory: "checkout", isRemote: false, ...override,
        }) });
        const { handle } = bridge.prepare(target);
        const result = await bridge.start({ handle, brief: "task" });
        assert.equal(result.status, "refused");
        assert.match(result.error, /no longer owns/);
        assert.equal(calls.length, 0);
        assert.ok(bridge.prepare(target).handle);
    }
});
test("selection invalidation during preflight prevents sending", async () => {
    const gate = deferred();
    const { bridge, calls } = harness({ snapshot: () => gate.promise });
    const { handle } = bridge.prepare(target);
    const pending = bridge.start({ handle, brief: "task" });
    bridge.invalidate();
    gate.resolve({ sessionId: "session-1", workingDirectory: "checkout" });
    assert.equal((await pending).status, "refused");
    assert.equal(calls.length, 0);
});
test("failed send is unknown, sticky across invalidation and exact target return, not rollback", async () => {
    const { bridge, calls } = harness({ send: async () => { throw new Error("wire lost"); } });
    const { handle } = bridge.prepare(target);
    const result = await bridge.start({ handle, brief: "task" });
    assert.equal(result.status, "unknown");
    assert.match(result.error, /wire lost/);
    bridge.invalidate();
    assert.ok(bridge.prepare({ ...target, bench: "8" }).handle);
    assert.equal(bridge.prepare(structuredClone(target)).outcome.status, "unknown");
    assert.equal(calls.length, 1);
});
test("timeout after send retains late admitted evidence for the old target", async () => {
    const gate = deferred();
    const { bridge, calls } = harness({ send: () => gate.promise, timeoutMs: 10 });
    const { handle } = bridge.prepare(target);
    assert.equal((await bridge.start({ handle, brief: "task" })).status, "unknown");
    bridge.invalidate();
    gate.resolve("late-message");
    await setImmediate();
    assert.equal(bridge.outcomes[0].status, "admitted");
    assert.equal(bridge.outcomes[0].messageId, "late-message");
    assert.deepEqual(bridge.outcomes[0].target, target);
    assert.equal(calls.length, 1);
});
test("preflight timeout cannot later send after the deadline", async () => {
    const gate = deferred();
    const { bridge, calls } = harness({ snapshot: () => gate.promise, timeoutMs: 10 });
    const { handle } = bridge.prepare(target);
    assert.equal((await bridge.start({ handle, brief: "task" })).status, "unknown");
    gate.resolve({ sessionId: "session-1", workingDirectory: "checkout" });
    await setImmediate();
    assert.equal(calls.length, 0);
});
test("busy Start rejects duplicates and outcome snapshots cannot mutate held authority", async () => {
    const gate = deferred();
    const { bridge, calls } = harness({ send: () => gate.promise });
    const { handle } = bridge.prepare(target);
    const pending = bridge.start({ handle, brief: "task" });
    await setImmediate();
    assert.throws(() => bridge.prepare(target), /settling/);
    await assert.rejects(bridge.start({ handle, brief: "again" }), /Prepare/);
    bridge.outcomes[0].target.bench = "forged";
    gate.resolve("message");
    await pending;
    assert.equal(bridge.outcomes[0].target.bench, "7");
    assert.equal(calls.length, 1);
});
