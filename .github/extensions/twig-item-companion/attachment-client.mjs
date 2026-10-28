export function createAttachmentClient({ document, request, text, navigator }) {
    let current = null;
    function leave() {
        const previous = current;
        current = null;
        if (!previous) return Promise.resolve();
        previous.box.replaceChildren();
        return request("/api/attachments", {
            method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ action: "invalidate", targetHandle: previous.targetHandle }),
        });
    }
    function mount(parent, targetHandle) {
        if (typeof targetHandle !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(targetHandle)) {
            parent.append(text("p", "Attachments are unavailable for this item or native ADO support."));
            return;
        }
        const state = { targetHandle, busy: false, invalidated: false };
        current = state;
        const box = document.createElement("section");
        state.box = box;
        box.append(text("h3", "Attachments"),
            text("p", "Inventory and download run only after an explicit click. Downloads use the browser's normal save behavior; no local path or SDK download action is available."));
        const inventory = text("button", "Show attachments (online)");
        inventory.type = "button";
        const status = text("p", "Attachment inventory has not been read.");
        status.setAttribute("role", "status");
        const entries = document.createElement("div");
        const refresh = () => { inventory.disabled = state.busy || state.invalidated; };
        async function readInventory(event) {
            if (event?.isTrusted !== true || navigator?.userActivation?.isActive !== true) {
                status.textContent = "Attachment inventory requires a trusted human click; synthetic activation was refused.";
                return;
            }
            if (current !== state || state.busy || state.invalidated) return;
            state.busy = true;
            refresh();
            status.textContent = "Reading qualified attachment inventory...";
            try {
                const result = await request("/api/attachments", {
                    method: "POST", headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ action: "inventory", targetHandle }),
                });
                if (current !== state) return;
                if (result.status === "refused") {
                    state.invalidated = result.code === "authority-invalidated";
                    status.textContent = "Attachment inventory refused (" + result.code + "). " +
                        (state.invalidated ? "Reload qualified item detail before reading again." : "No inventory was accepted.");
                    return;
                }
                if (result.status !== "inventory") throw new Error("Invalid attachment inventory response.");
                entries.replaceChildren();
                if (!Array.isArray(result.attachments)) throw new Error("Invalid attachment inventory response.");
                if (!result.attachments.length) entries.append(text("p", "No attachments are linked to this item."));
                for (const entry of result.attachments) {
                    const row = document.createElement("div");
                    row.append(text("p", entry.fileName + " — " + (entry.reportedSize ?? "size unknown") + " bytes"));
                    const link = text("a", "Download " + entry.fileName);
                    link.href = "/api/attachment-download/" + encodeURIComponent(entry.downloadHandle);
                    link.download = entry.fileName;
                    link.addEventListener("click", clickEvent => {
                        if (clickEvent.isTrusted !== true || navigator?.userActivation?.isActive !== true ||
                            current !== state || state.invalidated) {
                            clickEvent.preventDefault();
                            status.textContent = "Download requires a trusted human click and active browser user gesture.";
                            return;
                        }
                        link.setAttribute("aria-disabled", "true");
                        link.classList.add("muted");
                        status.textContent = "The browser is receiving the attachment using its normal save behavior.";
                    });
                    row.append(link);
                    entries.append(row);
                }
                state.invalidated = result.status === "refused" && result.code === "authority-invalidated";
                status.textContent = state.invalidated
                    ? "Attachment authority changed; reload qualified item detail before reading again."
                    : "Qualified attachment inventory loaded. Each download requires its own explicit human click.";
            } catch {
                if (current === state) status.textContent = "Attachment inventory unavailable; no automatic retry.";
            } finally {
                state.busy = false;
                if (current === state) refresh();
            }
        }
        inventory.addEventListener("click", readInventory);
        box.append(inventory, status, entries);
        parent.append(box);
    }
    return { mount, leave };
}
