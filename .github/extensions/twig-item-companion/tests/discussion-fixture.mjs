import { EventEmitter } from "node:events";
import { readFile } from "node:fs/promises";
export const benchFixture = JSON.parse(await readFile(new URL("./fixtures/bench-v2.json", import.meta.url), "utf8"));
export function discussionTarget() {
    const origin = structuredClone(benchFixture.browser.origin);
    return { workItemId: 42, expectedKey: { checkoutRoot: origin.worktreeRoot, connectionId: origin.connectionRef,
        bindingId: origin.bindingId, workItemId: 42 }, bench: benchFixture.browser.benchId,
        benchName: benchFixture.browser.benchName, identityId: origin.identityId, origin };
}
export function discussionContext(target = discussionTarget()) {
    return { itemKey: structuredClone(target.expectedKey), benchId: target.bench, benchName: target.benchName,
        benchContentsDigest: "native-contents-not-settings-digest", identityId: target.identityId,
        bindingRevision: String(target.origin.bindingRevision), selectionSource: target.origin.selectionSource,
        selectionRevision: String(target.origin.selectionRevision), attachmentRevision: String(target.origin.attachmentRevision),
        storageGeneration: target.origin.storageGeneration ?? null };
}
export function discussionPage(target = discussionTarget(), more = true) {
    const context = discussionContext(target);
    return { version: 1, status: "read", capability: { implementation: "native", availability: "available" },
        context, comments: [{ id: "9007199254740993", version: "2", author: { id: null, displayName: "<img src=x onerror=evil()>" },
            createdAt: "2026-10-25T12:00:00+00:00", modifiedAt: null, isDeleted: true,
            body: "<script>evil()</script>[link](https://evil.invalid)", format: "html" }],
        paging: { traversal: more ? "more" : "exhausted", count: "1", reportedTotalCount: null,
            nextCursor: more ? { version: 1, context: structuredClone(context),
                providerToken: "private+token&https://do-not-follow.invalid", optionProfile: "ado-comments-v1-top50-asc-deleted-none" } : null,
            scope: "provider-page", snapshotConsistency: "not-guaranteed" }, error: null };
}
export function discussionRefusal(code = "discussion-offline") {
    return { version: 1, status: "refused", capability: {
        implementation: code === "provider-unsupported" ? "unsupported" : "native", availability: "unavailable" },
        context: null, comments: [], paging: null, error: { code, message: "private diagnostic not for the browser",
            recovery: "private token not for the browser" } };
}
export function childHarness(handler = () => ({ value: discussionPage(), code: 0 })) {
    const calls = [];
    const stops = [];
    const spawn = (exe, args, options) => {
        const child = new EventEmitter();
        child.pid = 12345;
        for (const name of ["stdin", "stdout", "stderr"]) child[name] = new EventEmitter();
        const call = { exe, args, options, child, input: null, close(code = 0, signal = null) {
            child.emit("close", code, signal);
        } };
        child.stdin.end = source => {
            call.input = JSON.parse(source);
            queueMicrotask(() => {
                const result = handler(call, calls.length);
                if (result === null) return;
                child.stdout.emit("data", Buffer.from(result.raw ?? JSON.stringify(result.value)));
                if (result.stderr) child.stderr.emit("data", Buffer.from(result.stderr));
                call.close(result.code ?? 0);
            });
        };
        calls.push(call);
        return child;
    };
    const stopTree = async child => {
        stops.push(child.pid);
        child.emit("close", null, "SIGKILL");
    };
    return { calls, stops, spawn, stopTree };
}
