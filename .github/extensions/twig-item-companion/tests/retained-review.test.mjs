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
        { ...expected, expectedModelVersion: 3 },
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
        (v) => { v.reviewModel.modelVersion = 3; },
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
        (v) => { v.reviewModel.providerAffectedItems = {}; },
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
    assert.doesNotMatch(flatten(root), /authorizationChoices|authorization choice/i);
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

const providerRef = (nativeId, resourceScope = "scope/<opaque>", providerKind = "fixture-provider") => ({
    localId: `local-${nativeId}`, externalIdentity: { providerKind, resourceScope, nativeId },
});

function providerFixture() {
    const value = fixture();
    const target = providerRef("900719925474099312345");
    const peer = providerRef("peer/<script>untrusted()</script>");
    const kinds = ["batch", "add-link", "remove-link", "delete"];
    value.operations = ["Batch", "AddLink", "RemoveLink", "Delete"].map((kind, ordinal) =>
        ({ ordinal, id: `provider-${ordinal}`, kind }));
    value.reviewModel.affectedItems = [];
    value.reviewModel.providerAffectedItems = [{ item: target, role: "target" }, { item: peer, role: "peer" }];
    value.reviewModel.operations = kinds.map((kind, ordinal) => ({
        ordinal, opId: `provider-${ordinal}`, kind,
        target: { workItemId: null, stagedIdentity: null, providerWorkItem: target },
        summary: `Provider ${kind}`,
        preconditions: [{ kind: "expectedProviderRevision", value: 'W/"opaque-900719925474099312345"' }],
        consequences: kind === "batch" ? [
            { kind: "field-set", field: "Custom.Value", to: "", relation: null, otherId: null },
            { kind: "field-clear", field: "Custom.Clear", to: null, relation: null, otherId: null },
        ] : [{
            kind: kind === "delete" ? "work-item-delete" : kind === "add-link" ? "link-add" : "link-remove",
            field: null, to: null, relation: kind === "delete" ? null : "related", otherId: null,
            otherProviderWorkItem: kind === "delete" ? target : peer,
        }],
        ...(kind === "add-link" ? { capability: { relationship: "related", declared: "emulated",
            availability: "unknown", reason: "not-probed" } } : {}),
    }));
    return value;
}

test("provider review model v1 retains every opaque identity, effect and precondition without numeric coercion", () => {
    const value = providerFixture();
    const retained = capture(value);
    assert.deepEqual(retained.envelope, value);
    assert.ok(Object.isFrozen(retained.envelope.reviewModel.providerAffectedItems[0].item.externalIdentity));
    assert.equal(retained.envelope.reviewModel.operations[0].preconditions[0].value, 'W/"opaque-900719925474099312345"');
    assert.equal(retained.envelope.reviewModel.operations[0].consequences[0].to, "");
    assert.equal(retained.envelope.reviewModel.operations[0].consequences[1].to, null);
    value.reviewModel.providerAffectedItems[0].item.externalIdentity.nativeId = "changed";
    assert.equal(retained.envelope.reviewModel.operations[0].target.providerWorkItem.externalIdentity.nativeId,
        "900719925474099312345");
});

test("provider identities distinguish equal native IDs across scopes and providers and preserve target role precedence", () => {
    const value = providerFixture();
    const target = value.reviewModel.operations[0].target.providerWorkItem;
    const otherScope = providerRef(target.externalIdentity.nativeId, "another-scope");
    const otherProvider = providerRef(target.externalIdentity.nativeId, target.externalIdentity.resourceScope, "another-provider");
    value.reviewModel.operations[1].consequences[0].otherProviderWorkItem = otherScope;
    value.reviewModel.operations[2].consequences[0].otherProviderWorkItem = otherProvider;
    value.reviewModel.providerAffectedItems = [
        { item: target, role: "target" }, { item: otherScope, role: "peer" }, { item: otherProvider, role: "peer" },
    ];
    assert.equal(capture(value).envelope.reviewModel.providerAffectedItems.length, 3);
    value.reviewModel.operations[2].consequences[0].otherProviderWorkItem = target;
    value.reviewModel.providerAffectedItems.pop();
    assert.equal(capture(value).envelope.reviewModel.providerAffectedItems[0].role, "target");
});

test("provider targets, endpoints, coverage and preconditions refuse malformed or contradictory evidence", () => {
    const mutations = [
        (v) => { v.reviewModel.operations[0].target.workItemId = 42; },
        (v) => { v.reviewModel.operations[0].target.stagedIdentity = "seed"; },
        (v) => { v.reviewModel.operations[0].target.seed = {}; },
        (v) => { v.reviewModel.operations[0].target.providerWorkItem.localId = 42; },
        (v) => { v.reviewModel.operations[0].target.providerWorkItem.externalIdentity.nativeId = ""; },
        (v) => { v.reviewModel.operations[0].target.providerWorkItem.externalIdentity.resourceScope = null; },
        (v) => { delete v.reviewModel.operations[0].target.providerWorkItem.externalIdentity.providerKind; },
        (v) => { v.reviewModel.operations[0].preconditions[0].kind = "expectedRevision"; },
        (v) => { v.reviewModel.operations[0].preconditions[0].value = 3; },
        (v) => { v.reviewModel.operations[0].preconditions[0].value = ""; },
        (v) => { v.reviewModel.operations[1].consequences[0].otherId = 43; },
        (v) => { delete v.reviewModel.operations[1].consequences[0].otherProviderWorkItem; },
        (v) => { v.reviewModel.operations[1].consequences[0].relation = null; },
        (v) => { v.reviewModel.operations[3].consequences[0].otherProviderWorkItem = providerRef("elsewhere"); },
        (v) => { v.reviewModel.operations[0].consequences[0].otherProviderWorkItem = providerRef("peer"); },
        (v) => { v.reviewModel.operations[0].consequences[1].to = ""; },
        (v) => { v.reviewModel.operations[0].consequences[0].kind = "future-effect"; },
        (v) => { v.reviewModel.operations[1].opId = v.reviewModel.operations[0].opId; },
        (v) => { v.reviewModel.operations.reverse(); },
        (v) => { delete v.reviewModel.providerAffectedItems; },
        (v) => { v.reviewModel.providerAffectedItems.pop(); },
        (v) => { v.reviewModel.providerAffectedItems[0].role = "peer"; },
        (v) => { v.reviewModel.providerAffectedItems[1].role = "context"; },
        (v) => { v.reviewModel.providerAffectedItems.push(v.reviewModel.providerAffectedItems[0]); },
        (v) => { v.reviewModel.providerAffectedItems.push({ item: providerRef("extra"), role: "peer" }); },
        (v) => { v.reviewModel.modelVersion = 2; },
        (v) => { v.reviewModel.operations[1].capability.reason = null; },
    ];
    for (const [index, mutate] of mutations.entries()) {
        const value = providerFixture();
        mutate(value);
        assert.throws(() => capture(value), /unavailable/, `mutation ${index}`);
    }
});

test("provider deletion equality checks complete identity even when coverage is internally consistent", () => {
    for (const key of ["localId", "providerKind", "resourceScope", "nativeId"]) {
        const value = providerFixture();
        const other = structuredClone(value.reviewModel.operations[3].target.providerWorkItem);
        if (key === "localId") other.localId += "-other";
        else other.externalIdentity[key] += "-other";
        value.reviewModel.operations[3].consequences[0].otherProviderWorkItem = other;
        value.reviewModel.providerAffectedItems.push({ item: other, role: "peer" });
        assert.throws(() => capture(value), /deletion identity mismatch/, key);
    }
});

test("mixed legacy and provider operations preserve both complete affected sets", () => {
    const value = fixture();
    const provider = providerFixture();
    value.reviewModel.providerAffectedItems = provider.reviewModel.providerAffectedItems;
    const offset = value.operations.length;
    value.operations.push(...provider.operations.map((op) => ({ ...op, ordinal: op.ordinal + offset })));
    value.reviewModel.operations.push(...provider.reviewModel.operations.map((op) => ({ ...op, ordinal: op.ordinal + offset })));
    assert.equal(capture(value).envelope.reviewModel.operations.length, 9);
    value.reviewModel.operations[0].consequences[0].otherProviderWorkItem = providerRef("unexpected");
    assert.throws(() => capture(value), /invalid field effect/);
});

test("provider brief exposes effects, recipe and capability evidence without decision choices or origin", () => {
    const root = dom();
    const value = providerFixture();
    value.canApply = true;
    value.reviewModel.recipe = { recipeId: "<img src=x onerror=untrusted()>", version: 3 };
    const view = mountRetainedReview(root, JSON.stringify(value), expected);
    assert.equal(view.available, true);
    const brief = flatten(root);
    for (const text of ["900719925474099312345", "field-clear", "link-add", "link-remove", "work-item-delete",
        "expectedProviderRevision", "not-probed", "emulated", "Before value for Custom.Value: unknown",
        "<img src=x onerror=untrusted()>", "capture authenticity is not verified"])
        assert.ok(brief.includes(text), text);
    const buttons = all(root).filter((e) => e.tag === "button");
    assert.deepEqual(buttons.map((e) => e.textContent), ["Details", "Back"]);
    buttons[0].listeners.click();
    assert.match(flatten(root), /Complete retained envelope/);
    buttons[1].listeners.click();
    assert.equal(flatten(root), brief);
    assert.equal(all(root).some((e) => ["a", "script", "img", "iframe"].includes(e.tag)), false);
    assert.equal(view.retained.envelope.reviewModel.operations[0].consequences[0].before, undefined);
});

test("known additive provider evidence is retained while malformed recipe refuses", () => {
    const value = providerFixture();
    value.reviewModel.providerAffectedItems[0].item.additive = "future evidence";
    assert.deepEqual(capture(value).envelope, value);
    for (const recipe of [{ recipeId: "", version: 1 }, { recipeId: "recipe", version: "1" }]) {
        value.reviewModel.recipe = recipe;
        assert.throws(() => capture(value), /recipe context/);
    }
});

function appendFixture() {
    const value = fixture();
    value.operations = [{ ordinal: 0, id: "comment-1", kind: "AppendComment" }];
    value.reviewModel.modelVersion = 2;
    value.reviewModel.operations = [{
        ordinal: 0, opId: "comment-1", kind: "append-comment",
        target: { workItemId: 42, stagedIdentity: null, seed: null },
        summary: "Append markdown discussion comment",
        preconditions: [
            { kind: "savedBenchId", value: "7" },
            { kind: "expectedBenchContentsDigest", value: "b".repeat(64) },
            { kind: "expectedIdentityId", value: "identity-1" },
        ],
        consequences: [{
            kind: "discussion-append", bodyFormat: "markdown",
            submittedText: "Hello **team**", effectiveBody: "<p>Hello <strong>team</strong></p>\n",
            field: null, to: null, relation: null, otherId: null,
        }],
    }];
    return value;
}

test("v2 discussion append requires and renders exact saved-Bench and identity preconditions", () => {
    const value = appendFixture();
    const version2 = { ...expected, expectedModelVersion: 2 };
    const root = dom();
    const result = mountRetainedReview(root, JSON.stringify(value), version2);
    assert.equal(result.available, true);
    const output = flatten(root);
    assert.match(output, /savedBenchId.*7/s);
    assert.ok(output.includes("expectedBenchContentsDigest"));
    assert.ok(output.includes("b".repeat(64)));
    assert.ok(output.includes("expectedIdentityId"));
    assert.ok(output.includes("identity-1"));
    assert.ok(output.includes("Hello **team**"));
    assert.ok(output.includes("<p>Hello <strong>team</strong></p>"));

    for (const mutate of [
        (v) => { v.reviewModel.operations[0].preconditions[0].value = "07"; },
        (v) => { v.reviewModel.operations[0].preconditions[1].value = "A".repeat(64); },
        (v) => { v.reviewModel.operations[0].preconditions[2].value = ""; },
        (v) => { v.reviewModel.operations[0].preconditions[2].value = 42; },
        (v) => { v.reviewModel.operations[0].preconditions.pop(); },
        (v) => { v.reviewModel.operations[0].preconditions[0].kind = "expectedIdentityId"; },
        (v) => { v.reviewModel.operations[0].consequences[0].bodyFormat = "html"; },
    ]) {
        const invalid = appendFixture();
        mutate(invalid);
        assert.throws(() => retainProposalReview(JSON.stringify(invalid), version2), /unavailable/);
    }
    assert.throws(() => retainProposalReview(JSON.stringify(value), expected), /unavailable/);
});
