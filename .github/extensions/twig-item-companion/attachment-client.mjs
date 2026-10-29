export function createAttachmentClient({ document, request, text, navigator,
    wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)) }) {
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
        const state = { targetHandle, busy: false, invalidated: false, inventoryRevision: 0 };
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
            const inventoryRevision = ++state.inventoryRevision;
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
                    let clicked = false;
                    async function observeDownload() {
                        try {
                            for (let attempt = 0; attempt < 40; attempt++) {
                                await wait(1000);
                                if (current !== state || state.invalidated || state.inventoryRevision !== inventoryRevision) return;
                                const outcome = await request("/api/attachments", {
                                    method: "POST", headers: { "Content-Type": "application/json" },
                                    signal: AbortSignal.timeout(5000),
                                    body: JSON.stringify({ action: "download-status", targetHandle,
                                        downloadHandle: entry.downloadHandle }),
                                });
                                if (current !== state || state.inventoryRevision !== inventoryRevision) return;
                                if (outcome.status === "sent") {
                                    status.textContent = "Twig finished sending the attachment to the browser. Saving is not confirmed. If no file appeared, the host may have blocked the save.";
                                    return;
                                }
                                if (outcome.status === "refused" || outcome.status === "interrupted") {
                                    status.textContent = "Attachment download " + outcome.status + ": " +
                                        (outcome.message ?? "The connection closed before Twig finished sending bytes.") +
                                        " Read a fresh inventory before retrying.";
                                    return;
                                }
                                if (!["not-requested", "reading"].includes(outcome.status))
                                    throw new Error("Invalid download observation.");
                                status.textContent = outcome.status === "not-requested"
                                    ? "The click was accepted, but no download request has reached Twig. The host may be blocking iframe downloads."
                                    : "Twig received the download request and is reading the attachment. Saving is not confirmed.";
                            }
                            status.textContent += " Observation ended; no automatic download retry. Read a fresh inventory before retrying.";
                        } catch {
                            if (current === state && state.inventoryRevision === inventoryRevision)
                                status.textContent = "Download status unavailable. Saving is not confirmed; no automatic download retry.";
                        }
                    }
                    link.addEventListener("click", clickEvent => {
                        if (clicked) {
                            clickEvent.preventDefault();
                            status.textContent = "This download link was already used. Read a fresh inventory before retrying.";
                            return;
                        }
                        if (clickEvent.isTrusted !== true || navigator?.userActivation?.isActive !== true ||
                            current !== state || state.invalidated) {
                            clickEvent.preventDefault();
                            status.textContent = "Download requires a trusted human click and active browser user gesture.";
                            return;
                        }
                        clicked = true;
                        link.setAttribute("aria-disabled", "true");
                        link.classList.add("muted");
                        status.textContent = "Download click accepted. Waiting for Twig to observe the request; saving is not confirmed.";
                        void observeDownload();
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
