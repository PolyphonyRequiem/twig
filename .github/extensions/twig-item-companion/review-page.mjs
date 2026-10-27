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
    <div id="review-output"></div>
    <form id="apply-form">
      <h2>Authorize and apply this exact retained proposal</h2>
      <p class="notice">This separate action can write to the tracker. Review every effect above first.
      An assessment, acquisition, Details or reload never grants authority. Native authorization,
      digest, precondition and journal gates remain authoritative.</p>
      <label>Re-enter the reviewed SHA-256 digest<input id="apply-digest" required pattern="[a-f0-9]{64}"></label>
      <label>Authorizing identity<input id="apply-authorizer" required maxlength="512"></label>
      <label>Authorization rationale (optional)<input id="apply-rationale" maxlength="2048"></label>
      <label><input id="apply-consent" type="checkbox" required>
        I authorize these exact reviewed effects, not a future replacement proposal.</label>
      <button id="apply-proposal" type="submit" disabled>Authorize and apply (writes tracker)</button>
    </form>
    <pre id="apply-outcome" role="status" aria-live="polite"></pre>`;

export async function reviewClient({ document, request, loadModule }) {
    const form = document.querySelector("#review-form");
    const acquire = document.querySelector("#acquire-review");
    const show = document.querySelector("#retained-review");
    const status = document.querySelector("#review-status");
    const output = document.querySelector("#review-output");
    const applyForm = document.querySelector("#apply-form");
    const applyButton = document.querySelector("#apply-proposal");
    const applyOutcome = document.querySelector("#apply-outcome");
    let retained = null, captureId = null, applyReady = false;
    let requestGeneration = 0;
    const { mountRetainedReview } = await loadModule();
    function render(state) {
        retained = state.observation;
        if (captureId !== retained?.expected.captureId) {
            captureId = retained?.expected.captureId;
            for (const selector of ["#apply-digest", "#apply-authorizer", "#apply-rationale"]) {
                document.querySelector(selector).value = "";
            }
            document.querySelector("#apply-consent").checked = false;
        }
        const envelope = retained ? JSON.parse(retained.rawJson) : null;
        applyReady = !!(state.applyAvailable && !state.busy && !state.applyAttempt && envelope?.canApply &&
            envelope.reviewModel.modelVersion === 1 && envelope.reviewModel.operations.length > 0 && !envelope.issues.length &&
            !envelope.pendingChanges.length && !envelope.reviewModel.blockers.length);
        applyButton.disabled = !applyReady;
        applyOutcome.textContent = state.applyAttempt
            ? `${state.applyAttempt.status === "verified" ? "Native journal: every declared operation Verified." :
                "Not verified. Effects may already exist; inspect the native journal before any new authorization."}\n${JSON.stringify(state.applyAttempt, null, 2)}`
            : "No Apply submitted. Authorization is explicit and bound to this retained capture.";
        output.replaceChildren();
        if (state.error) {
            status.textContent = `Unavailable: ${state.error}. No automatic retry. A failed or unsupported preview may already have written its local journal.`;
        } else if (state.busy) {
            status.textContent = state.applyAttempt ? "Explicit native Apply in progress. No second action will be started." :
                "Explicit native preview in progress. No second preview will be started.";
        }
        if (state.observation) {
            const { rawJson, expected, acquisition } = state.observation;
            if (!state.error && !state.busy) status.textContent = `${acquisition.source}; checkout: ${acquisition.checkoutRoot}; file: ${acquisition.file}. ${acquisition.localEffects} ${acquisition.originCoverage} ${acquisition.freshness}`;
            mountRetainedReview(output, rawJson, expected);
        } else if (!state.error && !state.busy) {
            status.textContent = "No retained observation. Provider restart does not recover this in-memory review; acquire explicitly.";
        }
    }
    async function run(path, options) {
        const current = ++requestGeneration;
        acquire.disabled = show.disabled = applyButton.disabled = true;
        applyReady = false;
        output.replaceChildren();
        status.textContent = path === "/api/apply" ? "Applying one exact authorized proposal; tracker effects are possible." :
            options ? "Acquiring one native preview; local journal writes are possible." : "Loading retained state only.";
        try {
            const state = await request(path, options);
            if (current === requestGeneration) render(state);
        } catch (failure) {
            if (current === requestGeneration) {
                output.replaceChildren();
                status.textContent = `Unavailable: ${failure.message}. No automatic retry; native effects may have occurred. Show retained state to inspect the outcome.`;
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
    applyForm.addEventListener("submit", (event) => {
        event.preventDefault();
        if (!applyReady || !retained || !document.querySelector("#apply-consent").checked) return;
        const input = {
            captureId: retained.expected.captureId,
            confirm: document.querySelector("#apply-digest").value,
            authorize: document.querySelector("#apply-authorizer").value,
        };
        const rationale = document.querySelector("#apply-rationale").value;
        if (rationale.trim()) input.rationale = rationale;
        void run("/api/apply", { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify(input) });
    });
    await run("/api/state");
}
