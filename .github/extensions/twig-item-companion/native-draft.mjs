import { execFile as nodeExecFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";
import { validateDraftDocument as validateDocument } from "./draft-document.mjs";

const record = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const nonempty = (v) => typeof v === "string" && v.trim().length > 0;
const id = (v) => Number.isInteger(v) && v !== 0 && Math.abs(v) <= 2147483647;
function requireShape(ok, message) {
    if (!ok) throw new Error(`Native draft inspection unavailable: ${message}`);
}
function samePath(a, b) {
    const normalize = (p) => process.platform === "win32" ? path.resolve(p).toLowerCase() : path.resolve(p);
    return nonempty(a) && nonempty(b) && normalize(a) === normalize(b);
}
function validKey(key) {
    return record(key) && ["checkoutRoot", "connectionId", "bindingId"].every((k) => nonempty(key[k])) && id(key.workItemId);
}

export function validateDraftDocument(doc, expectedKey, cwd) {
    requireShape(validKey(expectedKey) && samePath(expectedKey.checkoutRoot, cwd), "a captured qualified item key is required");
    return validateDocument(doc, expectedKey);
}

export function createNativeDraftReader({ execFile = nodeExecFile, realpath = nodeRealpath } = {}) {
    return async ({ workItemId, workspacePath, expectedKey, signal }) => {
        requireShape(id(workItemId) && validKey(expectedKey) && expectedKey.workItemId === workItemId &&
            nonempty(workspacePath), "explicit native item and captured qualified key are required");
        const cwd = await realpath(workspacePath);
        requireShape(samePath(expectedKey.checkoutRoot, cwd), "captured checkout differs from inspection workspace");
        const stdout = await new Promise((resolve, reject) => {
            execFile("twig", ["draft", "show", String(workItemId), "--document", "-o", "json"], {
                cwd, encoding: "utf8", shell: false, windowsHide: true,
                timeout: 15_000, maxBuffer: 4 * 1024 * 1024, signal,
            }, (error, output, stderr) => {
                if (error) {
                    let message = String(stderr || "").trim();
                    try { message = JSON.parse(message)?.error || message; } catch { /* Preserve native non-JSON refusals. */ }
                    reject(new Error(typeof message === "string" && message ? message : `Native draft inspection failed: ${error.message}`, { cause: error }));
                } else resolve(output);
            });
        });
        let doc;
        try { doc = JSON.parse(stdout); }
        catch (error) { throw new Error("Native draft inspection returned malformed JSON.", { cause: error }); }
        return validateDraftDocument(doc, expectedKey, cwd);
    };
}
