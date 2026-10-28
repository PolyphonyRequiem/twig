import { realpath } from "node:fs/promises";
import path from "node:path";

export async function readConversationContext({ session, itemKey, timeoutMs = 5000 }) {
    const context = {
        status: "unknown", sessionId: session?.sessionId ?? null,
        itemKey: structuredClone(itemKey), itemDiscovery: "unknown",
        runDiscovery: "unknown", navigation: "unavailable",
        message: "Conversation item and linked run discovery are unknown. App destination/focus navigation is unavailable.",
    };
    if (typeof session?.sessionId !== "string" || !session.sessionId ||
        typeof session?.rpc?.metadata?.snapshot !== "function") {
        return { ...context, sessionId: null, message: "Runtime session metadata is unavailable. " + context.message };
    }
    let timer;
    try {
        const checked = (async () => {
            const snapshot = await session.rpc.metadata.snapshot();
            if (typeof snapshot?.sessionId !== "string" || !snapshot.sessionId ||
                typeof snapshot.isRemote !== "boolean" || typeof snapshot.workingDirectory !== "string" ||
                !snapshot.workingDirectory.trim()) return null;
            if (snapshot.sessionId !== session.sessionId || snapshot.isRemote) return false;
            const roots = await Promise.all([realpath(snapshot.workingDirectory), realpath(itemKey.checkoutRoot)]);
            const normalize = value => process.platform === "win32" ? path.resolve(value).toLowerCase() : path.resolve(value);
            return normalize(roots[0]) === normalize(roots[1]);
        })();
        const matches = await Promise.race([checked, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error("Metadata deadline exceeded.")), timeoutMs);
        })]);
        if (matches === null) return { ...context, message: "Runtime session metadata is incomplete or unsupported. " + context.message };
        return { ...context, status: matches ? "current" : "mismatch",
            message: (matches ? "Runtime session and checkout match this explicit native item selection; this does not establish a conversation/item link. "
                : "Conversation/session or checkout mismatch. This item is not a verified conversation target. ") + context.message };
    } catch {
        return { ...context, message: "Runtime session metadata or checkout verification is unavailable. " + context.message };
    } finally { clearTimeout(timer); }
}

export function renderConversationContext({ document, text, target, context }) {
    target.replaceChildren();
    target.append(text("h2", "Conversation context"));
    if (!context) {
        target.append(text("p", "Conversation item and linked run: unknown. Load an explicit item, then check runtime context. App destination/focus navigation is unavailable."));
        return;
    }
    target.append(text("p", context.message, context.status === "current" ? "notice" : "notice error"));
    target.append(text("p", "Observation: " + context.status));
    target.append(text("p", "Runtime session (not an app navigation ID): " + (context.sessionId ?? "unavailable")));
    target.append(text("p", "Explicit native item selection: #" + context.itemKey.workItemId));
}
