import assert from "node:assert/strict";
import test from "node:test";
import { serializeDiscussionRequest, validateDiscussionResult } from "../discussion-document.mjs";
import { discussionTarget, discussionPage, discussionRefusal } from "./discussion-fixture.mjs";
const target = discussionTarget();
const validate = (value, options = {}) => validateDiscussionResult(value, { target, exitCode: 0, ...options });

test("landed v1 request preserves exact native qualifiers and cursor, never a fabricated contents digest", () => {
    const cursor = discussionPage().paging.nextCursor;
    const request = JSON.parse(serializeDiscussionRequest(target, cursor));
    assert.deepEqual(request, { version: 1, itemKey: target.expectedKey, bench: target.bench,
        expectBench: target.bench, expectIdentity: target.identityId, cursor });
    assert.equal(validate(discussionPage()).context.benchContentsDigest, "native-contents-not-settings-digest");
});
test("validated page preserves decimal strings, null/empty, deleted flags, untrusted bodies and inert formats", () => {
    const value = discussionPage();
    value.comments[0].body = "";
    value.comments[0].format = null;
    const result = validate(value);
    assert.equal(result.page.comments[0].id, "9007199254740993");
    assert.equal(result.page.comments[0].body, "");
    assert.equal(result.page.comments[0].author.id, null);
    assert.equal(result.page.comments[0].isDeleted, true);
    assert.equal(result.page.comments[0].format, null);
    assert.equal(result.page.reportedTotalCount, null);
    assert.equal("context" in result.page, false);
    assert.equal("nextCursor" in result.page, false);
});
test("empty-more and empty-exhausted are distinct valid provider pages, not whole-thread snapshots", () => {
    for (const more of [true, false]) {
        const value = discussionPage(target, more);
        value.comments = [];
        value.paging.count = "0";
        const result = validate(value);
        assert.equal(result.page.traversal, more ? "more" : "exhausted");
        assert.equal(result.page.snapshotConsistency, "not-guaranteed");
    }
});
test("every native context field and every qualified item component must match on next page", () => {
    const first = validate(discussionPage());
    for (const field of Object.keys(first.context)) {
        const value = discussionPage();
        if (field === "itemKey") value.context.itemKey.workItemId = 43;
        else value.context[field] = "changed";
        assert.throws(() => validate(value, { context: first.context }));
    }
    for (const field of Object.keys(target.expectedKey)) {
        const value = discussionPage();
        value.context.itemKey[field] = field === "workItemId" ? 43 : "different";
        assert.throws(() => validate(value));
    }
});
test("contradictory counts, unsupported versions/formats, extra metadata and broken paging fail closed", () => {
    const faults = [
        value => { value.version = 2; }, value => { value.paging.count = "2"; },
        value => { value.paging.count = 1; }, value => { value.paging.reportedTotalCount = "0"; },
        value => { value.paging.scope = "thread"; }, value => { value.paging.snapshotConsistency = "guaranteed"; },
        value => { value.paging.nextCursor = null; }, value => { value.paging.nextCursor.optionProfile = "other"; },
        value => { value.paging.nextCursor.providerToken = "x\n"; },
        value => { value.paging.nextCursor.providerToken = "x".repeat(8193); },
        value => { value.paging.nextCursor.context.benchContentsDigest = "different"; },
        value => { value.comments.push(structuredClone(value.comments[0])); value.paging.count = "2"; },
        value => { value.comments[0].author.email = "not-exported"; },
        value => { value.comments[0].createdAt = "2026-10-25T12:00:00+03:00"; },
        value => { value.comments[0].format = "execute"; }, value => { delete value.comments[0].body; },
    ];
    for (const fault of faults) { const value = discussionPage(); fault(value); assert.throws(() => validate(value)); }
});
test("refused output is exit-consistent and projected to safe code without diagnostic text or partial data", () => {
    for (const code of ["discussion-denied", "discussion-not-found", "discussion-offline", "discussion-throttled",
        "discussion-response-too-large", "discussion-response-invalid", "discussion-unavailable",
        "authority-invalidated", "canceled", "provider-unsupported", "provider-unknown", "provider-unavailable",
        "bench-item-not-member", "bench-item-not-cached"]) {
        assert.deepEqual(validate(discussionRefusal(code), { exitCode: 1 }), { status: "refused", code });
    }
    assert.deepEqual(validate(discussionRefusal("usage"), { exitCode: 2 }), { status: "refused", code: "usage" });
    assert.throws(() => validate(discussionRefusal(), { exitCode: 0 }));
    assert.throws(() => validate(discussionRefusal("usage"), { exitCode: 1 }));
    assert.throws(() => validate(discussionPage(), { exitCode: 1 }));
    const value = discussionRefusal(); value.comments = discussionPage().comments;
    assert.throws(() => validate(value, { exitCode: 1 }));
});
test("request rejects invalid targets and malformed or oversized provider tokens before transport", () => {
    for (const bad of [{ ...target, workItemId: -1 }, { ...target, bench: "Sprint" },
        { ...target, expectedKey: { ...target.expectedKey, workItemId: 0 } }])
        assert.throws(() => serializeDiscussionRequest(bad));
    const cursor = discussionPage().paging.nextCursor;
    cursor.providerToken = "\ud83d\ude00".repeat(2049);
    assert.throws(() => serializeDiscussionRequest(target, cursor));
});
