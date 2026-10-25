export function createDiscussionClient({ document, request, text }) {
    // Typed executable admission failures latch for this canvas instance, so reading again on the same
    // handle cannot succeed; recovery is reopening the canvas. Kept local: this function is injected as source.
    const EXECUTABLE_CODES = new Set(["twig_executable_unavailable", "twig_capability_unlisted", "twig_capability_missing", "executable_changed"]);
    let current = null;
    function leave() {
        const previous = current;
        current = null;
        if (!previous) return Promise.resolve();
        previous.pageHandle = null;
        previous.box.replaceChildren();
        return request("/api/discussion", { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ action: "invalidate", targetHandle: previous.targetHandle }) });
    }
    function mount(parent, targetHandle) {
        if (typeof targetHandle !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(targetHandle)) {
            parent.append(text("p", "Discussion read unavailable: a published cached member and native discussion support are required."));
            return;
        }
        const state = { targetHandle, pageHandle: null, busy: false, observed: false };
        current = state;
        const box = document.createElement("section");
        state.box = box;
        box.append(text("h3", "Discussion"),
            text("p", "Explicit online read of one provider page. No posting, editing, Save or apply. Pages are not a consistent whole-thread snapshot."));
        const read = text("button", "Read discussion (online)");
        const next = text("button", "Next discussion page");
        read.type = next.type = "button";
        next.disabled = true;
        const status = text("p", "Discussion has not been read.");
        status.setAttribute("role", "status");
        const content = document.createElement("div");
        const refresh = () => { read.disabled = state.busy || state.invalidated === true; next.disabled = state.busy || !state.pageHandle; };
        const nullable = value => value === null ? "(unavailable)" : value === "" ? "(empty)" : String(value);
        async function load(action) {
            if (current !== state || state.busy || state.invalidated || action === "next" && !state.pageHandle) return;
            const pageHandle = state.pageHandle;
            state.pageHandle = null;
            state.busy = true;
            status.textContent = "Reading one discussion page online...";
            refresh();
            try {
                const result = await request("/api/discussion", {
                    method: "POST", headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ action, targetHandle, ...(action === "next" ? { pageHandle } : {}) }),
                });
                if (current !== state) return;
                if (result.discussion.status === "refused") {
                    if (result.discussion.code === "authority-invalidated") {
                        content.replaceChildren();
                        state.observed = false;
                        state.invalidated = true;
                    }
                    status.textContent = "Discussion read refused (" + result.discussion.code +
                        "). Recheck the native item, Bench and connection. " +
                        (state.observed ? "Displayed page is a previous observation." : "No discussion page was accepted.");
                    return;
                }
                const page = result.discussion.page;
                content.replaceChildren();
                content.append(text("p", "Provider page: " + page.count + " entries; reported total: " +
                    nullable(page.reportedTotalCount) + ". " + (page.traversal === "more" ?
                    "More pages are available." : "End of this traversal, not a complete snapshot.") +
                    " Snapshot consistency is not guaranteed."));
                if (!page.comments.length) content.append(text("p", "This provider page contains no entries."));
                for (const comment of page.comments) {
                    const entry = document.createElement("section");
                    entry.append(text("h4", "Comment " + comment.id),
                        text("p", "Version: " + nullable(comment.version) + "; author: " + nullable(comment.author.displayName) +
                            "; author ID: " + nullable(comment.author.id)),
                        text("p", "Created (UTC): " + nullable(comment.createdAt) + "; modified (UTC): " +
                            nullable(comment.modifiedAt) + "; deleted: " + nullable(comment.isDeleted) +
                            "; format (inert): " + nullable(comment.format)),
                        text("pre", nullable(comment.body)));
                    content.append(entry);
                }
                state.observed = true;
                state.pageHandle = result.discussion.pageHandle;
                status.textContent = "One discussion page read. Content is untrusted inert text; no mutation was performed.";
            } catch (error) {
                const oversized = error.message === "Discussion exceeds the extension 4MiB output limit; no page was accepted.";
                const executable = EXECUTABLE_CODES.has(error.code);
                if (executable && current === state) state.invalidated = true;
                if (current === state) status.textContent = (oversized || executable ? error.message : "Discussion read unavailable; no page was accepted.") + " " +
                    (state.observed ? "Displayed page is a previous observation. " : "") +
                    (executable ? "No page was accepted; reading again in this canvas is disabled." : "No automatic retry.");
            } finally {
                state.busy = false;
                if (current === state) refresh();
            }
        }
        read.addEventListener("click", () => { void load("read"); });
        next.addEventListener("click", () => { void load("next"); });
        box.append(read, next, status, content);
        parent.append(box);
    }
    return { mount, leave };
}
