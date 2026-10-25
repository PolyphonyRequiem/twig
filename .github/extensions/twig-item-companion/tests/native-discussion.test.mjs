import assert from "node:assert/strict";
import test from "node:test";
import { createNativeDiscussionReader } from "../native-discussion.mjs";
import { discussionTarget, discussionPage, discussionRefusal, childHarness } from "./discussion-fixture.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";
const target = discussionTarget();
const input = { target, workspacePath: target.expectedKey.checkoutRoot };
function reader(h) { return createNativeDiscussionReader({ executable: fakeExecutable(), ...h, realpath: async value => value }); }

test("discussion uses fixed shell-free command, admitted cwd, bounded stdin EOF and no browser/provider destination", async () => {
    const h = childHarness();
    const result = await reader(h)(input);
    assert.equal(result.status, "read");
    assert.equal(h.calls.length, 1);
    const call = h.calls[0];
    assert.equal(call.exe, FAKE_TWIG);
    assert.deepEqual(call.args, ["bench", "discussion", "--stdin", "-o", "json"]);
    assert.equal(call.options.cwd, input.workspacePath);
    assert.equal(call.options.shell, false);
    assert.deepEqual(call.options.stdio, ["pipe", "pipe", "pipe"]);
    assert.equal(call.input.cursor, null);
    assert.deepEqual(call.input.itemKey, target.expectedKey);
    assert.equal(call.input.expectBench, target.bench);
    assert.equal(call.input.expectIdentity, target.identityId);
});
test("next read echoes exact captured cursor and context without reinterpreting its token as a URL", async () => {
    const h = childHarness(() => ({ value: discussionPage(target, false) }));
    const cursor = discussionPage().paging.nextCursor;
    await reader(h)({ ...input, cursor, context: cursor.context });
    assert.deepEqual(h.calls[0].input.cursor, cursor);
    assert.equal(h.calls[0].args.some(arg => arg.includes("private+token")), false);
});
test("native exit one/two accepts only valid refusal envelopes and suppresses raw stderr", async () => {
    for (const code of [1, 2]) {
        const h = childHarness(() => ({ value: discussionRefusal(code === 2 ? "usage" : "discussion-denied"),
            code, stderr: "SECRET_TOKEN https://private.invalid" }));
        const result = await reader(h)(input);
        assert.equal(result.status, "refused");
        assert.doesNotMatch(JSON.stringify(result), /SECRET|private/);
    }
});
test("missing/malformed stdout and abnormal exits never default to successful empty pages or leak stderr", async () => {
    for (const response of [{ raw: "" }, { raw: "{}" }, { raw: "not json", code: 1 },
        { value: discussionPage(), code: 3 }, { value: discussionRefusal(), code: 0 }]) {
        const h = childHarness(() => ({ ...response, stderr: "PRIVATE-COMMENT" }));
        await assert.rejects(reader(h)(input), error => !error.message.includes("PRIVATE-COMMENT"));
    }
});
test("wrong checkout and pre-canceled requests refuse before spawning", async () => {
    const h = childHarness();
    await assert.rejects(reader(h)({ ...input, workspacePath: "other-checkout" }));
    const abort = new AbortController(); abort.abort();
    await assert.rejects(reader(h)({ ...input, signal: abort.signal }));
    assert.equal(h.calls.length, 0);
});
test("abort stops only owned child, awaits close, and excludes a concurrent second read", async () => {
    const h = childHarness(() => null);
    const read = reader(h);
    const controller = new AbortController();
    const pending = read({ ...input, signal: controller.signal });
    await Promise.resolve(); await Promise.resolve();
    await assert.rejects(read(input), /busy/);
    controller.abort();
    await assert.rejects(pending, /canceled/);
    assert.deepEqual(h.stops, [12345]);
    assert.equal(h.calls.length, 1);
});
test("extension stdout cap refuses an oversized page visibly, without accepting a truncated prefix", async () => {
    const h = childHarness(() => ({ raw: "x".repeat(4 * 1024 * 1024 + 1) }));
    await assert.rejects(reader(h)(input), /extension 4MiB output limit/);
    assert.deepEqual(h.stops, [12345]);
});
test("pipe errors and excessive diagnostics are sanitized and stop the owned child", async () => {
    for (const fault of ["pipe", "stderr"]) {
        const h = childHarness(call => {
            if (fault === "pipe") call.child.stdin.emit("error", new Error("SECRET"));
            else call.child.stderr.emit("data", Buffer.alloc(65537));
            return null;
        });
        await assert.rejects(reader(h)(input), error => !error.message.includes("SECRET"));
        assert.deepEqual(h.stops, [12345]);
    }
});
test("deadline and unresolved closure latch ownership instead of admitting a new child", async t => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const h = childHarness(() => null);
    const read = createNativeDiscussionReader({ executable: fakeExecutable(), spawn: h.spawn, realpath: async value => value,
        stopTree: async child => { h.stops.push(child.pid); } });
    const pending = read(input);
    const rejected = assert.rejects(pending, /15-second deadline/);
    await Promise.resolve(); await Promise.resolve();
    t.mock.timers.tick(19000);
    await rejected;
    await assert.rejects(read(input), /unresolved/);
    assert.deepEqual(h.stops, [12345]);
});
