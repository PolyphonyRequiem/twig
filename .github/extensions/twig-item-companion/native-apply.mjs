import { realpath as nodeRealpath } from "node:fs/promises";
import { createNativeCommandRunner } from "./native-editing.mjs";

const record = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const text = (value, limit) => typeof value === "string" && value.trim().length > 0 &&
    value.length <= limit && !value.includes("\0");

export function validateApplyRequest(input, observation) {
    const keys = record(input) ? Object.keys(input).sort().join(",") : "";
    if (keys !== "captureId,confirm" || !text(input.captureId, 128) ||
        typeof input.confirm !== "string" || !/^[a-f0-9]{64}$/.test(input.confirm)) {
        throw new Error("Apply requires only the retained captureId and exact confirm digest.");
    }
    const envelope = observation?.retained?.envelope;
    if (!envelope || input.captureId !== observation.expected.captureId ||
        input.confirm !== observation.expected.digest || envelope.digest !== input.confirm) {
        throw new Error("Apply confirmation does not match this panel's retained capture and digest.");
    }
    if (envelope.reviewModel.modelVersion !== 1 || !envelope.reviewModel.operations.length || !envelope.canApply ||
        envelope.issues.length || envelope.pendingChanges.length || envelope.reviewModel.blockers.length) {
        throw new Error("This retained preview is blocked or not executable. No apply was started.");
    }
    return Object.freeze({ ...input });
}

export function validateApplyResult(value, { digest, operations, exitCode }) {
    if (!record(value) || value.digest !== digest || typeof value.failed !== "boolean" ||
        !(value.error === null || typeof value.error === "string") || !Array.isArray(value.operations) ||
        ![0, 1, 2].includes(exitCode)) throw new Error("Invalid native Apply response.");
    const states = ["Planned", "Confirmed", "Applying", "Applied", "Verified", "Failed", "Indeterminate"];
    const expected = new Map(operations.map((op) => [op.opId, op]));
    const seen = new Set();
    for (const row of value.operations) {
        const operation = record(row) && expected.get(row.opId);
        if (!operation || seen.has(row.opId) || row.ordinal !== operation.ordinal ||
            !states.includes(row.state) || !(row.error === null || typeof row.error === "string") ||
            !(row.warning === null || typeof row.warning === "string")) {
            throw new Error("Native Apply operation coverage or identity is invalid.");
        }
        seen.add(row.opId);
    }
    if (value.unsettledAcknowledgments !== undefined &&
        (!record(value.unsettledAcknowledgments) || !Object.entries(value.unsettledAcknowledgments)
            .every(([id, evidence]) => expected.has(id) && typeof evidence === "string"))) {
        throw new Error("Invalid unsettled acknowledgment evidence.");
    }
    const verified = exitCode === 0 && !value.failed && value.error === null &&
        value.operations.length === operations.length && value.operations.every((row) => row.state === "Verified" && row.error === null) &&
        Object.keys(value.unsettledAcknowledgments ?? {}).length === 0;
    if (exitCode === 0 && !verified) throw new Error("Native Apply returned exit zero without complete verified effects.");
    return Object.freeze({ status: verified ? "verified" : "not-verified", exitCode,
        // Preserve the full native journal outcome, including partial effects and acknowledgments.
        native: structuredClone(value) });
}

export function createNativeProposalApplier(options = {}) {
    const { realpath = nodeRealpath } = options;
    const invoke = createNativeCommandRunner(options);
    return async function applyProposal({ input, observation, workspacePath, signal }) {
        const request = validateApplyRequest(input, observation);
        const cwd = await realpath(workspacePath);
        const file = await realpath(observation.acquisition.file);
        if (cwd !== observation.acquisition.checkoutRoot || file !== observation.acquisition.file) {
            throw new Error("Apply checkout or physical proposal file changed since acquisition.");
        }
        signal?.throwIfAborted();
        const args = ["proposal", "apply", "--file", file, "--confirm", request.confirm, "-o", "json"];
        return invoke("proposal-apply", args, cwd, signal, undefined,
            { digest: request.confirm, operations: observation.retained.envelope.reviewModel.operations }, validateApplyResult);
    };
}
