export function createHandoffClient({ document, request, text }) {
    const drafts = new Map();
    const outcomes = new Map();
    let current = null;
    function leave() { current = null; }
    function mount(parent, detail) {
        const key = JSON.stringify([detail.itemKey, detail.benchId, detail.identityId]);
        const state = { key, handle: null, busy: false, held: outcomes.has(key) };
        current = state;
        const box = document.createElement("section");
        box.append(text("h3", "Handle in this conversation"));
        box.append(text("p", "Fallback: Start sends your edited brief to the current Copilot conversation using its current agent and permissions. It does not create or open another app/Agency session, save edits, authorize or apply proposals."));
        const prepare = text("button", "Prepare handoff brief");
        prepare.type = "button";
        const label = document.createElement("label");
        label.append(text("span", "Task brief (not unsent field edits)"));
        const brief = document.createElement("textarea");
        brief.value = drafts.get(key) ?? "";
        brief.rows = 5;
        brief.maxLength = 8192;
        label.append(brief);
        const start = text("button", "Start in this session");
        start.type = "button";
        start.disabled = true;
        const check = text("button", "Check handoff status");
        check.type = "button";
        const status = text("p", "Prepare the item-bound brief, edit it, then explicitly Start. Nothing starts on selection.");
        status.setAttribute("role", "status");
        function refresh() {
            prepare.disabled = state.busy || state.held === true;
            start.disabled = state.busy || !state.handle || !brief.value.trim();
            check.disabled = state.busy;
        }
        brief.addEventListener("input", () => { drafts.set(key, brief.value); refresh(); });
        function show(outcome) {
            state.handle = null;
            state.held = outcome.inspectOnly === true || outcome.status !== "refused";
            if (state.held) outcomes.set(key, outcome);
            else outcomes.delete(key);
            status.textContent = outcome.status === "admitted" ?
                "Brief admitted to this conversation; work is not yet complete. Message: " + outcome.messageId :
                outcome.status === "refused" ? "Handoff refused before sending: " + outcome.error :
                    "Handoff outcome unknown. Inspect this conversation; do not retry. " + (outcome.error || "");
        }
        if (outcomes.has(key)) show(outcomes.get(key));
        refresh();
        async function act(action) {
            if (current !== state || state.busy || state.held) return;
            if (action === "start" && (!state.handle || !brief.value.trim())) return;
            state.busy = true;
            refresh();
            const submitted = brief.value;
            if (action === "start") {
                // A lost HTTP acknowledgement cannot authorize a second Start.
                state.held = true;
                outcomes.set(key, { status: "unknown", error: "Start is awaiting acknowledgement." });
                drafts.set(key, submitted);
            }
            try {
                const result = await request("/api/handoff", {
                    method: "POST", headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(action === "prepare" ? { action } :
                        { action, handle: state.handle, brief: submitted }),
                });
                if (result.handoff.outcome) {
                    if (result.handoff.outcome.status === "refused") outcomes.delete(key);
                    else outcomes.set(key, result.handoff.outcome);
                }
                if (current !== state) return;
                if (result.handoff.outcome) show(result.handoff.outcome);
                else {
                    state.handle = result.handoff.handle;
                    if (!drafts.has(key)) brief.value = result.handoff.brief;
                    status.textContent = "Brief ready. Start is explicit and uses this conversation, not a new session.";
                }
            } catch (error) {
                if (current !== state) return;
                state.handle = null;
                status.textContent = action === "start" ?
                    "Start acknowledgement unavailable; inspect this conversation before retrying. " + error.message :
                    "Cannot prepare handoff: " + error.message;
            } finally {
                state.busy = false;
                if (current === state) refresh();
            }
        }
        prepare.addEventListener("click", () => { void act("prepare"); });
        start.addEventListener("click", () => { void act("start"); });
        check.addEventListener("click", () => { void inspect(); });
        async function inspect() {
            if (current !== state || state.busy) return;
            state.handle = null;
            state.held = true;
            if (!outcomes.has(key)) outcomes.set(key, { status: "unknown" });
            state.busy = true;
            refresh();
            try {
                const snapshot = await request("/api/state");
                if (current !== state) return;
                const outcome = (snapshot.handoffOutcomes || []).find(entry => {
                    const target = entry.target;
                    const item = target?.expectedKey;
                    return typeof snapshot.handoffSessionId === "string" && snapshot.handoffSessionId.length > 0 &&
                        item && item.checkoutRoot === detail.itemKey.checkoutRoot &&
                        item.connectionId === detail.itemKey.connectionId &&
                        item.bindingId === detail.itemKey.bindingId &&
                        item.workItemId === detail.itemKey.workItemId &&
                        target.workItemId === detail.itemKey.workItemId &&
                        target.bench === detail.benchId && target.identityId === detail.identityId &&
                        entry.sessionId === snapshot.handoffSessionId;
                });
                if (outcome) {
                    show({ ...outcome, inspectOnly: true });
                }
                else status.textContent = "No matching handoff evidence is available. Any unknown outcome remains unresolved; no retry is enabled.";
            } catch (error) {
                if (current === state) status.textContent = "Handoff status unavailable; no retry is enabled. " + error.message;
            } finally {
                state.busy = false;
                if (current === state) refresh();
            }
        }
        box.append(prepare, label, start, check, status);
        parent.append(box);
    }
    return { mount, leave };
}
