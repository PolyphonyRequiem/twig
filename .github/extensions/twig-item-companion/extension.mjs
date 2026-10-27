import { CanvasError, createCanvas, joinSession } from "@github/copilot-sdk/extension";
import { createCanvasServer } from "./canvas-server.mjs";
import { createNativeDetailReader } from "./native-detail.mjs";
import { createNativeBenchReader } from "./native-bench.mjs";
import { createNativeBenchDetailReader } from "./native-bench-detail.mjs";
import { createNativeDraftReader } from "./native-draft.mjs";
import { createNativeProposalReader } from "./native-proposal.mjs";
import { createNativeProposalApplier } from "./native-apply.mjs";
import { createReviewServer } from "./review-server.mjs";
import { createNativeEditingAdapter } from "./native-editing.mjs";
import { createNativeDiscussionReader } from "./native-discussion.mjs";
import { admitTwigExecutable, ExecutableError } from "./native-executable.mjs";

const instances = new Map();
const workbenchInstances = new Map();
const reviewInstances = new Map();
let session;

const canvas = createCanvas({
    id: "twig-item-companion",
    displayName: "Twig item companion",
    description: "Browse cached native item detail; explicit draft inspection may update local observation/conflict metadata, never proposed intent.",
    actions: [
        {
            name: "inspect_draft",
            description: "Explicitly inspect the loaded item's native draft. May write local observation/conflict metadata; not zero-write or guaranteed offline. Reopen the panel for the retained result. No edit, capture, Save or apply authority.",
            inputSchema: { type: "object", properties: {}, additionalProperties: false },
            handler: async (ctx) => {
                try { return await getInstance(ctx.instanceId).inspectDraft(); }
                catch (error) { throw asCanvasError(error); }
            },
        },
        {
            name: "load_detail",
            description: "Load one work item's read-only semantic detail from the current admitted Twig workspace.",
            inputSchema: {
                type: "object",
                properties: {
                    workItemId: {
                        type: "integer",
                        minimum: -2147483647,
                        maximum: 2147483647,
                        not: { const: 0 },
                    },
                },
                required: ["workItemId"],
                additionalProperties: false,
            },
            handler: async (ctx) => {
                const instance = getInstance(ctx.instanceId);
                try {
                    return await instance.load(ctx.input.workItemId);
                } catch (error) {
                    throw asCanvasError(error);
                }
            },
        },
        {
            name: "refresh_detail",
            description: "Re-read the displayed item's cached detail while requiring its captured Bench, binding, and identity.",
            inputSchema: {
                type: "object",
                properties: {},
                additionalProperties: false,
            },
            handler: async (ctx) => {
                const instance = getInstance(ctx.instanceId);
                try {
                    return await instance.refresh();
                } catch (error) {
                    throw asCanvasError(error);
                }
            },
        },
    ],
    open: async (ctx) => {
        if (!session?.workspacePath) {
            throw new CanvasError("workspace_unavailable", "Twig item detail requires a project session with an admitted workspace.");
        }

        let instance = instances.get(ctx.instanceId);
        if (!instance) {
            const executable = await admit("item");
            instance = await createCanvasServer({
                workspacePath: session.workspacePath,
                readNativeDetail: createNativeDetailReader({ executable }),
                readNativeDraft: createNativeDraftReader({ executable }),
            });
            instances.set(ctx.instanceId, instance);
        }

        return {
            url: instance.url,
            title: "Twig item companion",
            status: "Cached detail; draft inspection is explicit and may update metadata",
        };
    },
    onClose: async (ctx) => {
        const instance = instances.get(ctx.instanceId);
        if (instance) {
            instances.delete(ctx.instanceId);
            await instance.close();
        }
    },
});

function getInstance(instanceId) {
    const instance = instances.get(instanceId);
    if (!instance) {
        throw new CanvasError("canvas_not_open", "Open the Twig item companion before loading detail.");
    }

    return instance;
}

// Pins each canvas instance to the absolute real twig executable and its content hash. Opening only
// hashes the file; capability help is probed lazily before the first native command that needs it.
async function admit(canvasId) {
    try { return await admitTwigExecutable({ canvas: canvasId }); }
    catch (error) { throw asCanvasError(error); }
}

function asCanvasError(error) {
    if (error instanceof CanvasError) return error;
    if (error instanceof ExecutableError) return new CanvasError(error.code, error.message);
    return new CanvasError("native_read_failed", error instanceof Error ? error.message : "Twig detail could not be read.");
}

const workbenchCanvas = createCanvas({
    id: "twig-workbench",
    displayName: "Twig workbench",
    description: "Browse a named native Bench and explicitly open or stage local drafts; no automatic edits, Save, apply or publication.",
    actions: [
        {
            name: "load_bench",
            description: "Read an existing saved Bench by name or decimal ID; does not switch the current Bench.",
            inputSchema: {
                type: "object",
                properties: { bench: { type: "string", minLength: 1, maxLength: 512 } },
                required: ["bench"],
                additionalProperties: false,
            },
            handler: async (ctx) => {
                const instance = workbenchInstances.get(ctx.instanceId);
                if (!instance) throw new CanvasError("canvas_not_open", "Open the Twig workbench before browsing.");
                try { return await instance.loadBench(ctx.input.bench); }
                catch (error) { throw asCanvasError(error); }
            },
        },
        {
            name: "refresh_bench",
            description: "Re-read the captured Bench by stable ID, requiring its full captured origin.",
            inputSchema: { type: "object", properties: {}, additionalProperties: false },
            handler: async (ctx) => {
                const instance = workbenchInstances.get(ctx.instanceId);
                if (!instance) throw new CanvasError("canvas_not_open", "Open the Twig workbench before refreshing.");
                try { return await instance.refreshBench(); }
                catch (error) { throw asCanvasError(error); }
            },
        },
        {
            name: "open_local_draft",
            description: "Explicitly open one selected Bench member's native local draft. May persist local observation metadata; returns local effects and never publishes, authorizes or applies.",
            inputSchema: {
                type: "object",
                properties: {
                    workItemId: { type: "integer", minimum: 1, maximum: 2147483647 },
                },
                required: ["workItemId"],
                additionalProperties: false,
            },
            handler: async (ctx) => {
                const instance = workbenchInstances.get(ctx.instanceId);
                if (!instance) throw new CanvasError("canvas_not_open", "Open the Twig workbench before opening a local draft.");
                try { return await instance.openLocalDraft(ctx.input.workItemId); }
                catch (error) { throw asCanvasError(error); }
            },
        },
        {
            name: "stage_local_draft",
            description: "Explicitly stage allowlisted field changes into the current server-held local draft only. Exact logical-target and revision preconditions are required; never publishes, authorizes or applies.",
            inputSchema: {
                type: "object",
                properties: {
                    expectedItemKey: {
                        type: "object", additionalProperties: false,
                        required: ["checkoutRoot", "connectionId", "bindingId", "workItemId"],
                        properties: {
                            checkoutRoot: { type: "string", minLength: 1, maxLength: 4096 },
                            connectionId: { type: "string", minLength: 1, maxLength: 512 },
                            bindingId: { type: "string", minLength: 1, maxLength: 512 },
                            workItemId: { type: "integer", minimum: 1, maximum: 2147483647 },
                        },
                    },
                    expectedBenchId: { type: "string", pattern: "^[1-9][0-9]{0,18}$" },
                    expectedIdentityId: { type: "string", minLength: 1, maxLength: 512 },
                    expectedGeneration: { type: "string", pattern: "^(0|[1-9][0-9]{0,18})$" },
                    expectedBaselineRevision: { type: "string", pattern: "^[1-9][0-9]{0,18}$" },
                    expectedObservedRevision: { type: "string", pattern: "^[1-9][0-9]{0,18}$" },
                    changes: {
                        type: "array", minItems: 1, maxItems: 128,
                        items: {
                            type: "object", additionalProperties: false, required: ["fieldRef", "proposedValue"],
                            properties: {
                                fieldRef: { type: "string", minLength: 1, maxLength: 512 },
                                proposedValue: { anyOf: [{ type: "string", maxLength: 262144 }, { type: "null" }] },
                            },
                        },
                    },
                },
                required: ["expectedItemKey", "expectedBenchId", "expectedIdentityId", "expectedGeneration",
                    "expectedBaselineRevision", "expectedObservedRevision", "changes"],
                additionalProperties: false,
            },
            handler: async (ctx) => {
                const instance = workbenchInstances.get(ctx.instanceId);
                if (!instance) throw new CanvasError("canvas_not_open", "Open the Twig workbench before staging a local draft.");
                try { return await instance.stageLocalDraft(ctx.input); }
                catch (error) { throw asCanvasError(error); }
            },
        },
    ],
    open: async (ctx) => {
        if (!session?.workspacePath) {
            throw new CanvasError("workspace_unavailable", "Twig workbench requires a project session with an admitted workspace.");
        }
        let instance = workbenchInstances.get(ctx.instanceId);
        if (!instance) {
            const executable = await admit("workbench");
            instance = await createCanvasServer({ workspacePath: session.workspacePath,
                readNativeBench: createNativeBenchReader({ executable }),
                readNativeBenchDetail: createNativeBenchDetailReader({ executable }),
                readNativeDiscussion: createNativeDiscussionReader({ executable }),
                nativeEditing: createNativeEditingAdapter({ executable }), handoffSession: session, workbench: true });
            workbenchInstances.set(ctx.instanceId, instance);
        }
        return { url: instance.url, title: "Twig workbench", status: "Cached browsing; explicit local draft actions are not published" };
    },
    onClose: async (ctx) => {
        const instance = workbenchInstances.get(ctx.instanceId);
        if (instance) {
            workbenchInstances.delete(ctx.instanceId);
            await instance.close();
        }
    },
});

const reviewCanvas = createCanvas({
    id: "twig-proposal-review",
    displayName: "Twig proposal review",
    description: "Review one retained native proposal, then separately authorize and apply that exact capture through native digest and journal gates.",
    actions: [{
        name: "acquire_review",
        description: "Explicitly preview one workspace proposal at its exact expected digest. May import a local journal row/update last-preview ordering. Reopen or show retained state to view it; no authorization or apply.",
        inputSchema: {
            type: "object", additionalProperties: false, required: ["file", "digest", "workspace"],
            properties: {
                file: { type: "string", minLength: 1, maxLength: 4096 },
                digest: { type: "string", pattern: "^[a-f0-9]{64}$" },
                workspace: {
                    type: "object", additionalProperties: false, required: ["organization", "project"],
                    properties: { organization: { type: "string", minLength: 1, maxLength: 512 },
                        project: { type: "string", minLength: 1, maxLength: 512 } },
                },
            },
        },
        handler: async (ctx) => {
            const instance = reviewInstances.get(ctx.instanceId);
            if (!instance) throw new CanvasError("canvas_not_open", "Open Twig proposal review before explicit acquisition.");
            try { return await instance.acquire(ctx.input); }
            catch (error) { throw asCanvasError(error); }
        },
    }, {
        name: "apply_review",
        description: "Separately authorize and apply the exact retained capture. Requires explicit authorization for these effects; never invoke from acquisition or assessment alone. Writes the tracker through native gates; no automatic retry.",
        inputSchema: {
            type: "object", additionalProperties: false, required: ["captureId", "confirm", "authorize"],
            properties: {
                captureId: { type: "string", minLength: 1, maxLength: 128 },
                confirm: { type: "string", pattern: "^[a-f0-9]{64}$" },
                authorize: { type: "string", minLength: 1, maxLength: 512 },
                rationale: { type: "string", minLength: 1, maxLength: 2048 },
            },
        },
        handler: async (ctx) => {
            const instance = reviewInstances.get(ctx.instanceId);
            if (!instance) throw new CanvasError("canvas_not_open", "Open and acquire Twig proposal review before explicit Apply.");
            try { return await instance.apply(ctx.input); }
            catch (error) { throw asCanvasError(error); }
        },
    }],
    open: async (ctx) => {
        if (!session?.workspacePath) throw new CanvasError("workspace_unavailable", "Proposal review requires a project session with an admitted workspace.");
        let instance = reviewInstances.get(ctx.instanceId);
        if (!instance) {
            const executable = await admit("review");
            instance = await createReviewServer({ workspacePath: session.workspacePath,
                readNativeProposal: createNativeProposalReader({ executable }),
                applyNativeProposal: createNativeProposalApplier({ executable }) });
            reviewInstances.set(ctx.instanceId, instance);
        }
        return { url: instance.url, title: "Twig proposal review", status: "Explicit preview, then separate digest-bound authorization and native Apply" };
    },
    onClose: async (ctx) => {
        const instance = reviewInstances.get(ctx.instanceId);
        if (instance) {
            reviewInstances.delete(ctx.instanceId);
            await instance.close();
        }
    },
});

session = await joinSession({ canvases: [canvas, workbenchCanvas, reviewCanvas] });
