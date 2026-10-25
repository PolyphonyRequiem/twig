import { CanvasError, createCanvas, joinSession } from "@github/copilot-sdk/extension";
import { createCanvasServer } from "./canvas-server.mjs";
import { createNativeDetailReader } from "./native-detail.mjs";
import { createNativeBenchReader } from "./native-bench.mjs";
import { createNativeBenchDetailReader } from "./native-bench-detail.mjs";
import { createNativeDraftReader } from "./native-draft.mjs";
import { createNativeProposalReader } from "./native-proposal.mjs";
import { createReviewServer } from "./review-server.mjs";
import { createNativeEditingAdapter } from "./native-editing.mjs";

const instances = new Map();
const workbenchInstances = new Map();
const reviewInstances = new Map();
const readNativeDetail = createNativeDetailReader();
const readNativeBench = createNativeBenchReader();
const readNativeBenchDetail = createNativeBenchDetailReader();
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
            instance = await createCanvasServer({
                workspacePath: session.workspacePath,
                readNativeDetail,
                readNativeDraft: createNativeDraftReader(),
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

function asCanvasError(error) {
    if (error instanceof CanvasError) return error;
    return new CanvasError("native_read_failed", error instanceof Error ? error.message : "Twig detail could not be read.");
}

const workbenchCanvas = createCanvas({
    id: "twig-workbench",
    displayName: "Twig workbench",
    description: "Browse a named native Bench without switching context. Explicit OPEN/STAGE can update local drafts; never Save, apply or publish.",
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
    ],
    open: async (ctx) => {
        if (!session?.workspacePath) {
            throw new CanvasError("workspace_unavailable", "Twig workbench requires a project session with an admitted workspace.");
        }
        let instance = workbenchInstances.get(ctx.instanceId);
        if (!instance) {
            instance = await createCanvasServer({ workspacePath: session.workspacePath, readNativeBench, readNativeBenchDetail,
                nativeEditing: createNativeEditingAdapter(), handoffSession: session, workbench: true });
            workbenchInstances.set(ctx.instanceId, instance);
        }
        return { url: instance.url, title: "Twig workbench", status: "Cached browsing; local draft OPEN/STAGE are explicit and require installed native support" };
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
    description: "Review one retained native proposal; explicit acquisition may write its local journal but never authorizes or applies.",
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
    }],
    open: async (ctx) => {
        if (!session?.workspacePath) throw new CanvasError("workspace_unavailable", "Proposal review requires a project session with an admitted workspace.");
        let instance = reviewInstances.get(ctx.instanceId);
        if (!instance) {
            instance = await createReviewServer({ workspacePath: session.workspacePath, readNativeProposal: createNativeProposalReader() });
            reviewInstances.set(ctx.instanceId, instance);
        }
        return { url: instance.url, title: "Twig proposal review", status: "Retained evidence only; preview acquisition is explicit and may write the local journal" };
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
