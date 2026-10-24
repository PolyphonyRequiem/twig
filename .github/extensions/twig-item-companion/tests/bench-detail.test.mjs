// SYNTHETIC fixtures pinned to the explicit-Bench detail contract authored in twig 06cb33b2
// (`bench detail <id> --bench <target> --document -o json`). These tests exercise client ->
// loopback -> reader composition across a fake process boundary. They are NOT native-binary
// integration; that waits until the native contract reaches main.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { createCanvasServer } from "../canvas-server.mjs";
import { createNativeBenchReader } from "../native-bench.mjs";
import { createNativeBenchDetailReader } from "../native-bench-detail.mjs";

const bench = JSON.parse(await readFile(new URL("./fixtures/bench-v2.json", import.meta.url), "utf8"));
const base = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));
const workspacePath = bench.browser.origin.worktreeRoot;
const expected = { benchId: bench.browser.benchId, origin: { ...bench.browser.origin } };

function detailPayload(change = () => {}) {
    const value = structuredClone(base);
    value.benchId = bench.browser.benchId;
    value.benchName = bench.browser.benchName;
    value.origin = { ...bench.browser.origin, storageGeneration: null };
    value.observation = "raw-mirror";
    value.membership = { selected: true, seed: false, pending: true };
    value.completeness = {
        sourceComplete: false, sourceChildCount: null, cachedChildCount: 1, parentCached: false, linksVerified: false,
        reasons: ["source-unknown", "cache-only", "layout-not-read-cache-only", "children-total-unknown",
            "related-items-not-exported", "parent-not-cached", "links-not-verified"],
    };
    value.layoutStatus = "unavailable";
    value.layoutSource = "twig-fallback";
    value.document.processId = "twig.fallback";
    value.itemKey.checkoutRoot = workspacePath;
    change(value);
    return value;
}
function detailReader(payload = detailPayload(), calls = []) {
    return createNativeBenchDetailReader({
        realpath: async (path) => path,
        execFile(exe, args, options, callback) {
            calls.push({ exe, args, options });
            if (payload instanceof Error) callback(payload, "", payload.stderr ?? "");
            else callback(null, JSON.stringify(payload), "");
        },
    });
}
const read = (reader, id = 42, expect = expected) => reader({ workItemId: id, workspacePath, expected: expect });

test("pinned 06cb33b2 shape: explicit --bench, --document, expect guards, no sync, no current-Bench change", async () => {
    const calls = [];
    const detail = await read(detailReader(detailPayload(), calls));
    assert.deepEqual(calls[0].args, [
        "bench", "detail", "42", "--bench", "7", "--document", "-o", "json",
        "--expect-bench", "7", "--expect-binding", "fixture-binding", "--expect-identity", "fixture-identity",
    ]);
    assert.equal(calls[0].options.shell, false);
    assert.equal(calls[0].options.timeout, 15000);
    assert.ok(!calls[0].args.includes("--sync"));
    assert.equal(detail.layoutStatus, "unavailable");
    assert.equal(detail.completeness.sourceComplete, false);
});

test("origin change mid-session is refused rather than rendered as the captured Bench", async () => {
    for (const change of [
        (v) => { v.origin.attachmentRevision = 9; },
        (v) => { v.origin.bindingRevision = 4; },
        (v) => { v.origin.connectionRef = "other"; v.itemKey.connectionId = "other"; },
        (v) => { v.origin.storageGeneration = "g2"; },
        (v) => { v.benchId = "8"; },
    ]) {
        await assert.rejects(read(detailReader(detailPayload(change))), /origin or captured target changed/);
    }
});

test("reader refuses unsupported, unqualified, non-member and source-claiming detail", async () => {
    const cases = [
        [(v) => { v.version = 1; }, /version 2 raw-mirror/],
        [(v) => { delete v.origin; }, /origin qualification/],
        [(v) => { v.observation = "overlay"; }, /version 2 raw-mirror/],
        [(v) => { v.membership = { selected: false, seed: false, pending: false }; }, /Bench membership/],
        [(v) => { delete v.membership; }, /Bench membership/],
        [(v) => { v.completeness.sourceComplete = true; }, /empty or complete source/],
        [(v) => { v.completeness.sourceChildCount = 0; }, /empty or complete source/],
        [(v) => { v.layoutStatus = "served"; v.layoutSource = "server"; }, /empty or complete source/],
        [(v) => { v.itemKey.workItemId = 43; }, /different work item/],
        [(v) => { v.origin.worktreeRoot = "C:\\elsewhere"; }, /origin qualification/],
    ];
    for (const [change, message] of cases) await assert.rejects(read(detailReader(detailPayload(change))), message);
});

test("reader requires a captured Bench and surfaces native error codes verbatim", async () => {
    const calls = [];
    await assert.rejects(detailReader(detailPayload(), calls)({ workItemId: 42, workspacePath }), /Load a qualified named Bench/);
    assert.equal(calls.length, 0, "no Twig launch without an explicit captured target");
    for (const code of ["bench-item-not-member", "bench-item-not-cached", "bench-origin-changed"]) {
        const error = Object.assign(new Error("exit 1"), { stderr: JSON.stringify({ error: `${code}: refused` }) });
        await assert.rejects(read(detailReader(error)), new RegExp(`^Error: ${code}: refused`));
    }
});

async function open(detail, calls = []) {
    const server = await createCanvasServer({
        workspacePath, workbench: true,
        readNativeBench: createNativeBenchReader({
            realpath: async (path) => path,
            execFile: (exe, args, options, callback) => callback(null, JSON.stringify(bench), ""),
        }),
        readNativeBenchDetail: detailReader(detail, calls),
    });
    const html = await (await fetch(server.url)).text();
    const token = JSON.parse(html.match(/const token = ("[^"]+");/)[1]);
    const post = (body) => fetch(new URL("/api/bench", server.url), {
        method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        body: JSON.stringify(body),
    });
    return { server, post, html };
}

test("loopback detail action uses the captured origin and never runs before a Bench is loaded", async () => {
    const calls = [];
    const { server, post, html } = await open(detailPayload(), calls);
    try {
        assert.equal((await post({ action: "detail", workItemId: 42 })).status, 500);
        assert.equal(calls.length, 0);
        assert.equal((await post({ action: "load", bench: "Release focus" })).status, 200);
        const response = await post({ action: "detail", workItemId: 42 });
        assert.equal(response.status, 200);
        const { benchDetail } = await response.json();
        assert.equal(benchDetail.origin.attachmentRevision, bench.browser.origin.attachmentRevision);
        assert.equal(calls.length, 1);
        assert.match(html, /showDetail/);
        assert.doesNotMatch(html, /--sync/);
    } finally { await server.close(); }
});

test("loopback detail refuses mid-session origin drift", async () => {
    const { server, post } = await open(detailPayload((v) => { v.origin.selectionRevision = 99; }));
    try {
        await post({ action: "load", bench: "Release focus" });
        const response = await post({ action: "detail", workItemId: 42 });
        assert.equal(response.status, 500);
        assert.match((await response.json()).error, /origin or captured target changed/);
    } finally { await server.close(); }
});
