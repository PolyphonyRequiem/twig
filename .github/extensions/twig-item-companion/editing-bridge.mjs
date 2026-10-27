import { randomBytes } from "node:crypto";
import { serializeStageRequest, validateEditingResult, validateRetainResult } from "./editing-document.mjs";

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
    const retainOutcomes = new Map();
    const targetKey = (target) => JSON.stringify([target.expectedKey.checkoutRoot, target.expectedKey.connectionId,
        target.expectedKey.bindingId, target.expectedKey.workItemId, target.bench, target.identityId]);
    const inspectFirst = "Inspect the local proposal journal before deciding any new capture. Outcome cannot be cleared by reopening. Unsent input is retained; no automatic retry.";
    let idle = Promise.resolve();
    function invalidate() {
        epoch++;
        capture = null;
        if (lastOutcome) lastOutcome = { ...lastOutcome, captureHandle: null, canStage: false, canRetain: false };
        controller?.abort();
    }
    function view(result) {
        if (!capture) return { status: result.status, canStage: false, error: result.error };
        const { document } = capture;
        return {
            status: result.status, committed: result.committed,
            captureHandle: capture.handle, itemKey: document.nativeContext.itemKey,
            canStage: !document.hasConflicts,
            canRetain: !document.hasConflicts && document.changes.length > 0 &&
                document.changes.every(change => change.baselineValue !== change.proposedValue),
            hasConflicts: document.hasConflicts,
            generation: document.generation,
            baselineRevision: document.baselineRevision,
            observedRevision: document.observedRevision,
            editors: editableControls(document),
            changes: document.changes, conflicts: document.conflicts,
        };
    }
    async function run(operation, target, retaining = false) {
        if (!adapter) throw new Error("Native editing adapter is unavailable; installed Twig support is not qualified.");
        if (active) throw new Error("A native editing operation is already in progress.");
        active = true;
        let releaseIdle;
        idle = new Promise(resolve => { releaseIdle = resolve; });
        controller = new AbortController();
        const generation = epoch;
        try {
            const result = await operation(controller.signal);
            if (retaining) {
                const outcome = { ...result, itemKey: target.expectedKey, benchId: target.bench,
                    identityId: target.identityId, captureHandle: null, canStage: false, canRetain: false,
                    inspectFirst: result.status === "indeterminate" };
                if (outcome.inspectFirst) outcome.recovery = inspectFirst;
                retainOutcomes.set(targetKey(target), structuredClone(outcome));
                capture = null;
                lastOutcome = outcome;
                return outcome;
            }
            if (generation !== epoch) throw new Error("Editing target changed; retain unsent input and explicitly reopen.");
            lastOutcome = { ...result, itemKey: target.expectedKey, benchId: target.bench, identityId: target.identityId };
            return lastOutcome;
        } catch (error) {
            capture = null;
            lastOutcome = { status: "unknown", canStage: false, itemKey: target.expectedKey,
                benchId: target.bench, identityId: target.identityId,
                error: { message: error instanceof Error ? error.message : "Native outcome unavailable." } };
            if (retaining) {
                lastOutcome = { ...lastOutcome, canRetain: false, captureHandle: null,
                    inspectFirst: true, recovery: inspectFirst };
                retainOutcomes.set(targetKey(target), structuredClone(lastOutcome));
            }
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
        get retainOutcomes() { return structuredClone([...retainOutcomes.values()]); },
        open(target) {
            if (active) throw new Error("A native editing operation is already in progress.");
            invalidate();
            const held = retainOutcomes.get(targetKey(target));
            if (held?.inspectFirst) {
                lastOutcome = structuredClone(held);
                return Promise.resolve(lastOutcome);
            }
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
                capture = { handle: randomBytes(32).toString("base64url"), document: result.document,
                    targetKey: targetKey(target) };
                return view(result);
            }, target);
        },
        async stageExpected(body) {
            const refused = (message) => ({
                status: "refused", committed: false, canStage: false,
                error: { code: "draft-precondition", message, recovery: "Explicitly open the current local draft and review its origin before retrying." },
            });
            const expectedKeys = ["changes", "expectedBaselineRevision", "expectedBenchId", "expectedGeneration",
                "expectedIdentityId", "expectedItemKey", "expectedObservedRevision"];
            if (!body || Object.keys(body).sort().join(",") !== expectedKeys.join(",") ||
                !body.expectedItemKey || Object.keys(body.expectedItemKey).sort().join(",") !==
                    ["bindingId", "checkoutRoot", "connectionId", "workItemId"].join(",") ||
                !Number.isInteger(body.expectedItemKey.workItemId) || body.expectedItemKey.workItemId <= 0) {
                return refused("Exact draft preconditions and changes are required; reopen the current draft.");
            }
            if (!capture) return refused("No current server-held draft capture exists; explicitly open the local draft.");
            const document = capture.document;
            const context = document.nativeContext;
            const expectedTargetKey = targetKey({
                expectedKey: body.expectedItemKey, bench: body.expectedBenchId, identityId: body.expectedIdentityId,
            });
            const sameItemKey = ["checkoutRoot", "connectionId", "bindingId", "workItemId"]
                .every((key) => body.expectedItemKey[key] === context.itemKey[key]);
            if (capture.targetKey !== expectedTargetKey || !sameItemKey ||
                body.expectedBenchId !== context.benchId || body.expectedIdentityId !== context.identityId ||
                body.expectedGeneration !== document.generation ||
                body.expectedBaselineRevision !== document.baselineRevision ||
                body.expectedObservedRevision !== document.observedRevision) {
                return refused("The workspace, Bench, item, identity, draft generation or revision changed; explicitly reopen and review the local draft.");
            }
            if (document.hasConflicts) return refused("Native revision drift/conflicts require explicit reopen or reconciliation.");
            const request = {
                version: 1, nativeContext: context, generation: document.generation,
                baselineRevision: document.baselineRevision, observedRevision: document.observedRevision,
                changes: body.changes,
            };
            try {
                serializeStageRequest(request);
            } catch (error) {
                return refused(error instanceof Error ? error.message : "Draft changes are invalid.");
            }
            const allowed = editableControls(document);
            if (request.changes.some((change) => !allowed.some((field) => field.fieldRef === change.fieldRef))) {
                return refused("The native form does not expose every requested field as a supported editable control.");
            }
            return this.stage({ captureHandle: capture.handle, changes: body.changes });
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
                capture = { handle: randomBytes(32).toString("base64url"), document: result.document,
                    targetKey: targetKey({ expectedKey: document.nativeContext.itemKey,
                        bench: document.nativeContext.benchId, identityId: document.nativeContext.identityId }) };
                return view(result);
            }, { expectedKey: document.nativeContext.itemKey, bench: document.nativeContext.benchId,
                identityId: document.nativeContext.identityId });
        },
        async retain(body) {
            if (active) throw new Error("A native editing operation is already in progress.");
            if (!body || Object.keys(body).join(",") !== "captureHandle" ||
                typeof body.captureHandle !== "string" || !capture || body.captureHandle !== capture.handle) {
                throw new Error("A current server-held editing capture is required; explicitly reopen.");
            }
            const document = capture.document;
            if (document.hasConflicts || !document.changes.length ||
                document.changes.some(change => change.baselineValue === change.proposedValue)) {
                throw new Error("RETAIN requires effective staged native changes without conflicts.");
            }
            const target = { expectedKey: document.nativeContext.itemKey, bench: document.nativeContext.benchId,
                identityId: document.nativeContext.identityId };
            const request = { version: 1, nativeContext: document.nativeContext, generation: document.generation,
                baselineRevision: document.baselineRevision, observedRevision: document.observedRevision };
            capture = null;
            return run(async (signal) => {
                const result = await adapter.retain({ request, workspacePath, signal });
                const exitCode = result.error ? (["usage", "invalid-json", "input-too-large"]
                    .includes(result.error.code) ? 2 : 1) : 0;
                validateRetainResult(result, { exitCode, expectedRequest: request });
                const receipt = result.capture && {
                    digest: result.capture.digest, operationId: result.capture.operationId,
                    effects: structuredClone(result.capture.effects), originCoverage: result.capture.originCoverage,
                    authorized: false, canApply: false,
                };
                return { status: result.status, committed: result.committed, error: result.error,
                    ...(receipt ? { receipt } : {}) };
            }, target, true);
        },
    };
}
