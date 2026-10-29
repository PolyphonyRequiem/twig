export function createAttachmentClient({ document, request, text, navigator, fetchBytes, urls = globalThis.URL,
    wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)) }) {
    let current = null;
    function closePreview(state) {
        state.previewController?.abort();
        state.previewController = null;
        if (state.previewUrl) urls?.revokeObjectURL(state.previewUrl);
        state.previewUrl = null;
        state.preview?.replaceChildren();
    }
    function leave() {
        const previous = current;
        current = null;
        if (!previous) return Promise.resolve();
        closePreview(previous);
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
            text("p", "Inventory, download and open run only after an explicit click. Downloads use the browser's normal save behavior; Open previews text and images inside this canvas without saving or running the file."));
        const inventory = text("button", "Show attachments (online)");
        inventory.type = "button";
        const status = text("p", "Attachment inventory has not been read.");
        status.setAttribute("role", "status");
        const entries = document.createElement("div");
        const preview = document.createElement("div");
        state.preview = preview;
        const refresh = () => { inventory.disabled = state.busy || state.invalidated; };
        const previewTextLimit = 256 * 1024;
        async function openPreview(event, entry, button, inventoryRevision) {
            if (event?.isTrusted !== true || navigator?.userActivation?.isActive !== true ||
                current !== state || state.invalidated || state.inventoryRevision !== inventoryRevision) {
                status.textContent = "Open requires a trusted human click on the current inventory.";
                return;
            }
            if (button.disabled || typeof fetchBytes !== "function") return;
            button.disabled = true;
            closePreview(state);
            const controller = new AbortController();
            state.previewController = controller;
            const stillCurrent = () => current === state && state.previewController === controller &&
                state.inventoryRevision === inventoryRevision;
            const progress = document.createElement("progress");
            progress.setAttribute("aria-label", "Open progress for " + entry.fileName);
            preview.replaceChildren(text("h4", entry.fileName), progress);
            status.textContent = "Twig is reading " + entry.fileName + " for preview. Nothing is saved or run.";
            try {
                const response = await fetchBytes("/api/attachment-preview/" + encodeURIComponent(entry.previewHandle),
                    { signal: controller.signal });
                if (!stillCurrent()) return;
                const total = Number(response.headers.get("content-length"));
                if (Number.isSafeInteger(total) && total > 0) { progress.max = total; progress.value = 0; }
                const chunks = [];
                let received = 0;
                const reader = response.body.getReader();
                for (;;) {
                    const { done, value } = await reader.read();
                    if (!stillCurrent()) { void reader.cancel(); return; }
                    if (done) break;
                    chunks.push(value);
                    received += value.byteLength;
                    if (progress.max > 1) progress.value = Math.min(received, progress.max);
                }
                progress.max = 1;
                progress.value = 1;
                const bytes = new Uint8Array(received);
                let offset = 0;
                for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
                const close = text("button", "Close preview");
                close.type = "button";
                close.addEventListener("click", () => {
                    if (current !== state || state.previewController !== null) return;
                    closePreview(state);
                    status.textContent = "Preview closed. Read a fresh inventory to open it again.";
                });
                if (entry.preview === "text") {
                    const truncated = bytes.byteLength > previewTextLimit;
                    const body = new TextDecoder("utf-8").decode(bytes.subarray(0, previewTextLimit));
                    preview.replaceChildren(text("h4", entry.fileName), close, text("pre", body),
                        ...(truncated ? [text("p", "Preview shows the first 256 KiB. Download the file for the rest.")] : []));
                } else {
                    const image = document.createElement("img");
                    state.previewUrl = urls.createObjectURL(new Blob([bytes], { type: response.headers.get("content-type") }));
                    image.src = state.previewUrl;
                    image.alt = entry.fileName;
                    image.className = "attachment-preview";
                    preview.replaceChildren(text("h4", entry.fileName), close, image);
                }
                state.previewController = null;
                status.textContent = "Opened " + entry.fileName + " (" + received + " bytes) inside the canvas. Nothing was saved or run. Read a fresh inventory to open it again.";
            } catch (error) {
                if (!stillCurrent()) return;
                state.previewController = null;
                preview.replaceChildren();
                status.textContent = "Open refused or unavailable: " + (error?.message ?? "preview failed") +
                    " No automatic retry; read a fresh inventory before trying again.";
            }
        }
        async function readInventory(event) {
            if (event?.isTrusted !== true || navigator?.userActivation?.isActive !== true) {
                status.textContent = "Attachment inventory requires a trusted human click; synthetic activation was refused.";
                return;
            }
            if (current !== state || state.busy || state.invalidated) return;
            state.busy = true;
            const inventoryRevision = ++state.inventoryRevision;
            closePreview(state);
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
                    const progress = document.createElement("progress");
                    progress.hidden = true;
                    progress.max = 1;
                    progress.setAttribute("aria-label", "Download progress for " + entry.fileName);
                    const finishProgress = ok => {
                        if (ok) { progress.value = 1; progress.hidden = false; }
                        else progress.hidden = true;
                    };
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
                                    finishProgress(true);
                                    status.textContent = "Twig sent " + entry.fileName + " to the host. The Copilot desktop app normally saves it to your Downloads folder without a prompt (duplicates get a (n) suffix); Twig cannot confirm the saved location.";
                                    return;
                                }
                                if (outcome.status === "refused" || outcome.status === "interrupted") {
                                    finishProgress(false);
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
                            finishProgress(false);
                        } catch {
                            finishProgress(false);
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
                        progress.removeAttribute("value");
                        progress.hidden = false;
                        status.textContent = "Download click accepted. Waiting for Twig to observe the request; saving is not confirmed.";
                        void observeDownload();
                    });
                    row.append(link);
                    if (entry.preview === "text" || entry.preview === "image") {
                        const open = text("button", "Open " + entry.fileName);
                        open.type = "button";
                        open.addEventListener("click", event => openPreview(event, entry, open, inventoryRevision));
                        row.append(" ", open);
                    }
                    row.append(progress);
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
        box.append(inventory, status, entries, preview);
        parent.append(box);
    }
    return { mount, leave };
}
