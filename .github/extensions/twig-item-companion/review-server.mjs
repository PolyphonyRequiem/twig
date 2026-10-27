import { randomBytes } from "node:crypto";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { renderPage } from "./render.mjs";
import { validateReviewRequest } from "./native-proposal.mjs";
import { validateApplyRequest } from "./native-apply.mjs";

export async function createReviewServer({ workspacePath, readNativeProposal, applyNativeProposal }) {
    const token = randomBytes(32).toString("base64url");
    const moduleSource = await readFile(new URL("./retained-review.mjs", import.meta.url), "utf8");
    let observation = null, error = null, controller = null, closed = false, generation = 0;
    let applyAttempt = null;
    const state = () => ({
        observation: observation ? { rawJson: observation.rawJson, expected: observation.expected,
            acquisition: observation.acquisition } : null,
        error, busy: controller !== null, generation, applyAttempt, applyAvailable: !!applyNativeProposal,
    });

    async function acquire(input) {
        if (closed) throw new Error("This Twig proposal review is closed.");
        if (controller) throw new Error("A proposal preview is already in progress; no second preview was started.");
        const owned = new AbortController();
        controller = owned;
        const current = ++generation;
        observation = error = null;
        applyAttempt = null;
        try {
            const request = validateReviewRequest(input);
            const result = await readNativeProposal({ workspacePath, input: request, signal: owned.signal });
            if (closed || owned.signal.aborted || generation !== current) throw new Error("Proposal preview was closed or superseded.");
            observation = result;
        } catch (failure) {
            if (!closed && generation === current) error = failure instanceof Error ? failure.message : "Native proposal preview failed.";
            throw failure;
        } finally {
            if (controller === owned) controller = null;
        }
        return state();
    }

    async function apply(input) {
        if (closed) throw new Error("This Twig proposal review is closed.");
        if (controller) throw new Error("A proposal action is already in progress; no second action was started.");
        if (!applyNativeProposal) throw new Error("Native Apply is unavailable.");
        if (applyAttempt) throw new Error("This capture was already submitted. Inspect the native journal; do not retry an uncertain effect.");
        const request = validateApplyRequest(input, observation);
        const owned = new AbortController();
        controller = owned;
        const current = ++generation;
        error = null;
        applyAttempt = { captureId: request.captureId, digest: request.confirm, authorize: request.authorize,
            status: "in-progress" };
        try {
            const result = await applyNativeProposal({ input: request, observation, workspacePath, signal: owned.signal });
            if (closed || owned.signal.aborted || generation !== current) throw new Error("Apply was closed or superseded; outcome may be unknown.");
            applyAttempt = { ...applyAttempt, ...result };
        } catch (failure) {
            if (!closed && generation === current) {
                error = failure instanceof Error ? failure.message : "Native Apply failed.";
                applyAttempt = { ...applyAttempt, status: "outcome-unknown", error };
            }
            throw failure;
        } finally {
            if (controller === owned) controller = null;
        }
        return state();
    }

    const server = createServer(async (req, res) => {
        const url = new URL(req.url, "http://127.0.0.1");
        res.setHeader("Cache-Control", "no-store");
        res.setHeader("X-Content-Type-Options", "nosniff");
        res.setHeader("Referrer-Policy", "no-referrer");
        const send = (status, value) => {
            res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
            res.end(JSON.stringify(value));
        };
        try {
            const origin = `http://127.0.0.1:${server.address().port}`;
            if (req.headers.host !== new URL(origin).host ||
                (req.headers.origin !== undefined && req.headers.origin !== origin)) {
                return send(403, { error: "Cross-origin requests are not allowed." });
            }
            if (req.method === "GET" && url.pathname === "/") {
                const nonce = randomBytes(24).toString("base64url");
                res.setHeader("Content-Security-Policy", `default-src 'none'; script-src 'self' 'nonce-${nonce}'; style-src 'nonce-${nonce}'; connect-src 'self'; base-uri 'none'; form-action 'none'`);
                res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
                res.end(renderPage({ token, nonce, review: true }));
                return;
            }
            if (req.method === "GET" && url.pathname === "/review-module.mjs" && url.searchParams.get("token") === token) {
                res.writeHead(200, { "Content-Type": "text/javascript; charset=utf-8" });
                res.end(moduleSource);
                return;
            }
            if (req.headers.authorization !== `Bearer ${token}`) return send(403, { error: "Invalid canvas instance token." });
            if (req.method === "GET" && url.pathname === "/api/state") return send(200, state());
            if (req.method !== "POST" || !["/api/acquire", "/api/apply"].includes(url.pathname)) return send(404, { error: "Unknown review route." });
            if (!String(req.headers["content-type"] || "").toLowerCase().startsWith("application/json")) {
                return send(415, { error: "Review actions require JSON." });
            }
            const chunks = [];
            let bytes = 0;
            for await (const chunk of req) {
                bytes += chunk.length;
                if (bytes > 8192) return send(413, { error: "Review request exceeds 8192 bytes; no action started." });
                chunks.push(chunk);
            }
            let input;
            try { input = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
            catch { return send(400, { error: "Review request is not valid JSON; no action started." }); }
            if (url.pathname === "/api/apply") await apply(input);
            else await acquire(input);
            send(200, state());
        } catch (failure) {
            send(400, { error: failure instanceof Error ? failure.message : "Proposal review failed." });
        }
    });
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    return {
        url: `http://127.0.0.1:${server.address().port}/`,
        acquire,
        apply,
        async close() {
            if (closed) return;
            closed = true;
            generation++;
            observation = null;
            controller?.abort();
            await new Promise((resolve, reject) => server.close((failure) => failure ? reject(failure) : resolve()));
        },
    };
}
