export function createEditingClient({ document, request, text }) {
    const intentByItem = new Map();
    let current = null;
    let generation = 0;
    let inFlight = false;
    function leave() {
        generation++;
        current = null;
        // Local capture revocation only; never runs native OPEN/STAGE on navigation.
        return request("/api/editing", { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ action: "invalidate" }) });
    }
    function mount(target, detail) {
        const item = detail.itemKey;
        const key = JSON.stringify([item.checkoutRoot, item.connectionId, item.bindingId, item.workItemId,
            detail.benchId, detail.identityId]);
        if (!intentByItem.has(key)) intentByItem.set(key, { changes: new Map(), revision: null });
        const retainedItem = intentByItem.get(key);
        const intent = retainedItem.changes;
        const session = { generation: ++generation, handle: null, intent, canStage: false, stale: false };
        current = session;
        const box = document.createElement("section");
        box.append(text("h3", "Local draft editing"));
        box.append(text("p", "OPEN may acquire native metadata and persist local observations. STAGE records local intent only. No Save, apply or publication. Installed native support is not yet qualified."));
        const open = text("button", "Open native draft");
        open.type = "button";
        const stage = text("button", "Stage local changes");
        stage.type = "button";
        const status = text("p", "Not opened. Unsupported control types remain read-only; native eligibility is final.");
        status.setAttribute("role", "status");
        const fields = document.createElement("div");
        const retained = text("pre", "");
        const discard = text("button", "Discard retained stale input");
        discard.type = "button";
        discard.hidden = true;
        function renderIntent() {
            retained.textContent = intent.size ? "Unsent input retained for this qualified item:\n" +
                [...intent].map(([field, value]) => field + ": " + (value === null ? "(null)" : JSON.stringify(value))).join("\n") : "";
            stage.disabled = inFlight || session.stale || !session.canStage || !session.handle || !intent.size;
            discard.hidden = !session.stale;
            open.disabled = inFlight;
        }
        session.refresh = renderIntent;
        function renderFields(editing) {
            fields.replaceChildren();
            for (const field of editing.editors || []) {
                const row = document.createElement("div");
                const label = document.createElement("label");
                label.append(text("span", field.label));
                const input = document.createElement("textarea");
                const nativeChange = (editing.changes || []).find(change =>
                    change.fieldRef.toLowerCase() === field.fieldRef.toLowerCase());
                const nativeValue = nativeChange ? nativeChange.proposedValue : field.value;
                const proposed = !session.stale && intent.has(field.fieldRef) ? intent.get(field.fieldRef) : nativeValue;
                input.value = proposed ?? "";
                const clear = document.createElement("input");
                clear.type = "checkbox";
                clear.checked = proposed === null;
                input.disabled = clear.checked || session.stale;
                clear.disabled = session.stale;
                input.addEventListener("input", () => {
                    if (session.stale) return;
                    retainedItem.revision = session.revision;
                    intent.set(field.fieldRef, input.value);
                    renderIntent();
                });
                clear.addEventListener("change", () => {
                    if (session.stale) return;
                    retainedItem.revision = session.revision;
                    input.disabled = clear.checked;
                    intent.set(field.fieldRef, clear.checked ? null : input.value);
                    renderIntent();
                });
                const nullLabel = document.createElement("label");
                nullLabel.append(clear, text("span", "Explicit null (distinct from empty text)"));
                label.append(input);
                row.append(label, nullLabel);
                fields.append(row);
            }
            if (!(editing.editors || []).length) fields.append(text("p", "No supported editable native controls. Other fields and contributions remain read-only."));
        }
        async function perform(action) {
            if (inFlight || current !== session) return;
            if (action === "stage" && (session.stale || !session.canStage || !session.handle || !intent.size)) return;
            const submitted = [...intent].map(([fieldRef, proposedValue]) => ({ fieldRef, proposedValue }));
            inFlight = true;
            open.disabled = true;
            session.canStage = false;
            renderIntent();
            status.textContent = action === "open" ? "Opening native draft explicitly..." : "Staging local intent explicitly...";
            try {
                const result = await request("/api/editing", {
                    method: "POST", headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(action === "open" ? { action } :
                        { action, captureHandle: session.handle, changes: submitted }),
                });
                if (current !== session || session.generation !== generation) return;
                const editing = result.editing;
                session.handle = editing.captureHandle || null;
                session.canStage = editing.canStage === true;
                if (editing.status === "opened" || editing.status === "staged") {
                    session.revision = editing.observedRevision;
                    session.stale = intent.size > 0 && retainedItem.revision !== null &&
                        retainedItem.revision !== session.revision;
                }
                session.editing = editing;
                if (action === "stage" && editing.status === "staged") {
                    for (const change of submitted) {
                        if (intent.get(change.fieldRef) === change.proposedValue) intent.delete(change.fieldRef);
                    }
                }
                renderFields(editing);
                status.textContent = editing.status === "opened" || editing.status === "staged"
                    ? editing.status + (editing.hasConflicts ? ": native drift/conflicts; reopen or reconcile before staging." : ": local draft only, not saved or published.")
                    : (editing.status || "unknown") + ": " + (editing.error?.message || "Native outcome unavailable.") +
                        " Retain input; explicitly reopen. Do not retry automatically.";
                if (session.stale) status.textContent = "Retained input belongs to a different observed revision. Review it separately; discard stale input before entering reconciled changes. No automatic rebase or staging.";
            } catch (error) {
                if (current !== session || session.generation !== generation) return;
                session.handle = null;
                session.canStage = false;
                status.textContent = "Unavailable/unknown: " + (error instanceof Error ? error.message : "Native editing failed.") +
                    " Input retained; explicitly reopen before any further staging.";
                status.setAttribute("role", "alert");
            } finally {
                inFlight = false;
                open.disabled = false;
                renderIntent();
                if (current && current !== session) current.refresh();
            }
        }
        open.addEventListener("click", () => void perform("open"));
        stage.addEventListener("click", () => void perform("stage"));
        discard.addEventListener("click", () => {
            if (inFlight || current !== session || !session.stale) return;
            intent.clear();
            retainedItem.revision = session.revision;
            session.stale = false;
            renderFields(session.editing);
            renderIntent();
            status.textContent = "Stale input explicitly discarded. Enter reconciled intent against the current native observation.";
        });
        box.append(open, stage, status, fields, retained, discard);
        target.append(box);
        renderIntent();
    }
    return { mount, leave };
}
