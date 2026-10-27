import { execFile as nodeExecFile, spawn as nodeSpawn } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";
import { serializeStageRequest, serializeRetainRequest, validateEditingResult, validateRetainResult } from "./editing-document.mjs";
import { requireAdmitted } from "./native-executable.mjs";
import { createNativeDraftSaver } from "./native-save.mjs";

// A transport failure cannot prove that a submitted local mutation rolled back.
export class NativeEditingTransportError extends Error {
    outcome = "unknown";
}
const nonempty = (value) => typeof value === "string" && value.trim().length > 0;
function spawnCaptured(executable, args, options, callback, overflow, spawn) {
    const child = spawn(executable, args, {
        cwd: options.cwd, shell: false, windowsHide: true, detached: options.detached,
        stdio: ["pipe", "pipe", "pipe"],
    });
    const chunks = [];
    let size = 0;
    let failure;
    let exceeded = false;
    const collect = (data, stdout) => {
        if (exceeded) return;
        size += data.length;
        if (size > options.maxBuffer) {
            exceeded = true;
            failure = new Error("Native editing combined stdout/stderr exceeds 4MiB.");
            overflow(failure);
            return;
        }
        if (stdout) chunks.push(data);
    };
    child.stdout.on("data", (data) => collect(data, true));
    child.stderr.on("data", (data) => collect(data, false));
    for (const stream of [child.stdout, child.stderr]) {
        stream.on("error", (error) => { failure = error; overflow(error); });
    }
    child.on("error", (error) => { failure = error; });
    child.once("close", (code, signal) => {
        const error = failure || (code === 0 && !signal ? null :
            Object.assign(new Error("Native editing child failed."), { code, signal }));
        callback(error, Buffer.concat(chunks).toString("utf8"));
    });
    return child;
}
async function terminateTree(child) {
    if (!Number.isInteger(child.pid) || child.pid <= 0) throw new Error("Owned child PID is unavailable.");
    if (process.platform === "win32") {
        await new Promise((resolve, reject) => {
            nodeExecFile("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"],
                { shell: false, windowsHide: true, timeout: 4000, maxBuffer: 65536 },
                (error) => error ? reject(error) : resolve());
        });
    } else {
        try { process.kill(-child.pid, "SIGKILL"); }
        catch (error) { if (error.code !== "ESRCH") throw error; }
    }
}
function samePath(a, b) {
    const normalize = (value) => process.platform === "win32" ? path.resolve(value).toLowerCase() : path.resolve(value);
    return nonempty(a) && nonempty(b) && normalize(a) === normalize(b);
}

export function createNativeCommandRunner({
    spawn = nodeSpawn, launch = (exe, args, options, callback, overflow) =>
        spawnCaptured(exe, args, options, callback, overflow, spawn),
    realpath = nodeRealpath, stopTree = terminateTree, executable,
} = {}) {
    let active = false;
    let poisoned = false;
    async function invoke(capability, args, cwd, signal, stdin, expected, validateResult) {
        signal?.throwIfAborted();
        requireAdmitted(executable);
        if (active || poisoned) throw new Error("Native editing process ownership is busy or unresolved; no second child admitted.");
        active = true;
        let twig;
        try {
            twig = await executable.prepare(capability, { signal });
            signal?.throwIfAborted();
        } catch (error) {
            active = false;
            throw error;
        }
        return new Promise((resolve, reject) => {
            let inputError;
            let settled = false;
            let closed = false;
            let output;
            let callbackDone = false;
            let cleanup;
            let closeTimer;
            let deadline;
            const abort = () => stop(new Error("Native editing canceled."));
            const finish = () => {
                if (!callbackDone || !closed || settled) return;
                void Promise.resolve(cleanup).then(() => {
                    if (inputError) fail(inputError);
                    else {
                        try {
                            const { error, stdout } = output;
                            const code = error ? error.code : 0;
                            if (error?.killed || error?.signal || ![0, 1, 2].includes(code)) throw error;
                            const validate = validateResult ?? (expected.action === "retain" ? validateRetainResult : validateEditingResult);
                            const result = validate(JSON.parse(stdout), { ...expected, exitCode: code });
                            settled = true;
                            clearTimeout(closeTimer);
                            clearTimeout(deadline);
                            signal?.removeEventListener("abort", abort);
                            resolve(result);
                        } catch (failure) { fail(failure); }
                    }
                }, (failure) => { poisoned = true; fail(failure); });
            };
            const stop = (error) => {
                inputError ??= error;
                if (!cleanup && child) cleanup = Promise.resolve().then(() => stopTree(child));
                // Rejection is observed immediately, but ownership remains latched until close.
                cleanup?.catch(() => {});
                finish();
            };
            const fail = (error) => {
                if (settled) return;
                settled = true;
                clearTimeout(closeTimer);
                clearTimeout(deadline);
                signal?.removeEventListener("abort", abort);
                reject(new NativeEditingTransportError(
                    "Native editing outcome is unknown. Retain intent and explicitly reopen; do not retry automatically.",
                    { cause: error }));
            };
            let child;
            try {
                child = launch(twig, args, {
                    cwd, encoding: "utf8", shell: false, windowsHide: true,
                    maxBuffer: 4 * 1024 * 1024,
                    detached: process.platform !== "win32",
                }, (error, stdout) => {
                    output = { error, stdout };
                    callbackDone = true;
                    if (error?.killed || error?.signal || (error && ![1, 2].includes(error.code))) stop(error);
                    finish();
                }, stop);
                child.once("close", () => { closed = true; finish(); });
                signal?.addEventListener("abort", abort, { once: true });
                if (signal?.aborted) abort();
                deadline = setTimeout(() => stop(new Error("Native editing exceeded its 15-second deadline.")), 15000);
                closeTimer = setTimeout(() => {
                    poisoned = true;
                    stop(new Error("Owned native child did not close before its deadline."));
                    fail(inputError);
                }, 19000);
                if (stdin !== undefined) {
                    if (!child?.stdin) {
                        stop(new Error("The native child does not provide stdin."));
                        return;
                    }
                    child.stdin.on("error", stop);
                    child.stdin.end(stdin, "utf8");
                }
            } catch (error) {
                if (child) stop(error);
                else { closed = true; callbackDone = true; fail(error); }
            }
        }).finally(() => {
            // The poison latch prevents reuse if closure/cleanup could not be proven.
            active = false;
        });
    }
    return invoke;
}

export function createNativeEditingAdapter(options = {}) {
    const { realpath = nodeRealpath } = options;
    const invoke = createNativeCommandRunner(options);
    return {
        save: createNativeDraftSaver(invoke, async (workspacePath, checkoutRoot) => {
            if (!nonempty(workspacePath)) throw new Error("An explicit Save workspace is required.");
            const cwd = await realpath(workspacePath);
            if (!samePath(checkoutRoot, cwd)) throw new Error("Save checkout differs from the retained native capture.");
            return cwd;
        }),
        async open({ workItemId, bench, workspacePath, expectedKey, identityId, signal }) {
            signal?.throwIfAborted();
            if (!Number.isInteger(workItemId) || workItemId <= 0 || workItemId > 2147483647 ||
                !nonempty(bench) || bench.length > 512 || !nonempty(workspacePath) ||
                !expectedKey || !nonempty(expectedKey.connectionId) || !nonempty(expectedKey.bindingId) ||
                expectedKey.workItemId !== workItemId || !nonempty(identityId)) {
                throw new Error("Explicit Bench, identity and qualified native item are required for OPEN.");
            }
            const captured = structuredClone(expectedKey);
            const cwd = await realpath(workspacePath);
            if (!samePath(captured.checkoutRoot, cwd)) throw new Error("OPEN checkout differs from captured item.");
            const args = ["draft", "open", String(workItemId), "--bench", bench, "-o", "json",
                "--expect-binding", captured.bindingId, "--expect-identity", identityId];
            const result = await invoke("draft-open", args, cwd, signal, undefined, { action: "open", expectedKey: captured, bench });
            if (result.document && result.document.nativeContext.identityId !== identityId) {
                throw new NativeEditingTransportError("Native OPEN identity changed; outcome is unknown. Retain intent and reopen.");
            }
            return result;
        },
        async stage({ request, workspacePath, signal }) {
            signal?.throwIfAborted();
            const json = serializeStageRequest(request);
            const captured = JSON.parse(json);
            if (!nonempty(workspacePath)) throw new Error("An explicit stage workspace is required.");
            const cwd = await realpath(workspacePath);
            if (!samePath(captured.nativeContext.itemKey.checkoutRoot, cwd)) {
                throw new Error("STAGE checkout differs from captured context.");
            }
            return invoke("draft-stage", ["draft", "stage", "--stdin", "-o", "json"], cwd, signal, json,
                { action: "stage", expectedContext: captured.nativeContext, expectedRequest: captured });
        },
        async retain({ request, workspacePath, signal }) {
            signal?.throwIfAborted();
            const json = serializeRetainRequest(request);
            const captured = JSON.parse(json);
            if (!nonempty(workspacePath)) throw new Error("An explicit retain workspace is required.");
            const cwd = await realpath(workspacePath);
            if (!samePath(captured.nativeContext.itemKey.checkoutRoot, cwd)) {
                throw new Error("RETAIN checkout differs from captured context.");
            }
            return invoke("draft-retain", ["draft", "retain", "--stdin", "-o", "json"], cwd, signal, json,
                { action: "retain", expectedRequest: captured });
        },
    };
}
