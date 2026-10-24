export const reviewMarkup = `
    <p class="notice">Acquire once explicitly: native preview may import a local proposal journal row and
    update last-preview ordering. It does not authorize or apply. Open, Details, Back and reload do not preview.</p>
    <form id="review-form" class="row">
      <label>Proposal file inside this checkout<input id="proposal-file" required></label>
      <label>Exact canonical SHA-256 digest<input id="proposal-digest" required pattern="[a-f0-9]{64}"></label>
      <label>Expected organization<input id="proposal-org" required></label>
      <label>Expected project<input id="proposal-project" required></label>
      <button id="acquire-review" type="submit">Acquire native preview (writes local journal)</button>
    </form>
    <button id="retained-review" type="button">Show retained observation (no preview)</button>
    <p id="review-status" class="notice" role="status" aria-live="polite">No retained observation. Acquisition is explicit.</p>
    <div id="review-output"></div>`;

export async function reviewClient({ document, request, loadModule }) {
    const form = document.querySelector("#review-form");
    const acquire = document.querySelector("#acquire-review");
    const show = document.querySelector("#retained-review");
    const status = document.querySelector("#review-status");
    const output = document.querySelector("#review-output");
    let requestGeneration = 0;
    const { mountRetainedReview } = await loadModule();
    function render(state) {
        output.replaceChildren();
        if (state.error) {
            status.textContent = `Unavailable: ${state.error}. No automatic retry. A failed or unsupported preview may already have written its local journal.`;
        } else if (state.busy) {
            status.textContent = "Explicit native preview in progress. No second preview will be started.";
        } else if (state.observation) {
            const { rawJson, expected, acquisition } = state.observation;
            status.textContent = `${acquisition.source}; checkout: ${acquisition.checkoutRoot}; file: ${acquisition.file}. ${acquisition.localEffects} ${acquisition.originCoverage} ${acquisition.freshness}`;
            mountRetainedReview(output, rawJson, expected);
        } else {
            status.textContent = "No retained observation. Provider restart does not recover this in-memory review; acquire explicitly.";
        }
    }
    async function run(path, options) {
        const current = ++requestGeneration;
        acquire.disabled = show.disabled = true;
        output.replaceChildren();
        status.textContent = options ? "Acquiring one native preview; local journal writes are possible." : "Loading retained state only.";
        try {
            const state = await request(path, options);
            if (current === requestGeneration) render(state);
        } catch (failure) {
            if (current === requestGeneration) {
                output.replaceChildren();
                status.textContent = `Unavailable: ${failure.message}. No automatic retry; local preview effects may have occurred.`;
            }
        } finally {
            if (current === requestGeneration) acquire.disabled = show.disabled = false;
        }
    }
    form.addEventListener("submit", (event) => {
        event.preventDefault();
        void run("/api/acquire", { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                file: document.querySelector("#proposal-file").value,
                digest: document.querySelector("#proposal-digest").value,
                workspace: { organization: document.querySelector("#proposal-org").value,
                    project: document.querySelector("#proposal-project").value },
            }) });
    });
    show.addEventListener("click", () => { void run("/api/state"); });
    await run("/api/state");
}
