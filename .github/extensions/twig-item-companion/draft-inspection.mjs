import { validateDraftDocument } from "./draft-document.mjs";

export function renderDraftInspection(root, document, expectedKey) {
    root.replaceChildren();
    const node = (tag, text) => {
        const element = root.ownerDocument.createElement(tag);
        element.textContent = text;
        return element;
    };
    let doc;
    try {
        doc = validateDraftDocument(document, expectedKey, expectedKey?.checkoutRoot);
    } catch (error) {
        const failure = node("p", error.message);
        failure.setAttribute("role", "alert");
        root.append(failure);
        return { available: false };
    }
    root.append(
        node("h1", "Native draft inspection - no edit or Save authority"),
        node("p", "Independently captured observations: only the qualified item key is correlated. Whole-Bench origin continuity is not established."),
        node("p", "Inspection may compare-and-swap local observation/conflict metadata. It is not zero-write and has no offline guarantee. Baseline and proposed intent are not edited here."),
        node("pre", JSON.stringify(doc.itemKey, null, 2)),
        node("p", `Cached inspection: ${doc.status}; draft: ${doc.draftStatus}; completeness: ${doc.observationCompleteness} (draft effects only, not the whole item).`),
        node("pre", `Native baseline revision: ${doc.baselineRevision}\nNative observed revision: ${doc.observedRevision}\nNative current revision: ${doc.currentRevision}\nDraft CAS generation: ${doc.generation} (not an item revision or authorization)`),
        node("h2", "Advisory native eligibility - server is final authority"),
        node("pre", JSON.stringify(doc.eligibility, null, 2)),
        node("p", `Conflicts: ${doc.hasConflicts}; pending local work: ${doc.localPendingWork}. Eligibility is not permission, a Save guarantee, or host authentication.`),
    );
    for (const field of doc.fields) {
        root.append(node("h2", field.fieldRef),
            node("pre", JSON.stringify(field, null, 2)));
    }
    root.append(node("h2", "Exact retained changes"), node("pre", JSON.stringify(doc.changes, null, 2)),
        node("h2", "Exact retained conflicts"), node("pre", JSON.stringify(doc.conflicts, null, 2)));
    return { available: true };
}
