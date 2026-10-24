import { execFile as nodeExecFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";
import { validateObservation } from "./native-detail.mjs";

const ORIGIN_KEYS = ["connectionRef", "organization", "project", "team", "bindingId", "bindingRevision",
    "identityId", "worktreeRoot", "worktreeFingerprint", "attachmentRevision", "selectionSource",
    "selectionRevision", "storageGeneration"];

// Reads the explicit-Bench detail document (`bench detail <id> --bench <target> --document`).
// `expected` is the origin captured from the v2 workbench read; it is mandatory, so this reader
// can never fall back to the shared current Bench.
export function createNativeBenchDetailReader({ execFile = nodeExecFile, realpath = nodeRealpath } = {}) {
    return async ({ workItemId, workspacePath, expected, signal }) => {
        if (!Number.isInteger(workItemId) || workItemId === 0 || Math.abs(workItemId) > 2147483647) {
            throw new Error("A non-zero work item ID is required.");
        }
        if (typeof workspacePath !== "string" || !workspacePath.trim()) {
            throw new Error("The current session does not provide a Twig workspace path.");
        }
        if (!expected || !nonempty(expected.benchId) || !record(expected.origin) ||
            !nonempty(expected.origin.bindingId) || !nonempty(expected.origin.identityId)) {
            throw new Error("Load a qualified named Bench before reading item detail.");
        }
        const cwd = await realpath(workspacePath);
        const args = ["bench", "detail", String(workItemId), "--bench", expected.benchId, "--document", "-o", "json",
            "--expect-bench", expected.benchId, "--expect-binding", expected.origin.bindingId,
            "--expect-identity", expected.origin.identityId];
        const stdout = await new Promise((resolve, reject) => {
            execFile("twig", args, {
                cwd, encoding: "utf8", shell: false, windowsHide: true,
                timeout: 15_000, maxBuffer: 4 * 1024 * 1024, signal,
            }, (error, output, stderr) => {
                if (error) reject(new Error(nativeError(stderr) || `Twig Bench detail failed: ${error.message}`, { cause: error }));
                else resolve(output);
            });
        });
        let value;
        try { value = JSON.parse(stdout); }
        catch (error) { throw new Error("Twig returned invalid Bench detail JSON.", { cause: error }); }
        validateBenchDetail(value, workItemId, cwd, expected);
        return value;
    };
}

function nativeError(stderr) {
    const text = String(stderr || "").trim();
    try {
        const message = JSON.parse(text)?.error;
        return typeof message === "string" ? message : text;
    } catch { return text; }
}

export function validateBenchDetail(value, workItemId, cwd, expected) {
    if (!record(value) || value.version !== 2 || value.observation !== "raw-mirror") {
        throw new Error("Unsupported explicit-Bench detail; version 2 raw-mirror is required.");
    }
    const origin = value.origin;
    if (!record(origin) || !ORIGIN_KEYS.every((key) => key === "storageGeneration" || key in origin) ||
        !["connectionRef", "bindingId", "identityId", "worktreeRoot"].every((key) => nonempty(origin[key])) ||
        !samePath(origin.worktreeRoot, cwd)) {
        throw new Error("Explicit-Bench detail origin qualification is missing or invalid.");
    }
    if (value.benchId !== expected.benchId || ORIGIN_KEYS.some((key) => (origin[key] ?? null) !== (expected.origin[key] ?? null))) {
        throw new Error("Twig Bench origin or captured target changed; explicitly load a new observation.");
    }
    if (value.itemKey?.connectionId !== origin.connectionRef || value.itemKey?.bindingId !== origin.bindingId ||
        value.identityId !== origin.identityId) {
        throw new Error("Explicit-Bench detail identity contradicts its origin.");
    }
    validateObservation(value, workItemId, cwd);
    const membership = value.membership;
    const c = value.completeness;
    if (!record(membership) || !["selected", "seed", "pending"].every((key) => typeof membership[key] === "boolean") ||
        !(membership.selected || membership.seed || membership.pending)) {
        throw new Error("Explicit-Bench detail does not prove Bench membership.");
    }
    if (value.layoutStatus !== "unavailable" ||
        !record(c) || c.sourceComplete !== false || c.sourceChildCount !== null ||
        !Number.isSafeInteger(c.cachedChildCount) || c.cachedChildCount < 0 ||
        !(c.parentCached === null || typeof c.parentCached === "boolean") ||
        typeof c.linksVerified !== "boolean" ||
        !Array.isArray(c.reasons) || !c.reasons.every(nonempty) ||
        !["source-unknown", "cache-only"].every((reason) => c.reasons.includes(reason))) {
        throw new Error("Explicit-Bench detail completeness evidence is missing or claims an empty or complete source.");
    }
}

function samePath(a, b) {
    const normalize = (value) => process.platform === "win32" ? path.resolve(value).toLowerCase() : path.resolve(value);
    return normalize(a) === normalize(b);
}
function record(value) { return value !== null && typeof value === "object" && !Array.isArray(value); }
function nonempty(value) { return typeof value === "string" && value.trim().length > 0; }
