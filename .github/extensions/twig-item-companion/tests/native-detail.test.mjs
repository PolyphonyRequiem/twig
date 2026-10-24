import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { createNativeDetailReader } from "../native-detail.mjs";

async function makeWorkspace() {
    return mkdtemp(path.join(os.tmpdir(), "twig-canvas-test-"));
}

function detail(workspacePath, workItemId = 42) {
    return JSON.stringify({
        version: 2,
        itemKey: {
            checkoutRoot: workspacePath,
            connectionId: "connection-1",
            bindingId: "binding-1",
            workItemId,
        },
        benchId: "bench-1",
        identityId: "identity-1",
        item: { revision: 5 },
        document: {},
        editing: "unavailable",
        relatedItemsCoverage: "not-exported",
        iconAssetAuthority: "unavailable",
    });
}

test("runs only the supported detail command in the canonical workspace", async () => {
    const workspacePath = await makeWorkspace();
    try {
        let actual;
        const read = createNativeDetailReader({
            realpath: async (value) => value,
            execFile: (executable, args, options, callback) => {
                actual = { executable, args, options };
                callback(null, detail(workspacePath), "");
                return {};
            },
        });

        const result = await read({ workItemId: 42, workspacePath });

        assert.equal(result.itemKey.workItemId, 42);
        assert.equal(actual.executable, "twig");
        assert.deepEqual(actual.args, ["bench", "detail", "42", "--document", "-o", "json"]);
        assert.equal(actual.options.cwd, workspacePath);
        assert.equal(actual.options.shell, false);
        assert.equal(actual.options.timeout, 15_000);
        assert.equal(actual.options.maxBuffer, 4 * 1024 * 1024);
    } finally {
        await rm(workspacePath, { recursive: true });
    }
});

test("refresh pins the captured Bench, binding, and identity", async () => {
    const workspacePath = await makeWorkspace();
    try {
        let actualArgs;
        const read = createNativeDetailReader({
            realpath: async (value) => value,
            execFile: (_executable, args, _options, callback) => {
                actualArgs = args;
                callback(null, detail(workspacePath), "");
                return {};
            },
        });

        await read({
            workItemId: 42,
            workspacePath,
            expected: { benchId: "bench-1", bindingId: "binding-1", identityId: "identity-1" },
        });

        assert.deepEqual(actualArgs.slice(6), [
            "--expect-bench", "bench-1",
            "--expect-binding", "binding-1",
            "--expect-identity", "identity-1",
        ]);
    } finally {
        await rm(workspacePath, { recursive: true });
    }
});

test("refuses invalid IDs before invoking Twig", async () => {
    const read = createNativeDetailReader({
        realpath: async (value) => value,
        execFile: () => assert.fail("Twig must not run for an invalid ID"),
    });

    await assert.rejects(
        read({ workItemId: 0, workspacePath: "C:\\workspace" }),
        /non-zero 32-bit integer/,
    );
});

test("refuses unsupported, mismatched, and cross-checkout observations", async () => {
    const workspacePath = await makeWorkspace();
    try {
        for (const [payload, expectedMessage] of [
            ['{"version":1}', /unsupported semantic detail document/],
            [detail(workspacePath, 43), /different work item/],
            [detail(path.join(workspacePath, "other")), /different checkout/],
        ]) {
            const read = createNativeDetailReader({
                realpath: async (value) => value,
                execFile: (_executable, _args, _options, callback) => {
                    callback(null, payload, "");
                    return {};
                },
            });
            await assert.rejects(read({ workItemId: 42, workspacePath }), expectedMessage);
        }
    } finally {
        await rm(workspacePath, { recursive: true });
    }
});

test("surfaces native command failures instead of returning empty detail", async () => {
    const workspacePath = await makeWorkspace();
    try {
        const read = createNativeDetailReader({
            realpath: async (value) => value,
            execFile: (_executable, _args, _options, callback) => {
                callback(new Error("exit 1"), "", "Admission refused.");
                return {};
            },
        });

        await assert.rejects(
            read({ workItemId: 42, workspacePath }),
            /Admission refused/,
        );
    } finally {
        await rm(workspacePath, { recursive: true });
    }
});
