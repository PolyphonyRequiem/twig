import { randomBytes } from "node:crypto";
import { serializeStageRequest, validateEditingResult } from "./editing-document.mjs";

function editableControls(document) {
    const controls = [...document.systemControls];
    for (const page of document.pages) {
        if (!page.visible || page.isContribution) continue;
        for (const section of page.sections) for (const group of section.groups) {
            if (group.visible && !group.isContribution) controls.push(...group.controls);
        }
    }
    const seen = new Set();
    return controls.filter((control) => {
        const ref = control.id.toLowerCase();
        const eligible = document.fields.find((field) => field.fieldRef.toLowerCase() === ref);
        if (seen.has(ref) || !control.visible || control.isContribution || control.readOnly ||
            control.controlType !== "FieldControl" || ![0, 1].includes(control.value?.state) ||
            eligible?.editable !== true) return false;
        seen.add(ref);
        return true;
    }).map((control) => ({
        fieldRef: control.id, label: control.label || control.id,
        value: control.value.state === 1 ? null : control.value.full,
    }));
}

// Public revisions are display evidence, never request authority.
export function createEditingBridge({ adapter, workspacePath }) {
    let epoch = 0;
    let capture = null;
    let active = false;
    let controller = null;
    let lastOutcome = null;
    let idle = Promise.resolve();
    function invalidate() {
        epoch++;
        capture = null;
        if (lastOutcome) lastOutcome = { ...lastOutcome, captureHandle: null, canStage: false };
        controller?.abort();
    }
    function view(result) {
        if (!capture) return { status: result.status, canStage: false, error: result.error };
        const { document } = capture;
        return {
            status: result.status, committed: result.committed,
            captureHandle: capture.handle, itemKey: document.nativeContext.itemKey,
            canStage: !document.hasConflicts,
            hasConflicts: document.hasConflicts,
            observedRevision: document.observedRevision,
            editors: editableControls(document),
            changes: document.changes, conflicts: document.conflicts,
        };
    }
    async function run(operation, target) {
        if (!adapter) throw new Error("Native editing adapter is unavailable; installed Twig support is not qualified.");
        if (active) throw new Error("A native editing operation is already in progress.");
        active = true;
        let releaseIdle;
        idle = new Promise(resolve => { releaseIdle = resolve; });
        controller = new AbortController();
        const generation = epoch;
        try {
            const result = await operation(controller.signal);
            if (generation !== epoch) throw new Error("Editing target changed; retain unsent input and explicitly reopen.");
            lastOutcome = { ...result, itemKey: target.expectedKey, benchId: target.bench, identityId: target.identityId };
            return lastOutcome;
        } catch (error) {
            capture = null;
            lastOutcome = { status: "unknown", canStage: false, itemKey: target.expectedKey,
                benchId: target.bench, identityId: target.identityId,
                error: { message: error instanceof Error ? error.message : "Native outcome unavailable." } };
            throw error;
        } finally {
            controller = null;
            active = false;
            releaseIdle();
        }
    }
    return {
        invalidate,
        awaitIdle: () => idle,
        get busy() { return active; },
        get outcome() { return lastOutcome; },
        open(target) {
            if (active) throw new Error("A native editing operation is already in progress.");
            invalidate();
            return run(async (signal) => {
                const result = await adapter.open({ ...target, workspacePath, signal });
                if (result.status !== "opened") {
                    return { status: result.status, committed: result.committed, error: result.error, canStage: false };
                }
                validateEditingResult(result, {
                    action: "open", exitCode: 0, expectedKey: target.expectedKey, bench: target.bench,
                });
                if (result.document?.nativeContext.identityId !== target.identityId) {
                    throw new Error("Native OPEN identity changed; outcome is unknown. Retain input and reopen.");
                }
                capture = { handle: randomBytes(32).toString("base64url"), document: result.document };
                return view(result);
            }, target);
        },
        async stage(body) {
            if (!body || Object.keys(body).sort().join(",") !== "captureHandle,changes" ||
                typeof body.captureHandle !== "string" || !capture || body.captureHandle !== capture.handle) {
                throw new Error("A current server-held editing capture is required; explicitly reopen.");
            }
            const document = capture.document;
            if (document.hasConflicts) throw new Error("Native revision drift/conflicts require explicit reopen or reconciliation.");
            const request = {
                version: 1, nativeContext: document.nativeContext,
                generation: document.generation, baselineRevision: document.baselineRevision,
                observedRevision: document.observedRevision, changes: body.changes,
            };
            serializeStageRequest(request);
            const allowed = editableControls(document);
            if (request.changes.some((change) => !allowed.some((field) => field.fieldRef === change.fieldRef))) {
                throw new Error("The native form does not expose this field as a supported editable control.");
            }
            return run(async (signal) => {
                const result = await adapter.stage({ request, workspacePath, signal });
                if (result.status !== "staged") {
                    capture = null;
                    return { status: result.status, committed: result.committed, error: result.error, canStage: false };
                }
                validateEditingResult(result, { action: "stage", exitCode: 0,
                    expectedContext: document.nativeContext, expectedRequest: request });
                capture = { handle: randomBytes(32).toString("base64url"), document: result.document };
                return view(result);
            }, { expectedKey: document.nativeContext.itemKey, bench: document.nativeContext.benchId,
                identityId: document.nativeContext.identityId });
        },
    };
}
