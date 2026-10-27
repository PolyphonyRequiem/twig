// Synthetic frozen-v1 contract tests. No native process, HTTP route or host activation.
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { createHash } from "node:crypto";
import { spawn as nativeSpawn } from "node:child_process";
import { mkdtemp, readFile, rm, rmdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { serializeStageRequest, serializeRetainRequest, validateEditingContext, validateEditingResult,
    validateRetainResult } from "../editing-document.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";
import { createNativeEditingAdapter, NativeEditingTransportError } from "../native-editing.mjs";
import { validateSaveResult } from "../native-save.mjs";

const form = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8")).document;
function context() {
    return {
        itemKey: { checkoutRoot: "C:\\fixture\\twig", connectionId: "fixture", bindingId: "binding", workItemId: 42 },
        externalIdentity: { providerKind: "ado", resourceScope: "native-resource", nativeId: "42" },
        benchId: "7", benchName: "Sprint", benchContentsDigest: "native-bench-digest", identityId: "identity",
        bindingRevision: "9007199254740993", selectionSource: "checkout", selectionRevision: "2",
        attachmentRevision: "3", storageGeneration: null, originDigest: "native-origin", evidenceDigest: "native-evidence",
    };
}
function document() {
    return {
        nativeContext: context(), generation: "9007199254740993", baselineRevision: "17", observedRevision: "17",
        currentRevision: "17", hasConflicts: false, changes: [], conflicts: [],
        systemControls: structuredClone(form.systemControls), pages: structuredClone(form.pages),
        fields: [
            { fieldRef: "System.Title", editable: true },
            { fieldRef: "Custom.Empty", editable: false, reason: "Native unavailable" },
        ],
        authorized: false, serverFinalAuthority: true,
    };
}
function result(status = "staged") {
    const value = { version: 1, status, committed: true, document: document() };
    if (status === "staged") value.document.generation = "9007199254740995";
    return value;
}
function request() {
    return {
        version: 1, nativeContext: context(), generation: "9007199254740993", baselineRevision: "17",
        observedRevision: "17", changes: [
            { fieldRef: "System.Title", proposedValue: "" },
            { fieldRef: "System.AssignedTo", proposedValue: null },
        ],
    };
}
function retainRequest() {
    const { changes, ...value } = request();
    return value;
}
function retainedResult() {
    const requested = retainRequest();
    const operationId = `draft-${"b".repeat(64)}`;
    const canonicalJson = `{"operations":[{"expectedRevision":17,"fields":{"Custom.Clear":null,"Custom.Empty":""},"id":"${operationId}","kind":"batch","workItemId":42}],"version":1,"workspace":{"organization":"fixture","project":"fixture"}}`;
    return { version: 1, status: "retained", committed: true, capture: {
        nativeContext: requested.nativeContext, generation: requested.generation,
        baselineRevision: "17", observedRevision: "17", currentRevision: "17",
        digest: createHash("sha256").update(canonicalJson).digest("hex"), operationId, canonicalJson,
        effects: [
            { fieldRef: "Custom.Clear", baselineValue: "previous", proposedValue: null },
            { fieldRef: "Custom.Empty", baselineValue: null, proposedValue: "" },
        ],
        originCoverage: "editing-context-and-native-origin-digest", authorized: false, canApply: false,
    } };
}
const workspacePath = "C:\\fixture\\twig";
function harness(response = result(), exit = 0, options = {}) {
    const calls = [];
    const adapter = createNativeEditingAdapter({ executable: options.executable ?? fakeExecutable(),
        realpath: async (value) => value,
        stopTree: async (child) => { child.stopped = true; },
        launch(exe, args, nativeOptions, callback) {
            const input = new EventEmitter();
            const call = { exe, args, options: nativeOptions, writes: [] };
            calls.push(call);
            input.end = (value, encoding) => { call.writes.push({ value, encoding }); call.eof = true; };
            const child = new EventEmitter();
            child.stdin = input;
            child.pid = 123;
            queueMicrotask(() => {
                if (options.stdinError) input.emit("error", new Error("pipe closed"));
                callback(exit === 0 ? null : Object.assign(new Error("native exit"), { code: exit }, options.error),
                    options.raw ?? JSON.stringify(response), "");
                child.emit("close", exit, null);
            });
            return child;
        },
    });
    return { adapter, calls };
}

function savedResult() {
    const capture = retainedResult().capture;
    return { version: 1, digest: capture.digest, callerIdentity: "identity", surface: "twig-workbench-explicit-save",
        hostAttested: false, status: "saved", verified: true, settled: true, error: null,
        draftSettlement: "settled", draftSettlementError: null, unsettledAcknowledgments: {},
        operations: [{ opId: capture.operationId, ordinal: 0, state: "Verified", error: null,
            warning: null, resultJson: '{"revision":18}' }] };
}
test("Save uses the same pinned bounded runner and sends only the retained digest context, never a proposal file", async () => {
    const native = savedResult();
    const executable = fakeExecutable();
    const h = harness(native, 0, { executable });
    const capture = retainedResult().capture;
    const saved = await h.adapter.save({ capture, workspacePath });
    assert.deepEqual(saved, native);
    assert.deepEqual(h.calls[0].args, ["draft", "save", "--stdin", "-o", "json"]);
    assert.equal(h.calls[0].exe, FAKE_TWIG);
    assert.deepEqual(executable.prepared, ["draft-save"]);
    const request = JSON.parse(h.calls[0].writes[0].value);
    assert.equal(request.callerIdentity, capture.nativeContext.identityId);
    assert.deepEqual(Object.keys(request).sort(), ["callerIdentity", "digest", "surface", "version"]);
    assert.equal(h.calls[0].eof, true);
});

test("Save validates operation coverage and never reports verified effects or settlement from contradictory success", () => {
    const value = savedResult();
    const expected = { exitCode: 0, digest: value.digest, callerIdentity: value.callerIdentity,
        operationId: value.operations[0].opId };
    for (const mutate of [
        x => { x.operations = []; }, x => { x.operations[0].opId = "other"; },
        x => { x.operations[0].state = "Indeterminate"; }, x => { x.draftSettlement = "retained"; },
        x => { x.hostAttested = true; }, x => { x.callerIdentity = "other"; },
        x => { x.unsettledAcknowledgments = { held: "ack" }; },
    ]) {
        const corrupted = structuredClone(value);
        mutate(corrupted);
        assert.throws(() => validateSaveResult(corrupted, expected));
    }
    const unsettled = { ...value, status: "verified-draft-retained", settled: false,
        draftSettlement: "retained", draftSettlementError: "Newer local intent retained" };
    assert.deepEqual(validateSaveResult(unsettled, { ...expected, exitCode: 1 }), unsettled);
});

test("STAGE uses bounded shell-free owned stdin EOF and preserves exact context, order, null and empty", async () => {
    const { adapter, calls } = harness();
    const signal = new AbortController().signal;
    const staged = request();
    const promise = adapter.stage({ request: staged, workspacePath, signal });
    staged.nativeContext.externalIdentity.resourceScope = "mutated after invocation";
    const value = await promise;
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].args, ["draft", "stage", "--stdin", "-o", "json"]);
    assert.equal(calls[0].exe, FAKE_TWIG);
    assert.equal(calls[0].options.shell, false);
    assert.equal(calls[0].options.timeout, undefined, "owned deadline terminates tree before a direct-child-only timeout");
    assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
    assert.equal(calls[0].options.signal, undefined, "abort is supervised until close rather than early execFile rejection");
    assert.equal(calls[0].options.cwd, workspacePath);
    assert.equal(calls[0].eof, true);
    assert.equal(calls[0].writes[0].encoding, "utf8");
    assert.deepEqual(JSON.parse(calls[0].writes[0].value), request());
    assert.equal(value.document.generation, "9007199254740995");
    assert.ok(Object.isFrozen(value.document.nativeContext));
});

test("each editing verb prepares its own capability on the admitted executable and spawns only the returned path", async () => {
    const executable = fakeExecutable();
    const { adapter, calls } = harness(result("opened"), 0, { executable });
    await adapter.open({ workItemId: 42, bench: "Sprint", workspacePath, expectedKey: context().itemKey, identityId: "identity" });
    assert.deepEqual(executable.prepared, ["draft-open"]);
    assert.equal(calls[0].exe, FAKE_TWIG);
    const staging = harness(result(), 0, { executable });
    await staging.adapter.stage({ request: request(), workspacePath });
    const retaining = harness(retainedResult(), 0, { executable });
    await retaining.adapter.retain({ request: retainRequest(), workspacePath });
    assert.deepEqual(executable.prepared, ["draft-open", "draft-stage", "draft-retain"]);
});

test("a refused capability or changed executable launches nothing and releases ownership for the next explicit verb", async () => {
    const refusal = Object.assign(new Error("Twig at C:\\fake-twig\\twig.exe changed since this canvas admitted it."), { code: "executable_changed" });
    const { adapter, calls } = harness(result(), 0, { executable: fakeExecutable({ refuse: refusal }) });
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), (error) => error === refusal);
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), (error) => error === refusal,
        "a second request reaches prepare again rather than finding the adapter busy");
    assert.equal(calls.length, 0);
});

test("an editing adapter without an admitted executable refuses before any launch", async () => {
    const calls = [];
    const adapter = createNativeEditingAdapter({ realpath: async (value) => value,
        launch() { calls.push("launch"); throw new Error("must not launch"); } });
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), { code: "twig_executable_unavailable" });
    assert.equal(calls.length, 0);
});

test("OPEN uses explicit Bench and native identity guards without invoking read-only inspector flags", async () => {
    const { adapter, calls } = harness(result("opened"));
    const value = await adapter.open({
        workItemId: 42, bench: "Sprint", workspacePath, expectedKey: context().itemKey, identityId: "identity",
    });
    assert.deepEqual(calls[0].args, [
        "draft", "open", "42", "--bench", "Sprint", "-o", "json",
        "--expect-binding", "binding", "--expect-identity", "identity",
    ]);
    assert.equal(calls[0].writes.length, 0);
    assert.equal(value.document.nativeContext.externalIdentity.resourceScope, "native-resource");
    assert.equal(value.status, "opened");
});

test("RETAIN sends only captured context and unchanged lossless counters with owned stdin EOF", async () => {
    const { adapter, calls } = harness(retainedResult());
    const input = retainRequest();
    const pending = adapter.retain({ request: input, workspacePath });
    input.nativeContext.identityId = "later mutation";
    const value = await pending;
    assert.deepEqual(calls[0].args, ["draft", "retain", "--stdin", "-o", "json"]);
    assert.deepEqual(JSON.parse(calls[0].writes[0].value), retainRequest());
    assert.equal(calls[0].eof, true);
    assert.equal(calls[0].options.shell, false);
    assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
    assert.equal(value.capture.generation, retainRequest().generation);
    assert.equal(value.capture.effects[0].proposedValue, null);
    assert.equal(value.capture.effects[1].proposedValue, "");
    assert.ok(Object.isFrozen(value.capture.effects[0]));
    assert.equal(calls.length, 1);
});

test("RETAIN requests reject effect, authority, path and lossy counter input before process launch", async () => {
    for (const patch of [{ changes: [] }, { digest: "a".repeat(64) }, { authorizer: "person" },
        { file: "proposal.json" }, { generation: 9007199254740992 }, { baselineRevision: "01" }]) {
        const { adapter, calls } = harness(retainedResult());
        await assert.rejects(adapter.retain({ request: { ...retainRequest(), ...patch }, workspacePath }));
        assert.equal(calls.length, 0);
    }
    const { adapter, calls } = harness(retainedResult());
    await assert.rejects(adapter.retain({ request: retainRequest(), workspacePath: "C:\\another" }), /checkout differs/);
    assert.equal(calls.length, 0);
    const large = retainRequest();
    large.nativeContext.benchName = "x".repeat(262144);
    assert.throws(() => serializeRetainRequest(large), /UTF-8 limit/);
});

test("RETAIN validates context, counters, effective nullable effects and canonical byte hash without authority", () => {
    for (const mutate of [
        (v) => { v.capture.nativeContext.identityId = "another"; },
        (v) => { v.capture.nativeContext.itemKey.bindingId = "another"; },
        (v) => { v.capture.generation = "9007199254740994"; },
        (v) => { v.capture.baselineRevision = "18"; },
        (v) => { v.capture.observedRevision = "18"; },
        (v) => { v.capture.currentRevision = "18"; },
        (v) => { v.capture.digest = "a".repeat(64); },
        (v) => { v.capture.canonicalJson += "\n"; },
        (v) => { v.capture.operationId = "draft-invalid"; },
        (v) => { v.capture.effects = []; },
        (v) => { v.capture.effects.push({ ...v.capture.effects[0], fieldRef: "custom.CLEAR" }); },
        (v) => { v.capture.effects[0].proposedValue = "previous"; },
        (v) => { delete v.capture.effects[0].baselineValue; },
        (v) => { v.capture.effects[0].proposedValue = 0; },
        (v) => { v.capture.authorized = true; },
        (v) => { v.capture.canApply = true; },
        (v) => { v.capture.originCoverage = "full-origin"; },
        (v) => { v.capture.fullOrigin = {}; },
        (v) => { v.capture.authorizer = "person"; },
        (v) => { v.capture.unexpected = true; },
        (v) => { v.document = document(); },
        (v) => { v.committed = false; },
        (v) => { v.status = "staged"; },
    ]) {
        const value = retainedResult();
        mutate(value);
        assert.throws(() => validateRetainResult(value, { exitCode: 0, expectedRequest: retainRequest() }), /unavailable/);
    }
});

test("RETAIN pure validation bounds canonical and aggregate effects before hash or deep clone", () => {
    for (const oversizedEffects of [false, true]) {
        const value = retainedResult();
        if (oversizedEffects) {
            value.capture.effects = Array.from({ length: 129 }, (_, index) => ({
                fieldRef: `Custom.Field${index}`, baselineValue: null, proposedValue: "x".repeat(32768),
            }));
        } else {
            value.capture.canonicalJson = "x".repeat(4 * 1024 * 1024);
        }
        value.capture.digest = "invalid-before-hashing";
        assert.throws(() => validateRetainResult(value, { exitCode: 0, expectedRequest: retainRequest() }),
            /adapter 4MiB UTF-8 envelope limit/);
    }
});

test("RETAIN opaque hash validation does not claim canonical semantic equivalence or require sorted effects", () => {
    const value = retainedResult();
    value.capture.effects.reverse();
    value.capture.canonicalJson = '{"opaqueNativeClaim":900719925474099312345}';
    value.capture.digest = createHash("sha256").update(value.capture.canonicalJson).digest("hex");
    const validated = validateRetainResult(value, { exitCode: 0, expectedRequest: retainRequest() });
    assert.deepEqual(validated, value);
    assert.notEqual(value.capture.operationId.slice(6), value.capture.digest);
    value.capture.effects = Array.from({ length: 129 }, (_, index) => ({
        fieldRef: `Custom.Field${index}`, baselineValue: null, proposedValue: "",
    }));
    assert.deepEqual(validateRetainResult(value, { exitCode: 0, expectedRequest: retainRequest() }), value);
});

test("RETAIN structured refusal and committed-indeterminate preserve exact evidence without retry", async () => {
    for (const [code, exit] of [["draft-empty", 1], ["canceled", 1], ["usage", 2], ["invalid-json", 2],
        ["input-too-large", 2]]) {
        const value = { version: 1, status: "refused", committed: false,
            error: { code, message: "Native refusal", recovery: "Do not retry" } };
        const { adapter, calls } = harness(value, exit);
        assert.deepEqual(await adapter.retain({ request: retainRequest(), workspacePath }), value);
        assert.equal(calls.length, 1);
    }
    for (const includeCapture of [true, false]) {
        const value = { version: 1, status: "indeterminate", committed: true,
            error: { code: "post-commit-fence", message: "Changed origin", recovery: "Inspect; no retry" } };
        if (includeCapture) value.capture = retainedResult().capture;
        const { adapter, calls } = harness(value, 1);
        assert.deepEqual(await adapter.retain({ request: retainRequest(), workspacePath }), value);
        assert.equal(calls.length, 1);
    }
    assert.throws(() => validateRetainResult({ version: 1, status: "indeterminate", committed: true,
        error: { code: "draft-empty", message: "Not a postcommit result", recovery: "Inspect" } },
    { exitCode: 1, expectedRequest: retainRequest() }), /unavailable/);
});

test("RETAIN malformed output, contradictory exit and stdin failure remain unknown", async () => {
    for (const [exit, options] of [[0, { raw: "" }], [0, { raw: "null" }], [0, { raw: "{" }],
        [1, {}], [0, { stdinError: true }], [1, { error: { killed: true } }]]) {
        const { adapter, calls } = harness(retainedResult(), exit, options);
        await assert.rejects(adapter.retain({ request: retainRequest(), workspacePath }),
            (error) => error instanceof NativeEditingTransportError && error.outcome === "unknown");
        assert.equal(calls.length, 1);
    }
});

test("OPEN and STAGE do not accept a RETAIN capture envelope", () => {
    for (const action of ["open", "stage"]) {
        assert.throws(() => validateEditingResult(retainedResult(), { action, exitCode: 0 }), /unavailable/);
    }
});

test("nonzero native refusals preserve structured errors rather than rejecting their valid stdout", async () => {
    for (const [code, exit] of [["draft-cas-conflict", 1], ["provider-unknown", 1], ["provider-unconfigured", 1],
        ["provider-unsupported", 1], ["invalid-json", 2]]) {
        const refused = { version: 1, status: "refused", committed: false,
            error: { code, message: "Native reason", recovery: "Explicit reopen; no retry" } };
        const { adapter, calls } = harness(refused, exit);
        const value = await adapter.stage({ request: request(), workspacePath });
        assert.deepEqual(value, refused);
        assert.equal(calls.length, 1, "no automatic retry");
    }
});

test("indeterminate committed results retain optional native document and never claim rollback", async () => {
    for (const includeDocument of [true, false]) {
        const value = { version: 1, status: "indeterminate", committed: true,
            error: { code: "post-commit-fence", message: "Origin changed", recovery: "Retain intent and reopen" } };
        if (includeDocument) value.document = document();
        const { adapter, calls } = harness(value, 1);
        assert.deepEqual(await adapter.stage({ request: request(), workspacePath }), value);
        assert.equal(calls.length, 1);
    }
});

test("invalid stdout, killed child, stdin failure and pre-command configuration errors have unknown outcome", async () => {
    for (const [exit, options] of [[1, { raw: "" }], [0, { raw: "not json" }],
        [1, { error: { killed: true } }], ["ABORT_ERR", {}], [0, { stdinError: true }],
        [1, { raw: "configuration failure before command" }]]) {
        const { adapter, calls } = harness(result(), exit, options);
        await assert.rejects(adapter.stage({ request: request(), workspacePath }), (error) =>
            error instanceof NativeEditingTransportError && error.outcome === "unknown" &&
            /do not retry/.test(error.message));
        assert.equal(calls.length, 1);
    }
});

test("contradictory exits, status, authority and same-visible-ID identity changes fail closed", () => {
    for (const mutate of [
        (value) => { value.committed = false; },
        (value) => { value.status = "opened"; },
        (value) => { value.document.authorized = true; },
        (value) => { value.document.generation = 9007199254740992; },
        (value) => { value.document.nativeContext.externalIdentity.resourceScope = "another-resource"; },
        (value) => { value.document.nativeContext.itemKey.connectionId = "other"; },
        (value) => { delete value.document.nativeContext.storageGeneration; },
        (value) => { value.document.extra = true; },
        (value) => { value.document.fields[1].reason = null; },
        (value) => { value.document.hasConflicts = true; },
    ]) {
        const value = result();
        mutate(value);
        assert.throws(() => validateEditingResult(value,
            { action: "stage", exitCode: 0, expectedContext: context() }), /Native editing contract/);
    }
    assert.throws(() => validateEditingResult(result(), { action: "stage", exitCode: 1 }), /exit/);
    assert.throws(() => validateEditingResult({
        version: 1, status: "refused", committed: false,
        error: { code: "usage", message: "Bad input", recovery: "Correct it" },
    }, { action: "stage", exitCode: 1 }), /exit/);
});

test("lossless Int64 decimal boundaries reject overflow, signs, whitespace and number coercion", () => {
    for (const value of ["0", "9007199254740993", "9223372036854775805"]) {
        const payload = request();
        payload.generation = value;
        assert.equal(JSON.parse(serializeStageRequest(payload)).generation, value);
    }
    for (const value of ["9223372036854775808", "01", "-1", "+1", " 1", "1.0", 1, null]) {
        const payload = request();
        payload.generation = value;
        assert.throws(() => serializeStageRequest(payload), /decimal/);
    }
});

test("stage acknowledgements bind exact revision and generation tokens to the submitted batch", () => {
    for (const mutate of [
        (value) => { value.document.generation = "9007199254740994"; },
        (value) => { value.document.generation = "9007199254740996"; },
        (value) => { value.document.baselineRevision = "16"; },
        (value) => { value.document.observedRevision = "18"; },
        (value) => { value.document.currentRevision = "18"; },
    ]) {
        const value = result();
        mutate(value);
        value.document.hasConflicts = value.document.baselineRevision !== value.document.observedRevision;
        assert.throws(() => validateEditingResult(value, {
            action: "stage", exitCode: 0, expectedRequest: request(), expectedContext: context(),
        }), /acknowledgement/);
    }
    const payload = request();
    payload.generation = "9223372036854775806";
    assert.throws(() => serializeStageRequest(payload), /overflow/);
});

test("OPEN revision drift alone is a conflict without inventing field conflicts", () => {
    const value = result("opened");
    value.document.observedRevision = value.document.currentRevision = "18";
    value.document.hasConflicts = true;
    assert.equal(validateEditingResult(value, { action: "open", exitCode: 0 }).document.hasConflicts, true);
    value.document.hasConflicts = false;
    assert.throws(() => validateEditingResult(value, { action: "open", exitCode: 0 }), /draft values/);
});

test("stage requires every nullable value, nonempty bounded ordered batch and unique canonical field references", () => {
    for (const mutate of [
        (value) => { delete value.changes[0].proposedValue; },
        (value) => { value.changes[0].proposedValue = undefined; },
        (value) => { value.changes[0].proposedValue = 1; },
        (value) => { value.changes.push({ fieldRef: "system.title", proposedValue: "duplicate" }); },
        (value) => { value.changes = []; },
        (value) => { value.changes = Array.from({ length: 129 }, (_, i) => ({ fieldRef: "Custom." + i, proposedValue: "" })); },
        (value) => { delete value.nativeContext.storageGeneration; },
        (value) => { value.nativeContext.extra = "not native"; },
    ]) {
        const payload = request();
        mutate(payload);
        assert.throws(() => serializeStageRequest(payload), /Native editing contract/);
    }
});

test("UTF-8 byte boundary counts multibyte content and never truncates payload", () => {
    const payload = request();
    payload.changes[0].proposedValue = "";
    const overhead = Buffer.byteLength(serializeStageRequest(payload), "utf8");
    payload.changes[0].proposedValue = "x".repeat(262144 - overhead);
    assert.equal(Buffer.byteLength(serializeStageRequest(payload), "utf8"), 262144);
    payload.changes[0].proposedValue += "x";
    assert.throws(() => serializeStageRequest(payload), /UTF-8 limit/);
    payload.changes[0].proposedValue = "\u00e9".repeat(Math.floor((262144 - overhead) / 2) + 1);
    assert.throws(() => serializeStageRequest(payload), /UTF-8 limit/);
});

test("malformed requests and wrong checkout never launch a child", async () => {
    const { adapter, calls } = harness();
    await assert.rejects(adapter.stage({ request: { ...request(), version: 2 }, workspacePath }), /unsupported/);
    await assert.rejects(adapter.stage({ request: request(), workspacePath: "C:\\other" }), /checkout/);
    await assert.rejects(adapter.open({ workItemId: 42, bench: "Sprint", workspacePath,
        expectedKey: context().itemKey }), /identity/);
    assert.equal(calls.length, 0);
    const native = context();
    delete native.externalIdentity.nativeId;
    assert.throws(() => validateEditingContext(native), /external identity/);
});

test("unwired adapter preserves native form order, unsupported controls and explicit value states", () => {
    const value = result();
    value.document.changes = [{ fieldRef: "System.Title", baselineValue: null, proposedValue: "" }];
    const accepted = validateEditingResult(value, { action: "stage", exitCode: 0 });
    assert.deepEqual(accepted.document.pages, form.pages);
    assert.deepEqual(accepted.document.changes, value.document.changes);
    const fields = accepted.document.pages[0].sections[0].groups[0].controls;
    assert.equal(fields[1].value.state, 1);
    assert.equal(fields[2].value.state, 2);
    assert.equal(fields[3].isContribution, true);
});

test("pre-aborted operation never resolves a checkout or launches a child", async () => {
    let touched = false;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async () => { touched = true; },
        launch: () => { touched = true; },
    });
    const controller = new AbortController();
    controller.abort();
    await assert.rejects(adapter.stage({ request: request(), workspacePath, signal: controller.signal }), /abort/i);
    await assert.rejects(adapter.open({ signal: controller.signal }), /abort/i);
    assert.equal(touched, false);
});

test("early abort callback does not settle ownership until child close and tree termination finish", async () => {
    let finishStop;
    let nativeCallback;
    let child;
    let calls = 0;
    let stops = 0;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        stopTree: async () => { stops++; await new Promise((resolve) => { finishStop = resolve; }); },
        launch(exe, args, options, callback) {
            calls++;
            nativeCallback = callback;
            child = new EventEmitter();
            child.pid = 123;
            child.stdin = new EventEmitter();
            child.stdin.end = () => {};
            return child;
        },
    });
    const controller = new AbortController();
    let settled = false;
    const running = adapter.stage({ request: request(), workspacePath, signal: controller.signal });
    const rejected = assert.rejects(running, NativeEditingTransportError).then(() => { settled = true; });
    await new Promise((resolve) => setImmediate(resolve));
    controller.abort();
    nativeCallback(Object.assign(new Error("early abort"), { code: "ABORT_ERR" }), "");
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(stops, 1);
    assert.equal(settled, false);
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), /ownership/);
    assert.equal(calls, 1);
    child.emit("close", null, "SIGTERM");
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(settled, false, "tree cleanup must finish as well as child close");
    finishStop();
    await rejected;
});

test("RETAIN cancellation holds shared OPEN/STAGE ownership until close and cleanup settle", async () => {
    let child, callback, finishStop;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        stopTree: () => new Promise((resolve) => { finishStop = resolve; }),
        launch(exe, args, options, done) {
            callback = done;
            child = new EventEmitter();
            child.pid = 123;
            child.stdin = new EventEmitter();
            child.stdin.end = () => {};
            return child;
        },
    });
    const controller = new AbortController();
    let settled = false;
    const running = adapter.retain({ request: retainRequest(), workspacePath, signal: controller.signal });
    const rejection = assert.rejects(running, NativeEditingTransportError).then(() => { settled = true; });
    await new Promise((resolve) => setImmediate(resolve));
    controller.abort();
    callback(Object.assign(new Error("early abort"), { code: "ABORT_ERR" }), "");
    await new Promise((resolve) => setImmediate(resolve));
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), /ownership/);
    await assert.rejects(adapter.retain({ request: retainRequest(), workspacePath }), /ownership/);
    assert.equal(settled, false);
    child.emit("close", null, "SIGTERM");
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(settled, false);
    finishStop();
    await rejection;
});

test("stdin error requests owned-tree termination and still waits for close", async () => {
    let child;
    let callback;
    let stopped = false;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        stopTree: async () => { stopped = true; },
        launch(exe, args, options, done) {
            callback = done;
            child = new EventEmitter();
            child.pid = 123;
            child.stdin = new EventEmitter();
            child.stdin.end = () => {};
            return child;
        },
    });
    const running = adapter.stage({ request: request(), workspacePath });
    const rejected = assert.rejects(running, NativeEditingTransportError);
    await new Promise((resolve) => setImmediate(resolve));
    child.stdin.emit("error", new Error("broken pipe"));
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(stopped, true);
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), /ownership/);
    callback(null, JSON.stringify(result()));
    child.emit("close", 0, null);
    await rejected;
});

test("failed owned-tree cleanup poisons the adapter instead of admitting a replacement child", async () => {
    let child;
    let callback;
    let calls = 0;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        stopTree: async () => { throw new Error("Tree closure not proven"); },
        launch(exe, args, options, done) {
            calls++;
            callback = done;
            child = new EventEmitter();
            child.pid = 123;
            child.stdin = new EventEmitter();
            child.stdin.end = () => {};
            return child;
        },
    });
    const controller = new AbortController();
    const running = adapter.stage({ request: request(), workspacePath, signal: controller.signal });
    const rejected = assert.rejects(running, NativeEditingTransportError);
    await new Promise((resolve) => setImmediate(resolve));
    controller.abort();
    callback(Object.assign(new Error("abort"), { code: "ABORT_ERR" }), "");
    child.emit("close", null, "SIGKILL");
    await rejected;
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), /ownership/);
    assert.equal(calls, 1);
});

test("editing wiring remains explicit; only a separate trusted browser Save can consume retained intent", async () => {
    const extension = await readFile(new URL("../extension.mjs", import.meta.url), "utf8");
    assert.match(extension, /nativeEditing: createNativeEditingAdapter\(\{ executable \}\)/);
    const bridge = await readFile(new URL("../editing-bridge.mjs", import.meta.url), "utf8");
    assert.match(bridge, /adapter\.open\(/);
    assert.match(bridge, /adapter\.stage\(/);
    for (const name of ["extension.mjs", "canvas-server.mjs", "render.mjs", "workbench-client.mjs", "editing-client.mjs", "editing-bridge.mjs"]) {
        const source = await readFile(new URL("../" + name, import.meta.url), "utf8");
        assert.doesNotMatch(source, /adapter\.(?:apply|publish)\s*\(|action:\s*["'](?:apply|publish)["']/);
        if (name !== "editing-bridge.mjs") assert.doesNotMatch(source, /adapter\.save\s*\(/);
    }
    const workbenchActions = extension.slice(extension.indexOf("const workbenchCanvas"), extension.indexOf("const reviewCanvas"));
    assert.doesNotMatch(workbenchActions, /name:\s*["'][^"']*(?:save|apply|publish)[^"']*["']/i);
    const client = await readFile(new URL("../editing-client.mjs", import.meta.url), "utf8");
    assert.match(client, /open\.addEventListener\("click", \(\) => void perform\("open"\)\)/);
    assert.match(client, /stage\.addEventListener\("click", \(\) => void perform\("stage"\)\)/);
    assert.match(client, /save\.addEventListener\("click", event =>/);
    assert.match(client, /event\?\.isTrusted !== true/);
    assert.doesNotMatch(bridge, /body\.authorization|validateSaveAuthorization/);
    assert.doesNotMatch(client, /innerHTML|setInterval|setTimeout/);
});

test("either output stream error requests termination and yields an unknown outcome", async () => {
    for (const name of ["stdout", "stderr"]) {
        let child;
        let stopped = false;
        const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
            realpath: async (value) => value,
            stopTree: async () => { stopped = true; },
            spawn() {
                child = new EventEmitter();
                child.pid = 123;
                for (const stream of ["stdin", "stdout", "stderr"]) child[stream] = new EventEmitter();
                child.stdin.end = () => {};
                return child;
            },
        });
        const running = adapter.stage({ request: request(), workspacePath });
        const rejected = assert.rejects(running, NativeEditingTransportError);
        await new Promise((resolve) => setImmediate(resolve));
        child[name].emit("error", new Error("stream unavailable"));
        await new Promise((resolve) => setImmediate(resolve));
        assert.equal(stopped, true);
        child.emit("close", 0, null);
        await rejected;
    }
});

test("combined output bound trips on stderr without retaining or parsing partial success", async () => {
    let child;
    let stopped = false;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        stopTree: async () => { stopped = true; },
        spawn() {
            child = new EventEmitter();
            child.pid = 123;
            for (const stream of ["stdin", "stdout", "stderr"]) child[stream] = new EventEmitter();
            child.stdin.end = () => {};
            return child;
        },
    });
    const running = adapter.stage({ request: request(), workspacePath });
    const rejected = assert.rejects(running, NativeEditingTransportError);
    await new Promise((resolve) => setImmediate(resolve));
    child.stdout.emit("data", Buffer.from(JSON.stringify(result()), "utf8"));
    child.stderr.emit("data", Buffer.alloc(4 * 1024 * 1024));
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(stopped, true);
    child.emit("close", 0, null);
    await rejected;
});

test("overflow with an already-exited root remains unknown and poisons reuse when tree cleanup is unproven", async () => {
    let calls = 0;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        stopTree: async () => { throw new Error("Root PID exited; descendant closure is unproven"); },
        launch(exe, args, options, callback) {
            calls++;
            const child = new EventEmitter();
            child.pid = 123;
            child.stdin = new EventEmitter();
            child.stdin.end = () => {};
            queueMicrotask(() => {
                callback(Object.assign(new Error("stdout maxBuffer exceeded"), {
                    code: "ERR_CHILD_PROCESS_STDIO_MAXBUFFER", killed: true,
                }), "{}");
                child.emit("close", null, "SIGTERM");
            });
            return child;
        },
    });
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), (error) =>
        error instanceof NativeEditingTransportError && error.outcome === "unknown");
    await assert.rejects(adapter.stage({ request: request(), workspacePath }), /ownership/);
    assert.equal(calls, 1);
});

for (const mode of ["cancel", "overflow"]) {
test("real owned process " + mode + " closes its inherited-pipe descendant before reuse", { timeout: 25000 }, async () => {
    const directory = await mkdtemp(join(tmpdir(), "twig-editing-owned-"));
    const receiptPath = join(directory, "owned-pids.json");
    const program = `
        const { spawn } = require("node:child_process");
        const { writeFileSync } = require("node:fs");
        const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "inherit" });
        writeFileSync(${JSON.stringify(receiptPath)}, JSON.stringify([process.pid, child.pid]));
        ${mode === "overflow" ? `setTimeout(() => {
            const chunk = Buffer.alloc(65536, 120);
            setInterval(() => { process.stdout.write(chunk); process.stderr.write(chunk); }, 1);
        }, 300);` : ""}
        setInterval(() => {}, 1000);
    `;
    const controller = new AbortController();
    let ids = [];
    let launches = 0;
    const adapter = createNativeEditingAdapter({ executable: fakeExecutable(),
        realpath: async (value) => value,
        spawn(exe, args, options) {
            launches++;
            const source = launches === 1 ? program : `process.stdout.write(${JSON.stringify(JSON.stringify(result()))})`;
            return nativeSpawn(process.execPath, ["-e", source], { ...options, cwd: directory });
        },
    });
    const running = adapter.stage({ request: request(), workspacePath, signal: controller.signal });
    const rejected = assert.rejects(running, (error) => error instanceof NativeEditingTransportError &&
        error.outcome === "unknown");
    const alive = (pid) => {
        try { process.kill(pid, 0); return true; }
        catch (error) { if (error.code === "ESRCH") return false; throw error; }
    };
    try {
        for (let attempt = 0; attempt < 200; attempt++) {
            try { ids = JSON.parse(await readFile(receiptPath, "utf8")); break; }
            catch (error) { if (error.code !== "ENOENT") throw error; }
            await new Promise((resolve) => setTimeout(resolve, 10));
        }
        assert.equal(ids.length, 2, "real parent and inherited-pipe descendant must both start");
        assert.ok(ids.every((pid) => Number.isInteger(pid) && pid > 0 && alive(pid)));
        if (mode === "cancel") controller.abort();
        await rejected;
        for (let attempt = 0; attempt < 100 && ids.some(alive); attempt++) {
            await new Promise((resolve) => setTimeout(resolve, 10));
        }
        assert.ok(ids.every((pid) => !alive(pid)), "owned parent and descendant must terminate, not merely fire an abort callback");
        assert.equal(launches, 1);
        assert.equal((await adapter.stage({ request: request(), workspacePath })).status, "staged");
        assert.equal(launches, 2, "reuse is admitted only after proven cleanup");
    } finally {
        controller.abort();
        await rejected;
        for (const pid of ids) {
            if (Number.isInteger(pid) && pid > 0 && alive(pid)) process.kill(pid, "SIGKILL");
        }
        await rm(receiptPath, { force: true });
        await rmdir(directory);
    }
});
}
