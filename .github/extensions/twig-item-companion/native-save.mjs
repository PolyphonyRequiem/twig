const record = value => value !== null && typeof value === "object" && !Array.isArray(value);
const keys = (value, names) => record(value) && Object.keys(value).sort().join(",") === names.sort().join(",");
export function validateSaveAuthorization(value, digest) {
    if (!keys(value, ["digest", "mode", "authorizerIdentity", "rationale", "authorizedAt"]) ||
        value.digest !== digest || value.mode !== 0 ||
        value.authorizerIdentity !== "workbench-ui-user (individual identity unverified)" ||
        value.rationale !== "Explicit Save gesture; caller/surface attribution only, not host-attested identity." ||
        typeof value.authorizedAt !== "string" || !Number.isFinite(Date.parse(value.authorizedAt))) {
        throw new Error("Save requires the separate UI's explicit authorization of this exact retained digest.");
    }
    return structuredClone(value);
}

export function validateSaveResult(value, { exitCode, digest, callerIdentity, operationId }) {
    if (!record(value) || value.version !== 1 || value.digest !== digest ||
        value.callerIdentity !== callerIdentity || value.surface !== "twig-workbench-explicit-save" ||
        value.hostAttested !== false || typeof value.verified !== "boolean" || typeof value.settled !== "boolean" ||
        !["saved", "verified-draft-retained", "not-verified"].includes(value.status) ||
        !Array.isArray(value.operations) || !record(value.unsettledAcknowledgments) ||
        ![0, 1].includes(exitCode)) throw new Error("Native Save response identity or shape is invalid.");
    if (value.operations.length > 1 || value.operations.some(row =>
        !record(row) || row.opId !== operationId || row.ordinal !== 0 ||
        !["Planned", "Confirmed", "Applying", "Applied", "Verified", "Failed", "Indeterminate"].includes(row.state))) {
        throw new Error("Native Save operation coverage is invalid.");
    }
    const verified = value.error === null && value.operations.length === 1 &&
        value.operations[0].state === "Verified" && value.operations[0].error === null &&
        Object.keys(value.unsettledAcknowledgments).length === 0;
    const settled = verified && value.draftSettlement === "settled" && value.draftSettlementError === null;
    if (value.verified !== verified || value.settled !== settled ||
        value.status !== (settled ? "saved" : verified ? "verified-draft-retained" : "not-verified") ||
        exitCode !== (settled ? 0 : 1)) throw new Error("Native Save success contradicts verified effects or draft settlement.");
    return structuredClone(value);
}

export function createNativeDraftSaver(invoke, resolveWorkspace) {
    return async ({ capture, authorization, workspacePath, signal }) => {
        const request = {
            version: 1, digest: capture.digest, callerIdentity: capture.nativeContext.identityId,
            surface: "twig-workbench-explicit-save",
            authorization: validateSaveAuthorization(authorization, capture.digest),
        };
        const cwd = await resolveWorkspace(workspacePath, capture.nativeContext.itemKey.checkoutRoot);
        return invoke("draft-save", ["draft", "save", "--stdin", "-o", "json"], cwd,
            signal, JSON.stringify(request),
            { digest: capture.digest, callerIdentity: request.callerIdentity, operationId: capture.operationId },
            validateSaveResult);
    };
}
