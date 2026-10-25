import { spawn as nodeSpawn, execFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";
import { serializeDiscussionRequest, validateDiscussionResult } from "./discussion-document.mjs";

async function terminate(child) {
    if (!Number.isInteger(child.pid) || child.pid <= 0) throw new Error("Owned discussion child unavailable.");
    if (process.platform === "win32") {
        await new Promise((resolve, reject) => execFile("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"],
            { shell: false, windowsHide: true, timeout: 4000, maxBuffer: 65536 },
            error => error ? reject(error) : resolve()));
    } else {
        try { process.kill(-child.pid, "SIGKILL"); }
        catch (error) { if (error.code !== "ESRCH") throw error; }
    }
}
export function createNativeDiscussionReader({
    spawn = nodeSpawn, realpath = nodeRealpath, stopTree = terminate,
} = {}) {
    let active = false;
    let poisoned = false;
    return async ({ target, workspacePath, cursor = null, context = null, signal }) => {
        if (active || poisoned) throw new Error("Native discussion ownership is busy or unresolved.");
        target = structuredClone(target);
        cursor = structuredClone(cursor);
        context = structuredClone(context);
        active = true;
        try {
            signal?.throwIfAborted();
            const cwd = await realpath(workspacePath);
            const normalize = value => process.platform === "win32" ? path.resolve(value).toLowerCase() : path.resolve(value);
            if (normalize(cwd) !== normalize(target.expectedKey.checkoutRoot))
                throw new Error("Discussion checkout does not match its admitted target.");
            const source = serializeDiscussionRequest(target, cursor);
            signal?.throwIfAborted();
            return await new Promise((resolve, reject) => {
                let child, deadline, closeDeadline, cleanup, failure, settled = false, stdoutBytes = 0, stderrBytes = 0;
                const chunks = [];
                function fail(message) { failure ??= new Error(message); }
                function stop(message) {
                    fail(message);
                    if (!cleanup && child) {
                        cleanup = Promise.resolve().then(() => stopTree(child));
                        cleanup.catch(() => { poisoned = true; });
                    }
                }
                const abort = () => stop("Native discussion canceled; no page was accepted.");
                function finish(error, value) {
                    if (settled) return;
                    settled = true;
                    clearTimeout(deadline);
                    clearTimeout(closeDeadline);
                    signal?.removeEventListener("abort", abort);
                    if (error) reject(error); else resolve(value);
                }
                try {
                    child = spawn("twig", ["bench", "discussion", "--stdin", "-o", "json"], {
                        cwd, shell: false, windowsHide: true, detached: process.platform !== "win32",
                        stdio: ["pipe", "pipe", "pipe"],
                    });
                    deadline = setTimeout(() => stop("Native discussion exceeded its 15-second deadline; no page was accepted."), 15000);
                    closeDeadline = setTimeout(() => {
                        poisoned = true;
                        stop("Native discussion child closure is unresolved; no page was accepted.");
                        finish(failure);
                    }, 19000);
                    child.stdout.on("data", data => {
                        stdoutBytes += data.length;
                        if (stdoutBytes > 4 * 1024 * 1024) stop("Native discussion exceeds the extension 4MiB output limit; no page was accepted.");
                        else if (!failure) chunks.push(data);
                    });
                    child.stderr.on("data", data => {
                        stderrBytes += data.length;
                        if (stderrBytes > 65536) stop("Native discussion diagnostics exceed the extension limit; no page was accepted.");
                    });
                    for (const stream of [child.stdin, child.stdout, child.stderr])
                        stream.on("error", () => stop("Native discussion transport failed; no page was accepted."));
                    child.on("error", () => stop("Native discussion could not start; no page was accepted."));
                    child.once("close", (code, childSignal) => {
                        clearTimeout(deadline);
                        void Promise.resolve(cleanup).then(() => {
                            if (failure) { finish(failure); return; }
                            if (childSignal || ![0, 1, 2].includes(code)) {
                                finish(new Error("Native discussion process failed; no page was accepted.")); return;
                            }
                            try {
                                const output = new TextDecoder("utf-8", { fatal: true }).decode(Buffer.concat(chunks));
                                const value = validateDiscussionResult(JSON.parse(output), { target, context, cursor, exitCode: code });
                                finish(null, value);
                            } catch {
                                finish(new Error("Native discussion returned an invalid or mismatched page."));
                            }
                        }, () => { poisoned = true; finish(new Error("Native discussion cleanup failed; ownership is unresolved.")); });
                    });
                    signal?.addEventListener("abort", abort, { once: true });
                    if (signal?.aborted) abort();
                    if (!failure) child.stdin.end(source, "utf8");
                } catch {
                    if (child) stop("Native discussion transport failed; no page was accepted.");
                    else finish(new Error("Native discussion could not start; no page was accepted."));
                }
            });
        } finally { active = false; }
    };
}
