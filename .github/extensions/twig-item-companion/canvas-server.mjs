import { randomBytes } from "node:crypto";
import { createServer } from "node:http";
import { renderPage } from "./render.mjs";
import { captureBench } from "./native-bench.mjs";

const MAX_REQUEST_BYTES = 4 * 1024;

export async function createCanvasServer({ workspacePath, readNativeDetail, readNativeBench, workbench = false }) {
    const token = randomBytes(32).toString("base64url");
    const state = {
        observation: null,
        benchObservation: null,
        error: null,
        busy: false,
        closed: false,
        controller: null,
    };

    const load = async (workItemId, expected) => {
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
        if (!state.observation) throw new Error("Load an item before refreshing detail.");
        const current = state.observation;
        return load(current.itemKey.workItemId, {
            benchId: current.benchId,
            bindingId: current.itemKey.bindingId,
            identityId: current.identityId,
            connectionId: current.itemKey.connectionId,
        });
    };

    const loadBench = async (bench, expected) => {
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
                error: state.error,
                busy: state.busy,
            });
            return;
        }

        if (request.method !== "POST" || request.url !== (workbench ? "/api/bench" : "/api/detail")) {
            sendJson(response, 404, { error: "Unknown Twig companion route." });
            return;
        }

        if (!String(request.headers["content-type"] || "").toLowerCase().startsWith("application/json")) {
            sendJson(response, 415, { error: "Twig companion actions require JSON." });
            return;
        }

        const body = await readJsonBody(request);
        if (workbench) {
            if (body.action === "load" && typeof body.bench === "string" && body.bench.trim() && body.bench.length <= 512) {
                sendJson(response, 200, { benchObservation: await loadBench(body.bench) });
            } else if (body.action === "refresh") {
                sendJson(response, 200, { benchObservation: await refreshBench() });
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
        close: async () => {
            if (state.closed) return;
            state.closed = true;
            state.controller?.abort();
            await closeServer(server);
        },
    };
}

function isWorkItemId(value) {
    return Number.isInteger(value) && value !== 0 && Math.abs(value) <= 2_147_483_647;
}

async function readJsonBody(request) {
    const chunks = [];
    let length = 0;
    for await (const chunk of request) {
        length += chunk.length;
        if (length > MAX_REQUEST_BYTES) {
            request.resume();
            throw new HttpError(413, "Request body exceeds the Twig companion limit.");
        }
        chunks.push(chunk);
    }

    let value;
    try {
        value = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    } catch (error) {
        throw new HttpError(400, "Request body must be valid JSON.", { cause: error });
    }
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
        throw new HttpError(400, "Request body must be a JSON object.");
    }
    return value;
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
