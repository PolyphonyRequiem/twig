export function createDiscussionPostClient({ document, request, text }) {
    let current = null;
    let held = null;
    function restore(outcomes) { held = outcomes?.find(value => value.status !== "verified") ?? held; }
    function leave() { current = null; }
    function mount(parent, targetHandle) {
        const state = { busy: false, submitted: !!held, capture: null };
        current = state;
        const box = document.createElement("section");
        const editor = document.createElement("textarea");
        editor.rows = 5;
        editor.setAttribute("aria-label", "Discussion comment plain text");
        const prepare = text("button", "Prepare discussion proposal");
        prepare.type = "button";
        const apply = text("button", "Authorize and post exact digest once");
        apply.type = "button";
        apply.disabled = true;
        const status = text("p", "Read the qualified discussion first. Preparing writes a local proposal only; posting requires a separate explicit authorization.");
        if (held) status.textContent = "Previous post " + held.digest + ": " + held.status + ". Inspect the native journal; no retry.";
        status.setAttribute("role", "status");
        const review = document.createElement("div");
        function update() {
            editor.disabled = state.busy || state.submitted;
            prepare.disabled = state.busy || state.submitted || !editor.value.trim();
            apply.disabled = state.busy || state.submitted || !state.capture?.canApply;
        }
        editor.addEventListener("input", () => {
            state.capture = null;
            review.replaceChildren();
            update();
        });
        async function send(body) {
            return request("/api/discussion-post", { method: "POST",
                headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...body, targetHandle }) });
        }
        prepare.addEventListener("click", async () => {
            if (current !== state || state.busy || state.submitted || !editor.value.trim()) return;
            state.busy = true;
            state.capture = null;
            review.replaceChildren();
            update();
            try {
                const result = await send({ action: "prepare", text: editor.value, format: "plain" });
                if (current !== state) return;
                const capture = state.capture = result.discussionPost;
                review.append(text("h4", "Exact discussion proposal"),
                    text("p", capture.workspace.organization + "/" + capture.workspace.project + " / #" + capture.target.workItemId +
                        " / Bench: " + capture.bench + " / identity: " + capture.identity),
                    text("p", "Digest: " + capture.digest), text("pre", capture.text),
                    text("pre", capture.effectiveBody), text("pre", JSON.stringify(capture.target)),
                    text("pre", JSON.stringify(capture.preconditions)));
                for (const blocker of capture.blockers) review.append(text("pre", JSON.stringify(blocker)));
                status.textContent = capture.canApply ? "Prepared, not posted. Authorize only this target, body and digest." :
                    "Native preview is blocked. No post is available.";
            } catch (error) {
                if (current === state) status.textContent = error.message + " No comment was posted by preparation.";
            } finally { state.busy = false; if (current === state) update(); }
        });
        apply.addEventListener("click", async event => {
            if (!event?.isTrusted || current !== state || state.busy || state.submitted || !state.capture?.canApply) return;
            const capture = state.capture;
            state.busy = state.submitted = true;
            held = { digest: capture.digest, status: "in-progress" };
            update();
            status.textContent = "Posting exact digest once; awaiting native typed-comment-ID readback...";
            try {
                const result = await send({ action: "apply", captureHandle: capture.captureHandle, confirm: capture.digest });
                held = result.discussionPost.status === "verified" ? null : result.discussionPost;
                if (current !== state) return;
                const outcome = result.discussionPost;
                status.textContent = outcome.status === "verified" ?
                    "Verified comment " + outcome.commentId + " by native typed-ID readback. No retry." :
                    "Post is not verified. Inspect the native journal; no retry.";
            } catch {
                held = { digest: capture.digest, status: "outcome-unknown" };
                if (current === state) status.textContent = "Post outcome is unknown. Inspect the native journal; no automatic retry.";
            } finally { state.busy = false; if (current === state) update(); }
        });
        box.append(text("h4", "Post a discussion comment"), editor, prepare, review, apply, status);
        parent.append(box);
        update();
    }
    return { mount, leave, restore };
}
