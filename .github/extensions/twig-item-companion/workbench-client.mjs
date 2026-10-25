export const workbenchMarkup = `
  <div class="twig-workbench">
    <form id="bench-form" class="row">
      <label for="bench-name">Saved Bench name or ID
        <input id="bench-name" required maxlength="512" aria-describedby="bench-help">
      </label>
      <button id="bench-load" type="submit">Browse Bench</button>
      <button id="bench-refresh" type="button" disabled>Re-read cache</button>
    </form>
    <details class="bench-disclosure"><summary>Read scope</summary>
      <p id="bench-help" class="muted">Browsing never switches the current Bench or active item. Local draft OPEN/STAGE require explicit buttons; no Save, apply or publication.</p>
    </details>
    <div id="bench-status" class="notice" role="status" aria-live="polite">Enter a saved Bench name or ID.</div>
    <div id="bench-content" hidden>
      <details class="bench-disclosure"><summary>Source and completeness</summary><div id="bench-facts"></div></details>
      <label class="bench-layout-label" for="bench-layout">Item navigation
        <select id="bench-layout"><option value="page">Separate pages</option><option value="split">Tree above item</option></select>
      </label>
      <div id="bench-panes">
      <div id="bench-browse">
        <div class="row">
          <label for="bench-filter">Filter loaded rows locally
            <input id="bench-filter" type="search">
          </label>
          <label for="bench-view">View
            <select id="bench-view"><option value="hierarchy">Tree</option><option value="results">Members</option></select>
          </label>
        </div>
        <p id="bench-counts" role="status" aria-live="polite"></p>
        <div id="bench-rows"></div>
      </div>
      <label id="bench-divider" hidden for="bench-height">Tree height
        <input id="bench-height" type="range" min="120" max="600" step="20" value="260" aria-label="Tree pane height in pixels">
      </label>
      <section id="bench-selection" hidden tabindex="-1"></section>
      </div>
    </div>
  </div>`;

export const workbenchStyles = `
    .twig-workbench section { border: 0; border-radius: 0; padding: 8px 0; margin: 0; }
    .twig-workbench .bench-disclosure { border: 0; border-radius: 0; padding: 4px 0; margin: 4px 0; }
    .twig-workbench .row { gap: 6px; margin-bottom: 8px; }
    .twig-workbench label { font-size: 12px; }
    .twig-workbench input, .twig-workbench select, .twig-workbench button { min-height: 30px; padding: 3px 6px; }
    .bench-layout-label { max-width: 220px; margin: 8px 0; }
    #bench-rows ul { list-style: none; margin: 0; padding-left: 16px; }
    #bench-rows > ul { padding-left: 0; }
    .bench-row { display: flex; align-items: center; gap: 4px; min-width: 0; }
    .bench-toggle, .bench-leaf { flex: 0 0 24px; width: 24px; }
    .bench-toggle { border: 0; background: transparent; }
    .bench-toggle::before { content: ""; display: inline-block; border-top: 4px solid transparent; border-bottom: 4px solid transparent; border-left: 5px solid currentColor; }
    .bench-toggle[aria-expanded="true"]::before { transform: rotate(90deg); }
    .bench-item { text-align: left; border: 0; border-radius: 0; background: transparent; min-width: 0; overflow-wrap: anywhere; }
    .bench-item:hover { background: var(--background-color-muted, var(--twig-background)); text-decoration: underline; }
    .bench-item[aria-pressed="true"] { background: var(--background-color-muted, var(--twig-background)); font-weight: 600; }
    .bench-membership { font-size: 12px; }
    #bench-panes[data-layout="split"] #bench-browse { max-height: var(--bench-tree-height, 260px); overflow: auto; }
    #bench-divider { border-top: 1px solid var(--twig-border); border-bottom: 1px solid var(--twig-border); padding: 4px 0; }
    #bench-divider input { min-height: 24px; padding: 0; max-width: 260px; }
    .bench-item-heading { font-size: var(--text-title-small, 16px); margin: 8px 0; }
    .bench-columns { display: flex; flex-wrap: wrap; gap: 16px; }
    .bench-columns > div { flex: 1 1 240px; min-width: 0; }
    .twig-workbench [hidden] { display: none !important; }
`;

export const workbenchClient = `
    const benchForm = document.querySelector("#bench-form");
    const benchInput = document.querySelector("#bench-name");
    const benchLoad = document.querySelector("#bench-load");
    const benchRefresh = document.querySelector("#bench-refresh");
    const benchStatus = document.querySelector("#bench-status");
    const benchContent = document.querySelector("#bench-content");
    const benchFacts = document.querySelector("#bench-facts");
    const benchBrowse = document.querySelector("#bench-browse");
    const benchRows = document.querySelector("#bench-rows");
    const benchFilter = document.querySelector("#bench-filter");
    const benchView = document.querySelector("#bench-view");
    const benchCounts = document.querySelector("#bench-counts");
    const benchSelection = document.querySelector("#bench-selection");
    const benchLayout = document.querySelector("#bench-layout");
    const benchPanes = document.querySelector("#bench-panes");
    const benchDivider = document.querySelector("#bench-divider");
    const benchHeight = document.querySelector("#bench-height");
    let observation = null;
    let selectedId = null;
    let detailOpen = false;
    let browseScroll = 0;
    let treeScroll = 0;
    let benchBusy = false;
    let generation = 0;
    let detailInFlight = false;
    let wantedDetail = null;
    const collapsed = new Set();
    const rowButtons = new Map();
    benchView.value = "hierarchy";
    benchLayout.value = "page";
    benchHeight.value = "260";

    function applyLayout() {
      const split = benchLayout.value === "split" && detailOpen;
      benchPanes.setAttribute("data-layout", split ? "split" : "page");
      benchBrowse.hidden = detailOpen && !split;
      benchDivider.hidden = !split;
      benchSelection.hidden = !detailOpen;
      for (const [id, button] of rowButtons) button.setAttribute("aria-pressed", String(selectedId === id));
    }

    function matches(row) {
      const query = benchFilter.value.trim().toLowerCase();
      return !query || (row.loaded !== false && [row.id, row.title, row.type, row.state]
        .join(" ").toLowerCase().includes(query));
    }
    function pair(list, label, value) {
      list.append(text("dt", label), text("dd", value));
    }
    function describe(row) {
      return "#" + row.id + " " + (row.loaded === false ? "Not cached" : row.type + " - " + row.title + " - " + row.state);
    }
    function showSelection(row) {
      handoffClient.leave();
      const revoked = editingClient.leave();
      revoked.catch((error) => {
        benchStatus.textContent = "Editing capture revocation failed: " + error.message;
        benchStatus.classList.add("error");
      });
      generation++;
      selectedId = row.id;
      detailOpen = true;
      browseScroll = window.scrollY;
      treeScroll = benchBrowse.scrollTop;
      applyLayout();
      benchSelection.replaceChildren();
      const back = text("button", "Back to Bench");
      back.type = "button";
      back.addEventListener("click", returnToBench);
      const heading = text("h2", describe(row));
      heading.classList.add("bench-item-heading");
      benchSelection.append(back, heading);
      const facts = document.createElement("dl");
      pair(facts, "Checkout", observation.origin.worktreeRoot);
      pair(facts, "Connection", observation.origin.connectionRef);
      pair(facts, "Binding", observation.origin.bindingId);
      pair(facts, "Bench", observation.benchName + " (" + observation.benchId + ")");
      pair(facts, "Work item ID", row.id);
      const member = observation.members.find((entry) => entry.id === row.id);
      pair(facts, "Provenance", member ? (member.seed ? "Local seed" : "raw-mirror member") : "raw-mirror ancestor context (not membership)");
      pair(facts, "Cached row", row.loaded === false ? "Unavailable" : "Loaded");
      pair(facts, "Type icons and provider colors", "Per-row native metadata and admitted icon assets are not exported by this read.");
      if (member) {
        pair(facts, "Selector-derived", member.selected);
        pair(facts, "Pending marker", member.pending + " (not effective local values)");
        pair(facts, "Parent ID", member.parentId ?? "Not carried");
      }
      const seam = "Raw cached summary only. No revision or editing authority is exported by this Bench contract. Full native form/draft detail is unavailable here; opening it must not retarget the shared current Bench.";
      const detailNote = text("p", seam, "muted");
      const source = document.createElement("details");
      source.classList.add("bench-disclosure");
      source.append(text("summary", "Item source and read limits"), facts, detailNote);
      benchSelection.append(source);
      benchSelection.focus();
      if (member && member.loaded) {
        const job = { gen: generation, obs: observation, id: row.id, note: detailNote, seam };
        void revoked.then(() => { if (!stale(job)) requestDetail(job); }, (error) => {
          detailNote.textContent = "Editing capture revocation failed; detail was not loaded: " + error.message;
          benchSelection.append(detailNote);
        });
      }
    }
    function renderNativeValue(value) {
      if (!value) return "No field value was carried.";
      if (value.state === 0) return value.full ?? "";
      if (value.state === 1) return "Empty on server.";
      if (value.state === 2) return "Not carried by Twig.";
      return "Unknown native value state.";
    }
    function stale(job) {
      return !detailOpen || job.gen !== generation || job.obs !== observation || selectedId !== job.id;
    }
    function requestDetail(job) {
      if (detailInFlight) {
        wantedDetail = job;
        job.note.textContent = job.seam + " Waiting for the previous detail read to finish; no second read is started.";
        return;
      }
      void showDetail(job);
    }
    async function showDetail(job) {
      const { id, note, seam } = job;
      detailInFlight = true;
      applyBusy();
      try {
        const result = await request("/api/bench", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ action: "detail", workItemId: id }),
        });
        if (stale(job)) return;
        const detail = result.benchDetail;
        const box = document.createElement("section");
        const c = detail.completeness;
        const meta = document.createElement("dl");
        pair(meta, "Detail observation", "raw-mirror, explicit Bench " + detail.benchName + " (" + detail.benchId + "); cache-only");
        pair(meta, "Membership", "selected=" + detail.membership.selected + ", seed=" + detail.membership.seed + ", pending=" + detail.membership.pending);
        pair(meta, "Source completeness", "Not source complete; remote child count unknown (" + c.cachedChildCount + " cached children)");
        pair(meta, "Parent cached", c.parentCached ?? "Not applicable");
        pair(meta, "Links verified", c.linksVerified);
        pair(meta, "Completeness reasons", c.reasons.join(", "));
        pair(meta, "Layout", "Server layout unavailable (cache-only); Twig fallback form shown");
        const coverage = document.createElement("details");
        coverage.classList.add("bench-disclosure");
        coverage.append(text("summary", "Native layout and coverage"), meta);
        box.append(text("h3", "Native detail (read-only)"), coverage);
        function renderControl(control, target) {
          if (!control.visible) return;
          const field = document.createElement("div");
          field.classList.add("field");
          field.append(text("div", control.label || control.id || "Unnamed control", "field-name"),
            text("pre", control.isContribution ? "Unsupported native contribution." : renderNativeValue(control.value)));
          target.append(field);
        }
        for (const control of detail.document.systemControls) renderControl(control, box);
        for (const page of detail.document.pages) {
          if (!page.visible) continue;
          const pageBox = document.createElement("section");
          pageBox.append(text("h3", page.label || page.id || "Native page"));
          if (page.isContribution) {
            pageBox.append(text("p", "Unsupported native page contribution."));
          } else {
            const columns = document.createElement("div");
            columns.classList.add("bench-columns");
            for (const section of page.sections) {
              const column = document.createElement("div");
              for (const group of section.groups) {
                if (!group.visible) continue;
                const groupBox = document.createElement("section");
                if (group.label) groupBox.append(text("h4", group.label));
                if (group.isContribution) groupBox.append(text("p", "Unsupported native group contribution."));
                else for (const control of group.controls) renderControl(control, groupBox);
                column.append(groupBox);
              }
              columns.append(column);
            }
            pageBox.append(columns);
          }
          if (page.pageType !== "custom" && !page.sections.length) {
            pageBox.append(text("p", "Page content is not exported by this native read."));
          }
          box.append(pageBox);
        }
        note.textContent = "Native explicit-Bench detail loaded. Displayed as inert text; no edit, save or apply authority.";
        benchSelection.append(box);
        editingClient.mount(benchSelection, detail);
        handoffClient.mount(benchSelection, detail);
      } catch (error) {
        if (stale(job)) return;
        note.textContent = seam + " Explicit-Bench detail failed: " + (error instanceof Error ? error.message : "unavailable") + ".";
        note.setAttribute("role", "alert");
        // The read failure must be visible even when provenance is collapsed.
        benchSelection.append(note);
      } finally {
        detailInFlight = false;
        const next = wantedDetail;
        wantedDetail = null;
        applyBusy();
        if (next && !stale(next)) void showDetail(next);
      }
    }
    function returnToBench() {
      handoffClient.leave();
      void editingClient.leave().catch((error) => {
        benchStatus.textContent = "Editing capture revocation failed: " + error.message;
        benchStatus.classList.add("error");
      });
      generation++;
      if (benchLayout.value === "split") treeScroll = benchBrowse.scrollTop;
      detailOpen = false;
      applyLayout();
      benchBrowse.scrollTop = treeScroll;
      const selectedButton = rowButtons.get(selectedId);
      const hiddenAncestor = (node) => {
        for (let parent = node.parentElement; parent && parent !== benchBrowse; parent = parent.parentElement) {
          if (parent.hidden) return true;
        }
        return false;
      };
      if (selectedButton && !hiddenAncestor(selectedButton)) selectedButton.focus();
      else benchFilter.focus();
      window.scrollTo(0, browseScroll);
    }
    function rowButton(row) {
      const button = text("button", describe(row));
      button.type = "button";
      button.classList.add("bench-item");
      button.disabled = benchBusy;
      button.setAttribute("title", observation.origin.connectionRef + " / " + observation.origin.organization +
        " / " + observation.origin.project + " / #" + row.id);
      button.setAttribute("aria-label", describe(row) + "; " + observation.origin.connectionRef +
        "; " + observation.origin.organization + "; " + observation.origin.project);
      button.setAttribute("aria-pressed", String(selectedId === row.id));
      rowButtons.set(row.id, button);
      button.addEventListener("click", () => showSelection(row));
      return button;
    }
    function renderRows() {
      if (!observation) return;
      benchRows.replaceChildren();
      rowButtons.clear();
      const c = observation.completeness;
      if (benchView.value === "hierarchy") {
        let shown = 0;
        function branch(nodes) {
          const list = document.createElement("ul");
          for (const row of nodes) {
            const children = branch(row.children);
            if (!matches(row) && !children.childElementCount) continue;
            const item = document.createElement("li");
            const line = document.createElement("div");
            line.classList.add("bench-row");
            if (children.childElementCount) {
              const toggle = text("button", "");
              toggle.type = "button";
              toggle.classList.add("bench-toggle");
              const expanded = !collapsed.has(row.id) || !!benchFilter.value.trim();
              toggle.setAttribute("aria-expanded", String(expanded));
              toggle.setAttribute("aria-label", (expanded ? "Collapse" : "Expand") + " children of #" + row.id);
              children.hidden = !expanded;
              toggle.addEventListener("click", () => {
                const open = children.hidden;
                children.hidden = !open;
                if (open) collapsed.delete(row.id); else collapsed.add(row.id);
                toggle.setAttribute("aria-expanded", String(open));
                toggle.setAttribute("aria-label", (open ? "Collapse" : "Expand") + " children of #" + row.id);
              });
              line.append(toggle);
            } else line.append(text("span", "", "bench-leaf"));
            line.append(rowButton(row), text("span", " - " + row.membership, "muted bench-membership"));
            item.append(line);
            if (children.childElementCount) item.append(children);
            list.append(item);
            shown++;
          }
          return list;
        }
        benchRows.append(branch(observation.roots));
        benchCounts.textContent = shown + " matching/context hierarchy rows of " + c.renderedNodeCount +
          " rendered nodes; hierarchy is " + (c.projectionComplete ? "complete for known cache evidence" : "partial") + ". Remote total unknown.";
      } else {
        const rows = observation.members.filter(matches);
        for (const row of rows) {
          const item = document.createElement("p");
          item.append(rowButton(row), text("span", row.seed ? " - local seed" : " - raw-mirror", "muted"));
          benchRows.append(item);
        }
        benchCounts.textContent = rows.length + " visible known member rows; " + c.loadedMemberCount +
          " loaded / " + c.knownMemberCount + " known, " + c.unloadedKnownMemberCount +
          " known not cached. Remote total and undiscovered count unknown.";
      }
      if (!benchRows.childElementCount || (benchView.value === "hierarchy" && !benchRows.children[0].childElementCount)) {
        benchRows.append(text("p", "No matching loaded rows in this view. This is not proof of an empty source."));
      }
    }
    function renderBench(value) {
      observation = value;
      generation++;
      selectedId = null;
      detailOpen = false;
      collapsed.clear();
      benchSelection.hidden = true;
      benchBrowse.hidden = false;
      applyLayout();
      benchContent.hidden = false;
      benchFacts.replaceChildren(text("h2", value.benchName + " (" + value.benchId + ")"));
      const facts = document.createElement("dl");
      for (const [key, entry] of Object.entries(value.origin)) pair(facts, key, entry);
      pair(facts, "Observation", "raw-mirror (local seeds labelled separately; no durable-edit overlay)");
      pair(facts, "Source completeness", "Unknown remote total; cache-only, never source complete");
      pair(facts, "Projection", value.completeness.projectionComplete ? "Complete for known cache evidence" : "Partial");
      for (const key of ["omittedLoadedMemberIds", "missingAncestorIds", "prunedAncestorIds", "depthOmittedIds",
        "depthDown", "depthUp", "workingLevel", "reasons"]) {
        const entry = value.completeness[key];
        pair(facts, key, Array.isArray(entry) ? entry.join(", ") || "None reported" : entry ?? "Not carried");
      }
      benchFacts.append(facts);
      const configuration = document.createElement("details");
      configuration.append(text("summary", "Native saved Bench configuration (read-only)"),
        text("pre", JSON.stringify(value.configuration, null, 2)));
      benchFacts.append(configuration);
      renderRows();
    }
    function setBenchBusy(busy) {
      benchBusy = busy;
      applyBusy();
      renderRows();
    }
    function applyBusy() {
      const busy = benchBusy || detailInFlight;
      benchLoad.disabled = benchInput.disabled = busy;
      benchRefresh.disabled = busy || !observation;
      benchFilter.disabled = benchView.disabled = busy;
    }
    async function performBench(action, bench) {
      setBenchBusy(true);
      benchStatus.classList.remove("error");
      benchStatus.textContent = "Reading native cache; no sync or current-Bench change.";
      try {
        handoffClient.leave();
        await editingClient.leave();
        const result = await request("/api/bench", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ action, bench }),
        });
        renderBench(result.benchObservation);
        benchInput.value = result.benchObservation.requestedBench;
        benchStatus.textContent = "Read-only Bench loaded. Remote completeness unknown; no edit/apply authority.";
      } catch (error) {
        benchStatus.classList.add("error");
        benchStatus.textContent = (observation ? "Read failed; previous qualified observation retained. " : "") +
          (error instanceof Error ? error.message : "Native Bench read failed.");
      } finally { setBenchBusy(false); }
    }
    benchForm.addEventListener("submit", (event) => {
      event.preventDefault();
      void performBench("load", benchInput.value);
    });
    benchRefresh.addEventListener("click", () => void performBench("refresh"));
    benchFilter.addEventListener("input", renderRows);
    benchView.addEventListener("change", renderRows);
    benchLayout.addEventListener("change", () => {
      treeScroll = benchBrowse.scrollTop;
      applyLayout();
      benchBrowse.scrollTop = treeScroll;
    });
    benchHeight.addEventListener("input", () => {
      const height = Number(benchHeight.value);
      if (Number.isInteger(height) && height >= 120 && height <= 600) {
        benchPanes.style.setProperty("--bench-tree-height", height + "px");
      }
    });
    benchSelection.addEventListener("keydown", (event) => {
      if (event.key === "Escape") { event.preventDefault(); returnToBench(); }
    });
    void request("/api/state").then((state) => {
      if (state.benchObservation) {
        renderBench(state.benchObservation);
        benchInput.value = state.benchObservation.requestedBench;
        benchStatus.textContent = "Read-only cached Bench restored for this panel; remote total unknown.";
      }
      if (state.error) { benchStatus.classList.add("error"); benchStatus.textContent = state.error; }
      setBenchBusy(state.busy);
    }).catch((error) => {
      benchStatus.classList.add("error");
      benchStatus.textContent = error instanceof Error ? error.message : "Canvas state could not be read.";
    });
`;
