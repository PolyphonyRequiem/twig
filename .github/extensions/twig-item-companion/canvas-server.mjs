import { randomBytes } from "node:crypto";
import { createServer } from "node:http";
import { renderPage } from "./render.mjs";
import { captureBench } from "./native-bench.mjs";
import { validateDraftDocument } from "./draft-document.mjs";
import { createEditingBridge } from "./editing-bridge.mjs";
import { createSessionHandoff } from "./session-handoff.mjs";

const MAX_REQUEST_BYTES = 4 * 1024;

export async function createCanvasServer({ workspacePath, readNativeDetail, readNativeBench, readNativeBenchDetail, readNativeDraft, nativeEditing, handoffSession, workbench = false }) {
    const token = randomBytes(32).toString("base64url");
    const editing = createEditingBridge({ adapter: nativeEditing, workspacePath });
    const handoff = createSessionHandoff({ session: handoffSession });
    let editingTarget = null;
    let selectionGeneration = 0;
    let closing = null;
    function invalidateEditing() {
        selectionGeneration++;
        editingTarget = null;
        editing.invalidate();
        handoff.invalidate();
    }
    const state = {
        observation: null,
        benchObservation: null,
        error: null,
        busy: false,
        closed: false,
        controller: null,
        draftDocument: null,
        draftError: null,
        draftGeneration: 0,
        draftController: null,
    };

    function clearDraft() {
        state.draftGeneration++;
        state.draftDocument = null;
        state.draftError = null;
        if (state.draftController) {
            state.draftController.abort();
        }
    }

    const load = async (workItemId, expected) => {
        clearDraft();
        if (state.closed) throw new Error("This Twig item companion is closed.");
        if (state.busy) throw new HttpError(409, "A Twig detail read is already in progress.");

        state.busy = true;
        state.error = null;
        state.controller = new AbortController();
        try {
            const observation = await readNativeDetail({
                workItemId,
                workspacePath,
                expected,
                signal: state.controller.signal,
            });
            state.observation = observation;
            return observation;
        } catch (error) {
            state.error = error instanceof Error ? error.message : "Twig detail could not be read.";
            throw error;
        } finally {
            state.controller = null;
            state.busy = false;
        }
    };

    const refresh = async () => {
        if (!state.observation) {
            clearDraft();
            throw new Error("Load an item before refreshing detail.");
        }
        const current = state.observation;
        return load(current.itemKey.workItemId, {
            benchId: current.benchId,
            bindingId: current.itemKey.bindingId,
            identityId: current.identityId,
            connectionId: current.itemKey.connectionId,
        });
    };

    const inspectDraft = async () => {
        clearDraft();
        const generation = state.draftGeneration;
        let controller;
        try {
            if (state.closed) throw new Error("This Twig item companion is closed.");
            if (workbench || !readNativeDraft) throw new Error("Native draft inspection is unavailable.");
            if (state.busy) throw new HttpError(409, "A Twig read is already in progress.");
            if (!state.observation) throw new Error("Load native item detail before explicitly inspecting its draft.");
            const observation = state.observation;
            const expectedKey = { ...observation.itemKey };
            controller = new AbortController();
            state.draftController = state.controller = controller;
            state.busy = true;
            const document = await readNativeDraft({
                workItemId: expectedKey.workItemId, workspacePath, expectedKey, signal: controller.signal,
            });
            if (state.closed || controller.signal.aborted || generation !== state.draftGeneration ||
                state.observation !== observation) throw new Error("Draft inspection was superseded or closed.");
            state.draftDocument = validateDraftDocument(document, expectedKey);
            return state.draftDocument;
        } catch (error) {
            if (!state.closed && generation === state.draftGeneration) {
                state.draftDocument = null;
                state.draftError = error instanceof Error ? error.message : "Native draft inspection failed.";
            }
            throw error;
        } finally {
            if (controller && state.controller === controller) {
                state.controller = state.draftController = null;
                state.busy = false;
            }
        }
    };

    const loadBench = async (bench, expected) => {
        if (editing.busy) throw new HttpError(409, "A native editing operation is already in progress.");
        invalidateEditing();
        if (!workbench || !readNativeBench) throw new Error("Native workbench browsing is unavailable.");
        if (state.closed) throw new Error("This Twig workbench is closed.");
        if (state.busy) throw new HttpError(409, "A Twig read is already in progress.");
        state.busy = true;
        state.error = null;
        const controller = new AbortController();
        state.controller = controller;
        try {
            const observation = await readNativeBench({
                bench, workspacePath, expected, signal: controller.signal,
            });
            if (state.closed || controller.signal.aborted) throw new Error("This Twig workbench is closed.");
            state.benchObservation = observation;
            return observation;
        } catch (error) {
            state.error = error instanceof Error ? error.message : "Native Bench read failed.";
            throw error;
        } finally {
            state.controller = null;
            state.busy = false;
        }
    };
    const refreshBench = async () => {
        if (!state.benchObservation) throw new Error("Load a saved Bench before re-reading its cache.");
        return loadBench(state.benchObservation.benchId, captureBench(state.benchObservation));
    };

    const loadBenchDetail = async (workItemId) => {
        if (editing.busy) throw new HttpError(409, "A native editing operation is already in progress.");
        invalidateEditing();
        const targetGeneration = selectionGeneration;
        if (!workbench || !readNativeBenchDetail) throw new Error("Native explicit-Bench detail is unavailable.");
        if (state.closed) throw new Error("This Twig workbench is closed.");
        if (!state.benchObservation) throw new Error("Load a saved Bench before reading item detail.");
        if (state.busy) throw new HttpError(409, "A Twig read is already in progress.");
        state.busy = true;
        const controller = new AbortController();
        state.controller = controller;
        try {
            const detail = await readNativeBenchDetail({
                workItemId, workspacePath, expected: captureBench(state.benchObservation), signal: controller.signal,
            });
            if (state.closed || controller.signal.aborted) throw new Error("This Twig workbench is closed.");
            if (targetGeneration !== selectionGeneration) throw new Error("Detail selection was superseded.");
            editingTarget = {
                workItemId: detail.itemKey.workItemId, bench: detail.benchId,
                expectedKey: structuredClone(detail.itemKey), identityId: detail.identityId,
            };
            return detail;
        } finally {
            state.controller = null;
            state.busy = false;
        }
    };

    const server = createServer((request, response) => {
        void routeRequest(request, response).catch((error) => {
            if (!response.headersSent) {
                const status = error instanceof HttpError ? error.status : 500;
                sendJson(response, status, { error: error instanceof Error ? error.message : "Request failed." });
            } else {
                response.destroy();
            }
        });
    });

    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", () => {
            server.removeListener("error", reject);
            resolve();
        });
    });

    const address = server.address();
    if (!address || typeof address === "string") {
        await closeServer(server);
        throw new Error("Could not determine the Twig companion loopback address.");
    }
    const host = `127.0.0.1:${address.port}`;
    const origin = `http://${host}`;

    async function routeRequest(request, response) {
        setSecurityHeaders(response);
        if (request.headers.host !== host) {
            sendJson(response, 403, { error: "Invalid local host." });
            return;
        }

        const requestOrigin = request.headers.origin;
        if (requestOrigin !== undefined && requestOrigin !== origin) {
            sendJson(response, 403, { error: "Cross-origin requests are not allowed." });
            return;
        }

        if (request.method === "GET" && request.url === "/") {
            const nonce = randomBytes(18).toString("base64url");
            response.writeHead(200, {
                "Content-Type": "text/html; charset=utf-8",
                "Content-Security-Policy": contentSecurityPolicy(nonce),
            });
            response.end(renderPage({ token, nonce, workbench }));
            return;
        }

        if (!hasValidToken(request, token)) {
            sendJson(response, 401, { error: "Canvas instance authorization is required." });
            return;
        }

        if (request.method === "GET" && request.url === "/api/state") {
            sendJson(response, 200, {
                observation: state.observation,
                ...(workbench ? { benchObservation: state.benchObservation } : {}),
                ...(!workbench ? { draftDocument: state.draftDocument, draftError: state.draftError } : {}),
                error: state.error,
                busy: state.busy,
                ...(workbench ? { editingOutcome: editing.outcome, editingBusy: editing.busy,
                    retainOutcomes: editing.retainOutcomes, handoffOutcomes: handoff.outcomes,
                    handoffSessionId: handoffSession?.sessionId ?? null } : {}),
            });
            return;
        }

        const editingRoute = workbench && request.url === "/api/editing";
        const handoffRoute = workbench && request.url === "/api/handoff";
        if (request.method !== "POST" || (!editingRoute && !handoffRoute && request.url !== (workbench ? "/api/bench" : "/api/detail"))) {
            sendJson(response, 404, { error: "Unknown Twig companion route." });
            return;
        }

        if (!String(request.headers["content-type"] || "").toLowerCase().startsWith("application/json")) {
            sendJson(response, 415, { error: "Twig companion actions require JSON." });
            return;
        }

        // Only the editing intent route admits the native 256KiB request-sized payload.
        const body = await readJsonBody(request, editingRoute ? 262144 : handoffRoute ? 16384 : MAX_REQUEST_BYTES, editingRoute || handoffRoute);
        if (handoffRoute) {
            if (state.busy || state.closed || editing.busy || !editingTarget) throw new HttpError(409, "Load current qualified Bench detail before handoff.");
            if (body.action === "prepare" && Object.keys(body).length === 1) {
                sendJson(response, 200, { handoff: handoff.prepare(editingTarget) });
            } else if (body.action === "start" && Object.keys(body).sort().join(",") === "action,brief,handle") {
                sendJson(response, 200, { handoff: { outcome: await handoff.start(body) } });
            } else {
                throw new HttpError(400, "Expected prepare, or explicit Start with only a handle and brief.");
            }
            return;
        }
        if (editingRoute) {
            if (body.action === "invalidate" && Object.keys(body).length === 1) {
                invalidateEditing();
                sendJson(response, 200, { invalidated: true });
                return;
            }
            if (state.busy || state.closed || !editingTarget) throw new HttpError(409, "Load current qualified Bench detail before explicit editing.");
            const disconnected = () => {
                if (!response.writableEnded) editing.invalidate();
            };
            request.once("aborted", disconnected);
            response.once("close", disconnected);
            try {
                if (body.action === "open" && Object.keys(body).length === 1) {
                    sendJson(response, 200, { editing: await editing.open(editingTarget) });
                } else if (body.action === "stage" && Object.keys(body).sort().join(",") === "action,captureHandle,changes") {
                    const { captureHandle, changes } = body;
                    sendJson(response, 200, { editing: await editing.stage({ captureHandle, changes }) });
                } else if (body.action === "retain" && Object.keys(body).sort().join(",") === "action,captureHandle") {
                    sendJson(response, 200, { editing: await editing.retain({ captureHandle: body.captureHandle }) });
                } else {
                    sendJson(response, 400, { error: "Expected explicit OPEN, STAGE with handle and intent, or RETAIN with only a handle." });
                }
            } finally {
                request.removeListener("aborted", disconnected);
                response.removeListener("close", disconnected);
            }
            return;
        }
        if (workbench) {
            if (body.action === "load" && typeof body.bench === "string" && body.bench.trim() && body.bench.length <= 512) {
                sendJson(response, 200, { benchObservation: await loadBench(body.bench) });
            } else if (body.action === "refresh") {
                sendJson(response, 200, { benchObservation: await refreshBench() });
            } else if (body.action === "detail" && isWorkItemId(body.workItemId)) {
                sendJson(response, 200, { benchDetail: await loadBenchDetail(body.workItemId) });
            } else {
                sendJson(response, 400, { error: "Expected a saved Bench name or ID, or a refresh action." });
            }
            return;
        }
        if (body.action === "load" && isWorkItemId(body.workItemId)) {
            sendJson(response, 200, { observation: await load(body.workItemId) });
            return;
        }
        if (body.action === "refresh") {
            sendJson(response, 200, { observation: await refresh() });
            return;
        }

        sendJson(response, 400, { error: "Expected a non-zero work item ID or a refresh action." });
    }

    return {
        url: `${origin}/`,
        load,
        refresh,
        loadBench,
        refreshBench,
        loadBenchDetail,
        inspectDraft,
        close: async () => {
            if (closing) return closing;
            state.closed = true;
            invalidateEditing();
            clearDraft();
            state.controller?.abort();
            closing = Promise.all([closeServer(server), editing.awaitIdle()]).then(() => undefined);
            return closing;
        },
    };
}

function isWorkItemId(value) {
    return Number.isInteger(value) && value !== 0 && Math.abs(value) <= 2_147_483_647;
}

async function readJsonBody(request, limit = MAX_REQUEST_BYTES, strict = false) {
    const chunks = [];
    let length = 0;
    for await (const chunk of request) {
        length += chunk.length;
        if (length > limit) {
            request.resume();
            throw new HttpError(413, "Request body exceeds the Twig companion limit.");
        }
        chunks.push(chunk);
    }

    let value;
    try {
        const source = new TextDecoder("utf-8", { fatal: strict }).decode(Buffer.concat(chunks));
        value = JSON.parse(source);
        if (strict) rejectDuplicateKeys(source);
    } catch (error) {
        throw new HttpError(400, "Request body must be valid JSON.", { cause: error });
    }
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
        throw new HttpError(400, "Request body must be a JSON object.");
    }
    return value;
}

function rejectDuplicateKeys(source) {
    // JSON.parse establishes grammar first; this pass retains duplicate property evidence.
    const tokens = source.match(/"(?:[^"\\]|\\.)*"|[{}[\]:,]/g) || [];
    const stack = [];
    for (let index = 0; index < tokens.length; index++) {
        const token = tokens[index];
        if (token === "{" || token === "[") {
            stack.push(token === "{" ? new Set() : null);
            if (stack.length > 32) throw new Error("Editing JSON exceeds the depth limit.");
        } else if (token === "}" || token === "]") stack.pop();
        else if (token.startsWith('"') && tokens[index + 1] === ":") {
            const key = JSON.parse(token);
            const object = stack[stack.length - 1];
            if (object.has(key)) throw new Error("Duplicate editing JSON key.");
            object.add(key);
        }
    }
}

function hasValidToken(request, token) {
    return request.headers.authorization === `Bearer ${token}`;
}

function sendJson(response, status, value) {
    response.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
    response.end(JSON.stringify(value));
}

function setSecurityHeaders(response) {
    response.setHeader("Cache-Control", "no-store");
    response.setHeader("X-Content-Type-Options", "nosniff");
    response.setHeader("Referrer-Policy", "no-referrer");
    response.setHeader("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
}

function contentSecurityPolicy(nonce) {
    return [
        "default-src 'none'",
        `script-src 'nonce-${nonce}'`,
        `style-src 'nonce-${nonce}'`,
        "connect-src 'self'",
        "img-src 'none'",
        "font-src 'self'",
        "object-src 'none'",
        "base-uri 'none'",
        "form-action 'self'",
    ].join("; ");
}

function closeServer(server) {
    return new Promise((resolve, reject) => {
        server.close((error) => error ? reject(error) : resolve());
    });
}

class HttpError extends Error {
    constructor(status, message, options) {
        super(message, options);
        this.status = status;
    }
}
