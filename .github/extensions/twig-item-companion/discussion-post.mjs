import { randomBytes } from "node:crypto";

export function createDiscussionPost({ adapter, workspacePath }) {
    let prepared = null, handle = null, busy = false, started = false, poisoned = false;
    let generation = 0, idle = Promise.resolve();
    const outcomes = [];
    return {
        get busy() { return busy; },
        get outcomes() { return structuredClone(outcomes); },
        invalidate() { generation++; prepared = handle = null; },
        awaitIdle() { return idle; },
        async prepare({ context, text, format }) {
            if (!adapter || busy || poisoned) throw new Error("Discussion posting is unavailable, busy, or has an unresolved outcome. No retry.");
            const current = ++generation;
            prepared = handle = null;
            busy = true;
            let release;
            idle = new Promise(resolve => { release = resolve; });
            try {
                const result = await adapter.prepare({ workspacePath, context: structuredClone(context), text, format });
                if (current !== generation) throw new Error("Discussion preparation was superseded; no authorization was accepted.");
                prepared = result;
                started = false;
                handle = randomBytes(32).toString("base64url");
                return { captureHandle: handle, digest: result.digest, target: result.target,
                    workspace: result.workspace, bench: result.bench, identity: result.identity,
                    format: result.format, text: result.text, effectiveBody: result.effectiveBody,
                    preconditions: result.preconditions ?? [],
                    canApply: result.canApply !== false, blockers: result.blockers ?? [] };
            } finally { busy = false; release(); }
        },
        async apply({ captureHandle, confirm }) {
            if (!adapter || busy || poisoned || started || !prepared || captureHandle !== handle ||
                confirm !== prepared.digest || prepared.canApply === false)
                throw new Error("Discussion authorization is stale, blocked, or already submitted. No retry.");
            busy = started = true;
            const outcome = { digest: confirm, target: structuredClone(prepared.target), status: "in-progress" };
            outcomes.push(outcome);
            let release;
            idle = new Promise(resolve => { release = resolve; });
            try {
                const result = await adapter.apply({ workspacePath, prepared, confirm });
                Object.assign(outcome, { status: result.status, commentId: result.commentId ?? null });
                if (result.status !== "verified") poisoned = true;
                return structuredClone(outcome);
            } catch {
                poisoned = true;
                outcome.status = "outcome-unknown";
                throw new Error("Discussion post outcome is unknown. Inspect the native journal; no automatic retry.");
            } finally { busy = false; release(); }
        },
    };
}
