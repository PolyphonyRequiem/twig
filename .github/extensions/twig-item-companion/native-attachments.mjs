import { spawn as nodeSpawn, execFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";
import { parseAttachmentFrame, serializeAttachmentRequest, validateAttachmentResult } from "./attachment-document.mjs";
import { requireAdmitted } from "./native-executable.mjs";

async function terminate(child) {
    if (!Number.isInteger(child.pid) || child.pid <= 0) throw new Error("Owned attachment child unavailable.");
    if (process.platform === "win32") {
        await new Promise((resolve, reject) => execFile("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"],
            { shell: false, windowsHide: true, timeout: 4000, maxBuffer: 65536 },
            error => error ? reject(error) : resolve()));
    } else {
        try { process.kill(-child.pid, "SIGKILL"); }
        catch (error) { if (error.code !== "ESRCH") throw error; }
    }
}

export function createNativeAttachmentReader({
    spawn = nodeSpawn, realpath = nodeRealpath, stopTree = terminate, executable,
} = {}) {
    let active = false;
    let poisoned = false;
    return async ({ target, action, entry = null, workspacePath, signal }) => {
        requireAdmitted(executable);
        if (active || poisoned) throw new Error("Native attachment ownership is busy or unresolved.");
        target = structuredClone(target);
        entry = structuredClone(entry);
        active = true;
        try {
            signal?.throwIfAborted();
            const cwd = await realpath(workspacePath);
            const normalize = value => process.platform === "win32" ? path.resolve(value).toLowerCase() : path.resolve(value);
            if (normalize(cwd) !== normalize(target.expectedKey.checkoutRoot))
                throw new Error("Attachment checkout does not match its admitted target.");
            const source = serializeAttachmentRequest(target, action, entry);
            const capability = await executable.prepare("attachments", { signal });
            signal?.throwIfAborted();
            return await new Promise((resolve, reject) => {
                let child, deadline, closeDeadline, cleanup, failure, settled = false;
                let stdoutBytes = 0, stderrBytes = 0;
                const chunks = [];
                const maxOutput = action === "download" ? 16 * 1024 * 1024 + 65552 : 4 * 1024 * 1024;
                function fail(message) { failure ??= new Error(message); }
                function stop(message) {
                    fail(message);
                    if (!cleanup && child) {
                        cleanup = Promise.resolve().then(() => stopTree(child));
                        cleanup.catch(() => { poisoned = true; });
                    }
                }
                const abort = () => stop("Native attachment read canceled; no bytes were accepted.");
                function finish(error, value) {
                    if (settled) return;
                    settled = true;
                    clearTimeout(deadline);
                    clearTimeout(closeDeadline);
                    signal?.removeEventListener("abort", abort);
                    if (error) reject(error); else resolve(value);
                }
                try {
                    child = spawn(capability, action === "download"
                        ? ["bench", "attachments", "--stdin", "--framed"]
                        : ["bench", "attachments", "--stdin", "-o", "json"], {
                        cwd, shell: false, windowsHide: true, detached: process.platform !== "win32",
                        stdio: ["pipe", "pipe", "pipe"],
                    });
                    deadline = setTimeout(() => stop("Native attachment read exceeded its 15-second deadline; no bytes were accepted."), 15000);
                    closeDeadline = setTimeout(() => {
                        poisoned = true;
                        stop("Native attachment child closure is unresolved; no bytes were accepted.");
                        finish(failure);
                    }, 19000);
                    child.stdout.on("data", data => {
                        stdoutBytes += data.length;
                        if (stdoutBytes > maxOutput)
                            stop("Native attachment response exceeds the extension output limit; no bytes were accepted.");
                        else if (!failure) chunks.push(data);
                    });
                    child.stderr.on("data", data => {
                        stderrBytes += data.length;
                        if (stderrBytes > 65536) stop("Native attachment diagnostics exceed the extension limit; no bytes were accepted.");
                    });
                    for (const stream of [child.stdin, child.stdout, child.stderr])
                        stream.on("error", () => stop("Native attachment transport failed; no bytes were accepted."));
                    child.on("error", () => stop("Native attachment command could not start; no bytes were accepted."));
                    child.once("close", (code, childSignal) => {
                        clearTimeout(deadline);
                        void Promise.resolve(cleanup).then(() => {
                            if (failure) { finish(failure); return; }
                            if (childSignal || ![0, 1, 2].includes(code)) {
                                finish(new Error("Native attachment command failed; no bytes were accepted.")); return;
                            }
                            try {
                                const output = Buffer.concat(chunks);
                                const frame = action === "download" ? parseAttachmentFrame(output) : {
                                    value: JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(output)),
                                    content: null,
                                };
                                const value = validateAttachmentResult(frame.value, {
                                    target, action, exitCode: code, content: frame.content, expectedEntry: entry,
                                });
                                finish(null, value);
                            } catch {
                                finish(new Error("Native attachment response was invalid or mismatched; no bytes were accepted."));
                            }
                        }, () => {
                            poisoned = true;
                            finish(new Error("Native attachment cleanup failed; ownership is unresolved."));
                        });
                    });
                    signal?.addEventListener("abort", abort, { once: true });
                    if (signal?.aborted) abort();
                    if (!failure) child.stdin.end(source, "utf8");
                } catch {
                    if (child) stop("Native attachment transport failed; no bytes were accepted.");
                    else finish(new Error("Native attachment command could not start; no bytes were accepted."));
                }
            });
        } finally { active = false; }
    };
}
