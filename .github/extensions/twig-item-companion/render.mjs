export function renderPage({ token, nonce }) {
    return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Twig item companion</title>
  <style nonce="${nonce}">
    :root {
      font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
      color: var(--text-color-default, #1f2328);
      background: var(--background-color-default, #ffffff);
      font-size: var(--text-body-medium, 14px);
      line-height: var(--leading-body-medium, 20px);
    }
    :root[data-color-mode="dark"] { color-scheme: dark; }
    :root[data-color-mode="light"] { color-scheme: light; }
    * { box-sizing: border-box; }
    body { margin: 0; padding: 16px; }
    main { max-width: 900px; margin: 0 auto; }
    h1, h2, h3, p { margin-top: 0; }
    h1 { font-size: var(--text-title-medium, 20px); line-height: var(--leading-title-medium, 26px); }
    h2 { font-size: var(--text-title-small, 16px); line-height: var(--leading-title-small, 22px); }
    h3 { font-size: var(--text-body-large, 16px); }
    .muted { color: var(--text-color-muted, #656d76); }
    .row { display: flex; flex-wrap: wrap; align-items: end; gap: 8px; margin-bottom: 16px; }
    label { display: grid; gap: 4px; flex: 1 1 220px; }
    input, button {
      min-height: 36px;
      border: 1px solid var(--border-color-default, #d1d9e0);
      border-radius: 6px;
      padding: 6px 10px;
      color: inherit;
      background: var(--background-color-default, #ffffff);
      font: inherit;
    }
    button { cursor: pointer; font-weight: var(--font-weight-semibold, 600); }
    button:focus-visible, input:focus-visible {
      outline: 2px solid var(--color-focus-outline, #0969da);
      outline-offset: 2px;
    }
    button:disabled { cursor: not-allowed; opacity: .6; }
    section, details {
      border: 1px solid var(--border-color-default, #d1d9e0);
      border-radius: 8px;
      padding: 12px;
      margin: 12px 0;
      min-width: 0;
    }
    details > summary { cursor: pointer; font-weight: var(--font-weight-semibold, 600); }
    .notice { border-left: 3px solid var(--true-color-blue, #0969da); padding: 8px 12px; }
    .error { border-left-color: var(--true-color-red, #cf222e); }
    .group { margin: 10px 0; padding-left: 12px; border-left: 1px solid var(--border-color-default, #d1d9e0); }
    .field { margin: 10px 0; }
    .field-name { font-weight: var(--font-weight-semibold, 600); overflow-wrap: anywhere; }
    pre {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      font-family: var(--font-mono, Consolas, monospace);
      font-size: var(--text-code-inline, 12px);
      margin: 4px 0 0;
    }
    dl { display: grid; grid-template-columns: minmax(100px, 180px) minmax(0, 1fr); gap: 6px 12px; }
    dt { color: var(--text-color-muted, #656d76); }
    dd { margin: 0; overflow-wrap: anywhere; }
    @media (max-width: 520px) {
      body { padding: 12px; }
      dl { grid-template-columns: 1fr; gap: 2px; }
      dd { margin-bottom: 8px; }
    }
  </style>
</head>
<body>
  <main>
    <header>
      <h1>Twig item companion</h1>
      <p class="muted">Read-only semantic detail from the current admitted Twig workspace. This view does not edit, save, apply, or publish work.</p>
    </header>
    <form id="load-form" class="row">
      <label for="work-item-id">Work item ID or local seed ID
        <input id="work-item-id" name="workItemId" type="number" step="1" required aria-describedby="id-help">
      </label>
      <button id="load-button" type="submit">Load detail</button>
      <button id="refresh-button" type="button" disabled>Refresh detail</button>
    </form>
    <p id="id-help" class="muted">The read runs through Twig in this session's workspace; a rejected admission or native read is shown as an error.</p>
    <div id="status" class="notice" role="status" aria-live="polite">Enter an ID to load cached detail.</div>
    <div id="observation"></div>
  </main>
  <script nonce="${nonce}">
    const token = ${JSON.stringify(token)};
    const form = document.querySelector("#load-form");
    const input = document.querySelector("#work-item-id");
    const loadButton = document.querySelector("#load-button");
    const refreshButton = document.querySelector("#refresh-button");
    const status = document.querySelector("#status");
    const output = document.querySelector("#observation");

    async function request(path, options = {}) {
      const response = await fetch(path, {
        ...options,
        headers: {
          Authorization: "Bearer " + token,
          ...(options.headers || {}),
        },
        cache: "no-store",
      });
      const value = await response.json();
      if (!response.ok) throw new Error(value.error || "Twig request failed.");
      return value;
    }

    function text(tag, value, className) {
      const node = document.createElement(tag);
      node.textContent = value == null ? "" : String(value);
      if (className) node.className = className;
      return node;
    }

    function addPair(list, label, value) {
      list.append(text("dt", label));
      list.append(text("dd", value));
    }

    function renderValue(value) {
      if (!value) return "No field value was carried.";
      if (value.state === 0) return value.full ?? "";
      if (value.state === 1) return "Empty on server.";
      if (value.state === 2) return "Not carried by Twig.";
      return "Unknown native value state.";
    }

    function renderControl(control) {
      const field = document.createElement("div");
      field.className = "field";
      field.append(text("div", control.label || control.id || "Unnamed control", "field-name"));
      const meta = [
        control.id,
        control.controlType,
        control.visible === false ? "not visible in native layout" : "visible in native layout",
        control.readOnly ? "server read-only" : "server did not mark read-only (Twig editing unavailable)",
        control.isContribution ? "contribution control" : "",
      ].filter(Boolean).join(" · ");
      field.append(text("div", meta, "muted"));
      if (control.value) field.append(text("pre", renderValue(control.value)));
      else if (!control.isContribution) field.append(text("pre", "No value was exported."));
      return field;
    }

    function renderDocument(formDocument) {
      const section = document.createElement("section");
      section.append(text("h2", "Native form"));
      section.append(text("p", "Page, section, group, and control order comes from Twig's native detail document. Values are displayed as inert text.", "muted"));

      if (Array.isArray(formDocument.systemControls) && formDocument.systemControls.length) {
        const group = document.createElement("div");
        group.className = "group";
        group.append(text("h3", "System controls"));
        for (const control of formDocument.systemControls) group.append(renderControl(control));
        section.append(group);
      }

      for (const page of formDocument.pages || []) {
        const details = document.createElement("details");
        details.open = page.visible === true;
        const summary = text("summary", page.label + " · " + page.pageType + (page.visible ? "" : " · not visible"));
        details.append(summary);
        if (page.isContribution) details.append(text("p", "Contribution page.", "muted"));
        for (const part of page.sections || []) {
          const column = document.createElement("div");
          column.className = "group";
          column.append(text("h3", part.id || "Section"));
          for (const groupData of part.groups || []) {
            const group = document.createElement("div");
            group.className = "group";
            group.append(text("h3", groupData.label || groupData.id || "Group"));
            if (groupData.visible === false) group.append(text("p", "Not visible in native layout.", "muted"));
            if (groupData.isContribution) group.append(text("p", "Contribution group.", "muted"));
            for (const control of groupData.controls || []) group.append(renderControl(control));
            column.append(group);
          }
          details.append(column);
        }
        section.append(details);
      }
      return section;
    }

    function renderObservation(value) {
      output.replaceChildren();
      const item = value.item || {};
      const header = document.createElement("section");
      header.append(text("h2", "#" + value.itemKey.workItemId + " " + (item.title || "Untitled item")));
      const facts = document.createElement("dl");
      addPair(facts, "Revision", item.revision);
      addPair(facts, "Bench", value.benchName + " (" + value.benchId + ")");
      addPair(facts, "Connection", value.itemKey.connectionId);
      addPair(facts, "Binding", value.itemKey.bindingId);
      addPair(facts, "Identity", value.identityId);
      addPair(facts, "Work item type", value.document.workItemTypeReferenceName);
      addPair(facts, "Process", value.document.processId);
      addPair(facts, "Layout", value.layoutStatus + " · " + value.layoutSource);
      addPair(facts, "Last synced", item.lastSyncedAt || "Not recorded");
      addPair(facts, "Local changes", value.hasLocalChanges ? "Present in cached snapshot" : "None reported");
      if (value.lockedTypeReferenceName) addPair(facts, "Locked type", value.lockedTypeReferenceName);
      if (value.linksVerifiedAt) addPair(facts, "Links verified", value.linksVerifiedAt);
      addPair(facts, "Editing", value.editing);
      addPair(facts, "Related-item coverage", value.relatedItemsCoverage);
      addPair(facts, "Icon asset authority", value.iconAssetAuthority);
      header.append(facts);
      output.append(header);
      output.append(renderDocument(value.document));

      const fields = item.fields;
      if (fields && typeof fields === "object") {
        const extras = document.createElement("details");
        extras.append(text("summary", "Cached snapshot fields"));
        const list = document.createElement("dl");
        for (const [name, fieldValue] of Object.entries(fields)) {
          addPair(list, name, fieldValue == null ? "Empty" : fieldValue);
        }
        extras.append(list);
        output.append(extras);
      }

      if (value.appearance) {
        const appearance = document.createElement("section");
        appearance.append(text("h2", "Cached type appearance"));
        const details = document.createElement("dl");
        addPair(details, "Type", value.appearance.name);
        addPair(details, "Icon ID", value.appearance.iconId);
        addPair(details, "Color metadata", value.appearance.color);
        addPair(details, "Source", value.appearanceSource);
        appearance.append(details);
        output.append(appearance);
      }
    }

    function setBusy(busy) {
      loadButton.disabled = busy;
      refreshButton.disabled = busy || !output.childElementCount;
      input.disabled = busy;
    }

    async function perform(action, workItemId) {
      setBusy(true);
      status.classList.remove("error");
      status.textContent = action === "refresh" ? "Refreshing native detail…" : "Loading native detail…";
      try {
        const result = await request("/api/detail", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(action === "refresh" ? { action } : { action, workItemId }),
        });
        renderObservation(result.observation);
        input.value = String(result.observation.itemKey.workItemId);
        status.textContent = "Read-only detail loaded. This capture does not authorize edits or proposal changes.";
      } catch (error) {
        status.classList.add("error");
        const message = error instanceof Error ? error.message : "Twig detail could not be read.";
        status.textContent = output.childElementCount
          ? "The latest read failed; the previous observation remains displayed. " + message
          : message;
      } finally {
        setBusy(false);
      }
    }

    form.addEventListener("submit", (event) => {
      event.preventDefault();
      const workItemId = Number(input.value);
      if (!Number.isInteger(workItemId) || workItemId === 0 || Math.abs(workItemId) > 2147483647) {
        status.classList.add("error");
        status.textContent = "Enter a non-zero 32-bit integer ID.";
        return;
      }
      void perform("load", workItemId);
    });
    refreshButton.addEventListener("click", () => void perform("refresh"));

    void request("/api/state").then((state) => {
      if (state.observation) {
        renderObservation(state.observation);
        input.value = String(state.observation.itemKey.workItemId);
        if (state.error) {
          status.classList.add("error");
          status.textContent = "The latest read failed; the previous observation remains displayed. " + state.error;
        } else {
          status.textContent = "Read-only detail restored for this open canvas instance.";
        }
      } else if (state.error) {
        status.classList.add("error");
        status.textContent = state.error;
      }
      setBusy(state.busy);
    }).catch((error) => {
      status.classList.add("error");
      status.textContent = error instanceof Error ? error.message : "Canvas state could not be loaded.";
    });
  </script>
</body>
</html>`;
}
