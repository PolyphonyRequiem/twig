export const workbenchMarkup = `
    <form id="bench-form" class="row">
      <label for="bench-name">Saved Bench name or ID
        <input id="bench-name" required maxlength="512" aria-describedby="bench-help">
      </label>
      <button id="bench-load" type="submit">Browse Bench</button>
      <button id="bench-refresh" type="button" disabled>Re-read cache</button>
    </form>
    <p id="bench-help" class="muted">Explicit saved target; never switches the current Bench or active item. No sync, editing or apply.</p>
    <div id="bench-status" class="notice" role="status" aria-live="polite">Enter a saved Bench name or ID.</div>
    <div id="bench-content" hidden>
      <section id="bench-facts"></section>
      <div id="bench-browse">
        <div class="row">
          <label for="bench-filter">Filter loaded rows locally
            <input id="bench-filter" type="search">
          </label>
          <label for="bench-view">View
            <select id="bench-view"><option value="results">Members</option><option value="hierarchy">Hierarchy</option></select>
          </label>
        </div>
        <p id="bench-counts" role="status" aria-live="polite"></p>
        <div id="bench-rows"></div>
      </div>
      <section id="bench-selection" hidden tabindex="-1"></section>
    </div>`;

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
    let observation = null;
    let selectedId = null;
    let returnButton = null;
    let browseScroll = 0;
    let benchBusy = false;
    let generation = 0;
    let detailInFlight = false;
    let wantedDetail = null;
    benchView.value = "results";

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
    function showSelection(row, button) {
      generation++;
      selectedId = row.id;
      returnButton = button;
      browseScroll = window.scrollY;
      benchBrowse.hidden = true;
      benchSelection.hidden = false;
      benchSelection.replaceChildren();
      const back = text("button", "Back to Bench");
      back.type = "button";
      back.addEventListener("click", returnToBench);
      benchSelection.append(back, text("h2", describe(row)));
      const facts = document.createElement("dl");
      pair(facts, "Checkout", observation.origin.worktreeRoot);
      pair(facts, "Connection", observation.origin.connectionRef);
      pair(facts, "Binding", observation.origin.bindingId);
      pair(facts, "Bench", observation.benchName + " (" + observation.benchId + ")");
      pair(facts, "Work item ID", row.id);
      const member = observation.members.find((entry) => entry.id === row.id);
      pair(facts, "Provenance", member ? (member.seed ? "Local seed" : "raw-mirror member") : "raw-mirror ancestor context (not membership)");
      pair(facts, "Cached row", row.loaded === false ? "Unavailable" : "Loaded");
      if (member) {
        pair(facts, "Selector-derived", member.selected);
        pair(facts, "Pending marker", member.pending + " (not effective local values)");
        pair(facts, "Parent ID", member.parentId ?? "Not carried");
      }
      const seam = "Raw cached summary only. No revision or editing authority is exported by this Bench contract. Full native form/draft detail is unavailable here; opening it must not retarget the shared current Bench.";
      const detailNote = text("p", seam, "muted");
      benchSelection.append(facts, detailNote);
      benchSelection.focus();
      if (member && member.loaded) requestDetail({ gen: generation, obs: observation, id: row.id, note: detailNote, seam });
    }
    function renderNativeValue(value) {
      if (!value) return "No field value was carried.";
      if (value.state === 0) return value.full ?? "";
      if (value.state === 1) return "Empty on server.";
      if (value.state === 2) return "Not carried by Twig.";
      return "Unknown native value state.";
    }
    function stale(job) {
      return job.gen !== generation || job.obs !== observation || selectedId !== job.id;
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
        box.append(text("h3", "Native detail (read-only)"), meta);
        const controls = [...detail.document.systemControls,
          ...detail.document.pages.flatMap((page) => page.sections.flatMap((s) => s.groups.flatMap((g) => g.controls)))];
        for (const control of controls) {
          if (control.isContribution) continue;
          const field = document.createElement("div");
          field.append(text("div", control.label || control.id || "Unnamed control", "field-name"),
            text("pre", renderNativeValue(control.value)));
          box.append(field);
        }
        note.textContent = "Native explicit-Bench detail loaded. Displayed as inert text; no edit, save or apply authority.";
        benchSelection.append(box);
      } catch (error) {
        if (stale(job)) return;
        note.textContent = seam + " Explicit-Bench detail failed: " + (error instanceof Error ? error.message : "unavailable") + ".";
      } finally {
        detailInFlight = false;
        const next = wantedDetail;
        wantedDetail = null;
        applyBusy();
        if (next && !stale(next)) void showDetail(next);
      }
    }
    function returnToBench() {
      generation++;
      benchSelection.hidden = true;
      benchBrowse.hidden = false;
      if (returnButton) returnButton.focus();
      window.scrollTo(0, browseScroll);
    }
    function rowButton(row) {
      const button = text("button", describe(row));
      button.type = "button";
      button.disabled = benchBusy;
      button.setAttribute("aria-pressed", String(selectedId === row.id));
      if (selectedId === row.id) returnButton = button;
      button.addEventListener("click", () => showSelection(row, button));
      return button;
    }
    function renderRows() {
      if (!observation) return;
      benchRows.replaceChildren();
      const c = observation.completeness;
      if (benchView.value === "hierarchy") {
        let shown = 0;
        function branch(nodes) {
          const list = document.createElement("ul");
          for (const row of nodes) {
            const children = branch(row.children);
            if (!matches(row) && !children.childElementCount) continue;
            const item = document.createElement("li");
            item.append(rowButton(row), text("span", " - " + row.membership, "muted"));
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
      returnButton = null;
      benchSelection.hidden = true;
      benchBrowse.hidden = false;
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
