import assert from "node:assert/strict";
import test from "node:test";
import { retainProposalReview, mountRetainedReview } from "../retained-review.mjs";

// Fictional native wire shape from PlanCommand.RenderPreview and ChangeProposalReviewModelJson.
// This is not a real capture or evidence of native or host authentication.
const hash = "a".repeat(64);
const workspace = { organization: "fictional-org", project: "fictional-project" };
const expected = {
    captureId: "capture-1", expectedCaptureId: "capture-1", expectedModelVersion: 1,
    digest: hash, workspace, sourceKind: "host-supplied-native-preview",
};
function fixture() {
    const kinds = ["Batch", "AddLink", "RemoveLink", "PublishSeed", "Delete"];
    const wires = ["batch", "add-link", "remove-link", "publish-seed", "delete"];
    const effects = [
        [
            { kind: "field-set", field: "System.Description", to: "<script>untrusted()</script>" + "x".repeat(300),
                relation: null, otherId: null, before: { state: "unknown", value: null, source: "cache", revision: 3, reason: "revision-mismatch" } },
            { kind: "field-clear", field: "Custom.Clear", to: null, relation: null, otherId: null },
            { kind: "field-set", field: "Custom.Empty", to: "", relation: null, otherId: null,
                before: { state: "absent", value: null, source: "cache", revision: 3, reason: null } },
        ],
        [{ kind: "link-add", relation: "Related", otherId: 43, field: null, to: null }],
        [{ kind: "link-remove", relation: "Related", otherId: 44, field: null, to: null }],
        [{ kind: "seed-publish", field: null, to: null, relation: null, otherId: null }],
        [{ kind: "work-item-delete", otherId: 42, field: null, to: null, relation: null }],
    ];
    return {
        digest: hash, canApply: false,
        issues: [{ code: "fixture-warning", path: "", message: "Visible issue" }],
        pendingChanges: [{ kind: "field", field: "System.Title", value: "local intent" }],
        operations: kinds.map((kind, ordinal) => ({ ordinal, id: `op-${ordinal}`, kind })),
        reviewModel: {
            model: "twig.change-proposal.review", modelVersion: 1, digest: hash, workspace: { ...workspace },
            rationale: "Fictional review", recipe: null,
            affectedItems: [{ id: 42, title: "Untrusted <img src=x>", type: "Fixture", state: "Actual wording",
                role: "target", url: "javascript:untrusted()", parentId: null, revision: 3 }],
            contextItems: [],
            operations: wires.map((kind, ordinal) => ({
                ordinal, opId: `op-${ordinal}`, kind,
                target: { workItemId: kind === "publish-seed" ? null : 42,
                    stagedIdentity: kind === "publish-seed" ? "seed-fixture" : null, seed: null },
                summary: `Exact ${kind}`, consequences: effects[ordinal],
                preconditions: [{ kind: kind === "publish-seed" ? "expectedFingerprint" : "expectedRevision",
                    value: kind === "publish-seed" ? "fixture-fingerprint" : "3" }],
            })),
            authorizationChoices: ["cancel"],
            blockers: [{ kind: "pending", workItemId: 42, detail: "Pending local intent" }],
            policyDecisions: [{ ruleId: "fixture-rule", kind: "fixture", outcome: "Unknown",
                blocking: true, reason: "Not evaluated", workItemId: 42, opId: "op-0" }],
        },
    };
}
const capture = (value = fixture(), expectation = expected) => retainProposalReview(JSON.stringify(value), expectation);

test("retains all native operations and exact values without authenticating source or gaining authority", () => {
    const input = fixture();
    input.canApply = true;
    input.reviewModel.authorizationChoices = ["apply"];
    input.additive = { future: "retained" };
    const retained = capture(input);
    assert.deepEqual(retained.envelope, input);
    assert.match(retained.provenance, /authenticity is not verified/);
    assert.ok(Object.isFrozen(retained.envelope.reviewModel.operations[0].consequences[0]));
    input.reviewModel.operations.pop();
    assert.equal(retained.envelope.reviewModel.operations.length, 5);
    assert.equal(retained.envelope.reviewModel.operations[0].consequences[1].to, null);
    assert.equal(retained.envelope.reviewModel.operations[0].consequences[2].to, "");
});

test("missing expected capture identity/version/source or mismatched reported identity refuses", () => {
    for (const key of Object.keys(expected)) {
        const e = { ...expected };
        delete e[key];
        assert.throws(() => capture(fixture(), e), /unavailable/);
    }
    for (const e of [
        { ...expected, expectedCaptureId: "another" },
        { ...expected, expectedModelVersion: 2 },
        { ...expected, digest: "b".repeat(64) },
        { ...expected, workspace: { ...workspace, project: "elsewhere" } },
        { ...expected, sourceKind: "authenticated" },
    ]) assert.throws(() => capture(fixture(), e), /unavailable/);
});

test("malformed, unsupported and structurally truncated envelopes refuse instead of partial review", () => {
    assert.throws(() => retainProposalReview('{"digest":', expected), /truncated JSON/);
    assert.throws(() => retainProposalReview("x".repeat(4 * 1024 * 1024 + 1), expected), /oversized/);
    const deep = fixture();
    let cursor = deep;
    for (let i = 0; i < 65; i++) { cursor.extra = {}; cursor = cursor.extra; }
    assert.throws(() => capture(deep), /nesting/);
    for (const mutate of [
        (v) => { v.reviewModel.modelVersion = 2; },
        (v) => { v.reviewModel.digest = "b".repeat(64); },
        (v) => { v.reviewModel.operations.pop(); },
        (v) => { v.reviewModel.operations.reverse(); },
        (v) => { v.reviewModel.operations[1].opId = "op-0"; },
        (v) => { v.reviewModel.operations[0].consequences = []; },
        (v) => { v.reviewModel.operations[0].preconditions = []; },
        (v) => { v.reviewModel.operations[0].consequences[0].to = null; },
        (v) => { v.reviewModel.operations[0].consequences[1].to = ""; },
        (v) => { v.reviewModel.operations[0].consequences[0].before.state = "absent"; v.reviewModel.operations[0].consequences[0].before.value = "not absent"; },
        (v) => { v.reviewModel.operations[1].consequences[0].otherId = null; },
        (v) => { v.reviewModel.operations[4].consequences[0].otherId = 99; },
        (v) => { delete v.reviewModel.blockers; },
        (v) => { delete v.pendingChanges; },
        (v) => { delete v.reviewModel.authorizationChoices; },
        (v) => { v.reviewModel.providerAffectedItems = []; },
        (v) => { v.reviewModel.operations[0].target.providerWorkItem = { localId: "opaque" }; },
        (v) => { v.reviewModel.operations[1].consequences[0].otherProviderWorkItem = { localId: "opaque" }; },
        (v) => { v.reviewModel.operations[0].preconditions[0].kind = "expectedProviderRevision"; },
    ]) {
        const v = fixture();
        mutate(v);
        assert.throws(() => capture(v), /unavailable/);
    }
});

test("published targets, link peers and deletion IDs reject zero, negative and overflow values", () => {
    for (const invalid of [0, -42, 2147483648, -2147483648, 1.5]) {
        for (const ordinal of [0, 1, 2, 4]) {
            const value = fixture();
            value.reviewModel.operations[ordinal].target.workItemId = invalid;
            if (ordinal === 4) value.reviewModel.operations[ordinal].consequences[0].otherId = invalid;
            assert.throws(() => capture(value), /unavailable/, `target ${ordinal}: ${invalid}`);
        }
        for (const ordinal of [1, 2, 4]) {
            const value = fixture();
            value.reviewModel.operations[ordinal].consequences[0].otherId = invalid;
            assert.throws(() => capture(value), /unavailable/, `peer ${ordinal}: ${invalid}`);
        }
    }
    const context = fixture();
    context.reviewModel.contextItems = [{ ...context.reviewModel.affectedItems[0], id: -42, role: "context" }];
    assert.equal(capture(context).envelope.reviewModel.contextItems[0].id, -42);
});

test("canonical native digests must be lowercase even when every reported copy agrees", () => {
    const value = fixture();
    value.digest = value.reviewModel.digest = hash.toUpperCase();
    assert.throws(() => capture(value, { ...expected, digest: hash.toUpperCase() }), /unavailable/);
});

function dom() {
    const doc = { createElement: (tag) => new Element(tag) };
    class Element {
        constructor(tag) { this.tag = tag; this.children = []; this.listeners = {}; this.ownerDocument = doc; this.textContent = ""; }
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this.children = children; }
        setAttribute(key, value) { this[key] = value; }
        addEventListener(key, fn) { this.listeners[key] = fn; }
        set innerHTML(_) { throw new Error("Untrusted HTML must never be interpreted"); }
    }
    return new Element("root");
}
const flatten = (e) => [e.textContent, ...e.children.map(flatten)].join("\n");
const all = (e) => [e, ...e.children.flatMap(all)];

test("brief preserves long titles and custom scalar values but defers only description bodies", () => {
    const root = dom();
    const value = fixture();
    const effects = value.reviewModel.operations[0].consequences;
    const title = "Long-title-" + "t".repeat(300);
    const scalar = "Custom-scalar-" + "c".repeat(300);
    effects.push(
        { kind: "field-set", field: "System.Title", to: title, relation: null, otherId: null },
        { kind: "field-set", field: "Custom.Scalar", fieldType: "String", to: scalar, relation: null, otherId: null },
    );
    effects[0].field = "system.DESCRIPTION";
    mountRetainedReview(root, JSON.stringify(value), expected);
    const brief = flatten(root);
    assert.ok(brief.includes(title));
    assert.ok(brief.includes(scalar));
    assert.doesNotMatch(brief, /<script>untrusted\(\)<\/script>/);
    assert.match(brief, /Exact requested value retained/);
    all(root).find((e) => e.tag === "button" && e.textContent === "Details").listeners.click();
    assert.ok(flatten(root).includes(effects[0].to));
    all(root).find((e) => e.tag === "button" && e.textContent === "Back").listeners.click();
    assert.equal(flatten(root), brief);
});

test("brief/details/back stay on one frozen capture and inert content; only view controls exist", () => {
    const root = dom();
    const value = fixture();
    const view = mountRetainedReview(root, JSON.stringify(value), expected);
    assert.equal(view.available, true);
    const brief = flatten(root);
    assert.match(brief, /Pending local intent/);
    assert.match(brief, /Visible issue/);
    assert.match(brief, /work-item-delete/);
    assert.match(brief, /field-clear/);
    assert.match(brief, /Exact requested value retained/);
    assert.doesNotMatch(brief, /untrusted\(\)<\/script>/);
    assert.match(brief, /javascript:untrusted/); // Text, never href.
    const buttons = all(root).filter((e) => e.tag === "button");
    assert.deepEqual(buttons.map((e) => e.textContent), ["Details", "Back"]);
    buttons[0].listeners.click();
    assert.match(flatten(root), /<script>untrusted\(\)<\/script>/);
    assert.match(flatten(root), /"canApply": false/);
    assert.match(flatten(root), /"authorizationChoices"/);
    assert.equal(view.retained.captureId, "capture-1");
    buttons[1].listeners.click();
    assert.equal(flatten(root), brief);
    assert.equal(all(root).some((e) => e.tag === "a" || e.tag === "script" || e.tag === "iframe"), false);
});

test("invalid replacement clears old review and exposes unavailable alert, not prior success", () => {
    const root = dom();
    mountRetainedReview(root, JSON.stringify(fixture()), expected);
    const result = mountRetainedReview(root, "{}", expected);
    assert.equal(result.available, false);
    assert.match(flatten(root), /unavailable/);
    assert.equal(root.children[0].role, "alert");
    assert.equal(all(root).filter((e) => e.tag === "button").length, 0);
});
