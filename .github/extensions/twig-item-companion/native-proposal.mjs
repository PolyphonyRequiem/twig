import { execFile as nodeExecFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import path from "node:path";
import { retainProposalReview } from "./retained-review.mjs";

export function validateReviewRequest(input) {
    if (!input || typeof input !== "object" || Array.isArray(input) ||
        Object.keys(input).sort().join(",") !== "digest,file,workspace" ||
        typeof input.file !== "string" || !input.file.trim() || input.file.length > 4096 || input.file.includes("\0") ||
        typeof input.digest !== "string" || !/^[a-f0-9]{64}$/.test(input.digest) ||
        !input.workspace || typeof input.workspace !== "object" || Array.isArray(input.workspace) ||
        Object.keys(input.workspace).sort().join(",") !== "organization,project" ||
        !["organization", "project"].every((key) =>
            typeof input.workspace[key] === "string" && input.workspace[key].trim() && input.workspace[key].length <= 512)) {
        throw new Error("Review requires a proposal file, exact lowercase SHA-256 digest, organization and project; no other input is accepted.");
    }
    return Object.freeze({ file: input.file, digest: input.digest,
        workspace: Object.freeze({ ...input.workspace }) });
}

export function createNativeProposalReader({ execFile = nodeExecFile, realpath = nodeRealpath } = {}) {
    return async function acquireReview({ input, workspacePath, signal }) {
        const request = validateReviewRequest(input);
        if (typeof workspacePath !== "string" || !workspacePath.trim()) {
            throw new Error("Proposal review requires this session's admitted Twig workspace.");
        }
        const cwd = await realpath(workspacePath);
        const file = await realpath(path.resolve(cwd, request.file));
        const relative = path.relative(cwd, file);
        if (!relative || relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
            throw new Error("The physical proposal file must be inside this session's physical workspace.");
        }
        signal?.throwIfAborted();
        const rawJson = await new Promise((resolve, reject) => {
            execFile("twig", ["proposal", "preview", "--file", file, "--expect-digest", request.digest, "-o", "json"], {
                cwd, encoding: "utf8", shell: false, timeout: 15_000,
                maxBuffer: 4 * 1024 * 1024, windowsHide: true, signal,
            }, (error, stdout, stderr) => {
                if (error) {
                    reject(new Error(typeof stderr === "string" && stderr.trim()
                        ? stderr.trim() : `Twig proposal preview failed: ${error.message}`, { cause: error }));
                } else {
                    resolve(typeof stdout === "string" ? stdout : "");
                }
            });
        });
        signal?.throwIfAborted();
        const captureId = randomUUID();
        const expected = Object.freeze({
            captureId, expectedCaptureId: captureId, expectedModelVersion: 1,
            digest: request.digest, workspace: request.workspace, sourceKind: "host-supplied-native-preview",
        });
        const retained = retainProposalReview(rawJson, expected);
        return Object.freeze({
            rawJson, expected, retained,
            acquisition: Object.freeze({ checkoutRoot: cwd, file, source: "native-subprocess-preview",
                originCoverage: "Physical session checkout and reported workspace/digest only; full native origin is not exported.",
                localEffects: "Explicit preview may import a local journal row and update last-preview ordering; it never authorizes or applies.",
                freshness: "Retained observation only; current file, revisions and readiness have not been rechecked." }),
        });
    };
}
