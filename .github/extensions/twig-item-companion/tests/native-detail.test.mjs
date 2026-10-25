import assert from "node:assert/strict";
import { mkdtemp, rm, readFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { createNativeDetailReader } from "../native-detail.mjs";
import { fakeExecutable, FAKE_TWIG } from "./executable-fixture.mjs";

const fixture = JSON.parse(await readFile(new URL("./fixtures/detail-v2.json", import.meta.url), "utf8"));

async function makeWorkspace() {
    return mkdtemp(path.join(os.tmpdir(), "twig-canvas-test-"));
}

function detail(workspacePath, workItemId = 42) {
    const value = structuredClone(fixture);
    value.itemKey = { checkoutRoot: workspacePath, connectionId: "connection-1", bindingId: "binding-1", workItemId };
    value.benchId = "bench-1";
    value.identityId = "identity-1";
    value.item.id = value.document.workItemId = workItemId;
    return JSON.stringify(value);
}

test("runs only the supported detail command in the canonical workspace", async () => {
    const workspacePath = await makeWorkspace();
    try {
        let actual;
        const read = createNativeDetailReader({ executable: fakeExecutable(),
            realpath: async (value) => value,
            execFile: (executable, args, options, callback) => {
                actual = { executable, args, options };
                callback(null, detail(workspacePath), "");
                return {};
            },
        });

        const result = await read({ workItemId: 42, workspacePath });

        assert.equal(result.itemKey.workItemId, 42);
        assert.equal(actual.executable, FAKE_TWIG);
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
        const read = createNativeDetailReader({ executable: fakeExecutable(),
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
    const read = createNativeDetailReader({ executable: fakeExecutable(),
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
            const read = createNativeDetailReader({ executable: fakeExecutable(),
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
        const read = createNativeDetailReader({ executable: fakeExecutable(),
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

test("detail prepares its listed capability, spawns the admitted path, and a refusal launches nothing", async () => {
    const workspacePath = await makeWorkspace();
    try {
        const executable = fakeExecutable();
        const spawned = [];
        const execFile = (file, args, options, callback) => { spawned.push(file); callback(null, detail(workspacePath), ""); return {}; };
        await createNativeDetailReader({ executable, realpath: async (value) => value, execFile })({ workItemId: 42, workspacePath });
        assert.deepEqual(executable.prepared, ["detail"]);
        assert.deepEqual(spawned, [FAKE_TWIG]);

        const refusal = Object.assign(new Error("does not offer bench detail --document"), { code: "twig_capability_missing" });
        const refused = createNativeDetailReader({ executable: fakeExecutable({ refuse: refusal }), realpath: async (value) => value, execFile });
        await assert.rejects(refused({ workItemId: 42, workspacePath }), (error) => error === refusal);
        await assert.rejects(createNativeDetailReader({ realpath: async (value) => value, execFile })({ workItemId: 42, workspacePath }),
            { code: "twig_executable_unavailable" });
        assert.deepEqual(spawned, [FAKE_TWIG]);
    } finally {
        await rm(workspacePath, { recursive: true, force: true });
    }
});