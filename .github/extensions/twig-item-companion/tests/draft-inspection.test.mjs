import assert from "node:assert/strict";
import test from "node:test";
import { createNativeDraftReader, validateDraftDocument } from "../native-draft.mjs";
import { renderDraftInspection } from "../draft-inspection.mjs";

// Fictional flat root matching e735705d DraftCommand.AddDocumentFields, not native validation evidence.
const checkout = process.cwd();
const key = { checkoutRoot: checkout, connectionId: "fixture-connection", bindingId: "fixture-binding", workItemId: 42 };
function fixture() {
    return {
        draftDocumentVersion: 1, itemKey: { ...key },
        baselineRevision: 3, observedRevision: 3, currentRevision: 3, generation: 1,
        draftStatus: "staged", status: "current", reconciliationState: "current",
        hasConflicts: false, localPendingWork: false, freshness: "cached",
        observationCompleteness: "complete-for-draft-effects",
        changes: [{ fieldRef: "System.Title", baselineValue: null, proposedValue: "" }],
        conflicts: [],
        fields: [{ fieldRef: "System.Title", baselineValue: null, observedValue: null,
            observedValueAvailable: true, proposedValue: "", nativePersistable: true,
            nativeCapabilitySource: "native-edit-sink", eligibilityReason: null, matchesBaseline: true }],
        eligibility: { fieldsPersistable: true, rules: "not-applicable", rulesReason: null,
            serverIsFinalAuthority: true, authorized: false, inspectionOnly: true },
    };
}
function reader(payload = fixture(), calls = []) {
    return createNativeDraftReader({
        realpath: async (path) => path,
        execFile(exe, args, options, callback) {
            calls.push({ exe, args, options });
            if (payload instanceof Error) callback(payload, "", payload.stderr);
            else callback(null, typeof payload === "string" ? payload : JSON.stringify(payload), "");
        },
    });
}
const read = (r, expectedKey = key) => r({ workItemId: 42, workspacePath: checkout, expectedKey });

test("explicit native inspection preserves string/null values with bounded fake process and no mutation verbs", async () => {
    const calls = [];
    const controller = new AbortController();
    const result = await reader(fixture(), calls)({
        workItemId: 42, workspacePath: checkout, expectedKey: key, signal: controller.signal,
    });
    assert.equal(result.changes[0].baselineValue, null);
    assert.equal(result.changes[0].proposedValue, "");
    assert.deepEqual(calls[0].args, ["draft", "show", "42", "--document", "-o", "json"]);
    assert.equal(calls[0].exe, "twig");
    assert.equal(calls[0].options.shell, false);
    assert.equal(calls[0].options.timeout, 15000);
    assert.equal(calls[0].options.maxBuffer, 4 * 1024 * 1024);
    assert.equal(calls[0].options.signal, controller.signal);
    assert.ok(!calls[0].args.some((v) => ["set", "--sync", "capture", "apply", "save"].includes(v)));
});

test("missing draft and native admission refusals remain visible, never become empty success", async () => {
    for (const message of ["No recoverable draft exists for work item #42.", "Native origin changed."]) {
        const failure = Object.assign(new Error("exit 1"), { stderr: JSON.stringify({ error: message }) });
        await assert.rejects(read(reader(failure)), (e) => e.message === message);
    }
    await assert.rejects(read(reader("{")), /malformed JSON/);
    await assert.rejects(read(reader({ draftDocument: fixture() })), /version/);
});

test("key correlation covers connection, binding, item and canonical checkout, not a fabricated Bench origin", async () => {
    for (const part of ["connectionId", "bindingId", "workItemId", "checkoutRoot"]) {
        const doc = fixture();
        doc.itemKey[part] = part === "workItemId" ? 43 : `${doc.itemKey[part]}-other`;
        await assert.rejects(read(reader(doc)), /identity changed/);
    }
    const calls = [];
    await assert.rejects(read(reader(fixture(), calls), { ...key, workItemId: 0 }), /qualified key/);
    assert.equal(calls.length, 0);
});

test("unsafe or missing generation and every native revision refuse instead of rounding", () => {
    for (const name of ["generation", "baselineRevision", "observedRevision", "currentRevision"]) {
        for (const invalid of [undefined, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, "3"]) {
            const doc = fixture();
            doc[name] = invalid;
            assert.throws(() => validateDraftDocument(doc, key, checkout), /numeric generation\/revision/);
        }
    }
});

test("ordinal item-key checkout identity refuses a path-equivalent alias", async () => {
    const doc = fixture();
    doc.itemKey.checkoutRoot = checkout + (process.platform === "win32" ? "\\." : "/.");
    assert.notEqual(doc.itemKey.checkoutRoot, key.checkoutRoot);
    await assert.rejects(read(reader(doc)), /qualified item identity changed/);
});

test("native revision drift and retained conflicts exactly determine hasConflicts", () => {
    const drift = fixture();
    drift.observedRevision = drift.currentRevision = 4;
    assert.notEqual(drift.baselineRevision, drift.observedRevision);
    assert.throws(() => validateDraftDocument(drift, key, checkout), /conflict flag contradicts/);
    drift.hasConflicts = true;
    drift.status = drift.reconciliationState = "conflicted";
    assert.equal(validateDraftDocument(drift, key, checkout).hasConflicts, true);
    const mismatch = fixture();
    mismatch.currentRevision = 4;
    assert.throws(() => validateDraftDocument(mismatch, key, checkout), /observed and current/);
    const falseConflict = fixture();
    falseConflict.hasConflicts = true;
    falseConflict.status = falseConflict.reconciliationState = "conflicted";
    assert.throws(() => validateDraftDocument(falseConflict, key, checkout), /conflict flag contradicts/);
});

test("field references correlate case-insensitively without rewriting retained intent", () => {
    const doc = fixture();
    doc.hasConflicts = true;
    doc.status = doc.reconciliationState = "conflicted";
    doc.fields[0].fieldRef = "SYSTEM.TITLE";
    doc.conflicts = [{ fieldRef: "system.title", baselineValue: null, observedValue: "prior", proposedValue: "" }];
    assert.equal(validateDraftDocument(doc, key, checkout).conflicts[0].fieldRef, "system.title");
    assert.equal(doc.changes[0].fieldRef, "System.Title");
    for (const collection of ["changes", "fields", "conflicts"]) {
        const duplicate = structuredClone(doc);
        duplicate[collection].push({ ...duplicate[collection][0], fieldRef: "sYsTeM.tItLe" });
        assert.throws(() => validateDraftDocument(duplicate, key, checkout), /unavailable/);
    }
});

test("empty draft, conflict drift and blocked local work remain distinct inspection evidence", () => {
    const empty = fixture();
    empty.draftStatus = "empty";
    empty.changes = empty.fields = [];
    assert.equal(validateDraftDocument(empty, key, checkout).draftStatus, "empty");
    const conflict = fixture();
    conflict.hasConflicts = true;
    conflict.status = conflict.reconciliationState = "conflicted";
    conflict.observedRevision = conflict.currentRevision = 4;
    conflict.generation = 2;
    conflict.conflicts = [{ fieldRef: "System.Title", baselineValue: null, observedValue: "remote", proposedValue: "" }];
    conflict.fields[0].observedValue = "remote";
    conflict.fields[0].matchesBaseline = false;
    assert.equal(validateDraftDocument(conflict, key, checkout).generation, 2);
    const blocked = fixture();
    blocked.localPendingWork = true;
    blocked.status = blocked.reconciliationState = "blocked-local-work";
    blocked.observationCompleteness = "blocked-by-local-work";
    blocked.fields[0].observedValueAvailable = blocked.fields[0].matchesBaseline = false;
    assert.equal(validateDraftDocument(blocked, key, checkout).fields[0].observedValueAvailable, false);
});

test("missing fields, arbitrary scalars and unexpected authority flags fail closed", () => {
    for (const mutate of [
        (v) => { v.draftDocumentVersion = 2; },
        (v) => { v.fields = []; },
        (v) => { delete v.fields[0].observedValueAvailable; },
        (v) => { v.fields[0].proposedValue = false; },
        (v) => { v.fields[0].nativeCapabilitySource = "host"; },
        (v) => { v.fields[0].observedValueAvailable = false; v.fields[0].observedValue = "unknown"; },
        (v) => { v.fields[0].matchesBaseline = true; v.fields[0].observedValue = "different"; },
        (v) => { v.eligibility.authorized = true; },
        (v) => { v.eligibility.inspectionOnly = false; },
        (v) => { v.eligibility.serverIsFinalAuthority = false; },
        (v) => { delete v.eligibility.rulesReason; },
        (v) => { delete v.changes; },
        (v) => { v.status = "saved"; },
    ]) {
        const doc = fixture();
        mutate(doc);
        assert.throws(() => validateDraftDocument(doc, key, checkout), /unavailable/);
    }
});

function dom() {
    const doc = { createElement: (tag) => new Element(tag) };
    class Element {
        constructor(tag) { this.tag = tag; this.children = []; this.ownerDocument = doc; this.textContent = ""; }
        replaceChildren(...children) { this.children = children; }
        append(...children) { this.children.push(...children); }
        setAttribute(key, value) { this[key] = value; }
        set innerHTML(_) { throw new Error("Never interpret draft values as HTML"); }
    }
    return new Element("root");
}
const all = (node) => [node, ...node.children.flatMap(all)];
const flatten = (node) => all(node).map((v) => v.textContent).join("\n");

test("renderer is inert and buttonless, discloses metadata CAS and independently captured observations", () => {
    const root = dom();
    const doc = fixture();
    const proposed = "<script>alert('untrusted')</script>";
    doc.changes[0].proposedValue = doc.fields[0].proposedValue = proposed;
    assert.equal(renderDraftInspection(root, doc, key).available, true);
    assert.ok(flatten(root).includes(proposed));
    assert.match(flatten(root), /Independently captured/);
    assert.match(flatten(root), /not established/);
    assert.match(flatten(root), /not zero-write/);
    assert.match(flatten(root), /no offline guarantee/);
    assert.match(flatten(root), /Draft CAS generation/);
    assert.equal(all(root).some((v) => ["button", "input", "a", "script"].includes(v.tag)), false);
    const bad = fixture();
    bad.eligibility.authorized = true;
    assert.equal(renderDraftInspection(root, bad, key).available, false);
    assert.equal(root.children[0].role, "alert");
    assert.doesNotMatch(flatten(root), /Exact retained changes/);
});
