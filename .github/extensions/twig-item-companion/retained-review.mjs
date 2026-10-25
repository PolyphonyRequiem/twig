// Shape validation is not capture authentication. This module performs no acquisition or mutation.
const KINDS = { Batch: "batch", AddLink: "add-link", RemoveLink: "remove-link", PublishSeed: "publish-seed", Delete: "delete" };
const EFFECTS = {
    batch: ["field-set", "field-clear"], "add-link": ["link-add"],
    "remove-link": ["link-remove"], "publish-seed": ["seed-publish"], delete: ["work-item-delete"],
};
const record = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const text = (v) => typeof v === "string";
const nonempty = (v) => text(v) && v.trim().length > 0;
const nullableText = (v) => v === null || text(v);
const id = (v) => Number.isInteger(v) && v !== 0 && Math.abs(v) <= 2147483647;
const publishedId = (v) => id(v) && v > 0;
const nullableId = (v) => v === null || id(v);
const digest = (v) => text(v) && /^[a-f0-9]{64}$/.test(v);
const workspace = (v) => record(v) && nonempty(v.organization) && nonempty(v.project);
const sameWorkspace = (a, b) => workspace(a) && workspace(b) &&
    a.organization === b.organization && a.project === b.project;
const providerReference = (v) => record(v) && nonempty(v.localId) && record(v.externalIdentity) &&
    ["providerKind", "resourceScope", "nativeId"].every((key) => nonempty(v.externalIdentity[key]));
const providerKey = (v) => JSON.stringify([v.localId, v.externalIdentity.providerKind,
    v.externalIdentity.resourceScope, v.externalIdentity.nativeId]);

function requireShape(ok, detail) {
    if (!ok) throw new Error(`Retained proposal review unavailable: ${detail}`);
}

function items(values) {
    requireShape(Array.isArray(values), "missing item context");
    for (const item of values) {
        requireShape(record(item) && id(item.id) && nonempty(item.role) &&
            ["title", "type", "state", "url"].every((key) => nullableText(item[key])) &&
            nullableId(item.parentId) && (item.revision === null || Number.isInteger(item.revision)), "invalid item context");
    }
}

function consequence(c, kind, provider) {
    requireShape(record(c) && EFFECTS[kind].includes(c.kind), "unsupported or contradictory effect");
    requireShape(["field", "to", "relation"].every((key) => nullableText(c[key])) &&
        nullableId(c.otherId), "missing exact effect value");
    if (c.kind === "field-set" || c.kind === "field-clear") {
        requireShape(c.otherProviderWorkItem == null && c.otherId === null &&
            nonempty(c.field) && (c.kind === "field-set" ? text(c.to) : c.to === null), "invalid field effect");
    }
    if (c.kind === "link-add" || c.kind === "link-remove") {
        requireShape(nonempty(c.relation), "missing link relation");
    }
    if (["link-add", "link-remove", "work-item-delete"].includes(c.kind)) {
        requireShape(provider
            ? c.otherId === null && providerReference(c.otherProviderWorkItem)
            : publishedId(c.otherId) && c.otherProviderWorkItem == null, "invalid effect endpoint");
    }
    if (c.kind === "seed-publish") requireShape(c.otherProviderWorkItem == null, "unsupported provider seed");
    if (c.before != null) {
        requireShape(record(c.before) && ["unknown", "absent", "value"].includes(c.before.state) &&
            nullableText(c.before.value) && nonempty(c.before.source) &&
            (c.before.revision === null || Number.isInteger(c.before.revision)) &&
            nullableText(c.before.reason), "invalid prior-value observation");
        requireShape(c.before.state === "value" ? text(c.before.value) : c.before.value === null,
            "contradictory prior-value state");
    }
    if (c.textChange != null) {
        requireShape(record(c.textChange) && nonempty(c.textChange.metric) && nonempty(c.textChange.unit) &&
            ["removed", "inserted"].every((key) => Number.isSafeInteger(c.textChange[key]) && c.textChange[key] >= 0),
        "invalid text-change evidence");
    }
}

export function retainProposalReview(rawJson, expected) {
    requireShape(record(expected) && nonempty(expected.captureId) &&
        expected.captureId === expected.expectedCaptureId && expected.expectedModelVersion === 1 &&
        digest(expected.digest) && workspace(expected.workspace) &&
        expected.sourceKind === "host-supplied-native-preview",
    "expected capture identity, model version, workspace, digest and source kind are required");
    requireShape(text(rawJson) && rawJson.length <= 4 * 1024 * 1024, "missing or oversized envelope");
    let envelope;
    try { envelope = JSON.parse(rawJson); }
    catch { throw new Error("Retained proposal review unavailable: malformed or truncated JSON"); }
    requireShape(record(envelope) && digest(envelope.digest) && envelope.digest === expected.digest &&
        typeof envelope.canApply === "boolean" && Array.isArray(envelope.operations) &&
        Array.isArray(envelope.issues) && Array.isArray(envelope.pendingChanges), "invalid native preview envelope");
    const model = envelope.reviewModel;
    requireShape(record(model) && model.model === "twig.change-proposal.review" && model.modelVersion === 1 &&
        model.digest === envelope.digest && sameWorkspace(model.workspace, expected.workspace),
    "unsupported model or reported identity mismatch");
    requireShape(nullableText(model.rationale) && Array.isArray(model.operations) &&
        model.operations.length === envelope.operations.length &&
        Array.isArray(model.authorizationChoices) && model.authorizationChoices.every(nonempty) &&
        Array.isArray(model.blockers), "missing review coverage");
    items(model.affectedItems);
    if (model.contextItems !== undefined) items(model.contextItems);
    if (model.recipe != null) requireShape(record(model.recipe) && nonempty(model.recipe.recipeId) &&
        Number.isSafeInteger(model.recipe.version) && model.recipe.version > 0, "invalid recipe context");
    const providerCoverage = new Map();
    if (model.providerAffectedItems !== undefined) {
        requireShape(Array.isArray(model.providerAffectedItems), "invalid provider affected items");
        for (const entry of model.providerAffectedItems) {
            requireShape(record(entry) && providerReference(entry.item) &&
                ["target", "peer"].includes(entry.role), "invalid provider affected item");
            const key = providerKey(entry.item);
            requireShape(!providerCoverage.has(key), "duplicate provider affected identity");
            providerCoverage.set(key, entry.role);
        }
    }
    const providerRoles = new Map();
    const markProvider = (item, role) => {
        const key = providerKey(item);
        if (role === "target" || !providerRoles.has(key)) providerRoles.set(key, role);
    };
    const seen = new Set();
    model.operations.forEach((op, ordinal) => {
        const wire = envelope.operations[ordinal];
        requireShape(record(op) && record(wire) && nonempty(op.opId) && !seen.has(op.opId) &&
            op.ordinal === ordinal && wire.ordinal === ordinal && wire.id === op.opId &&
            KINDS[wire.kind] === op.kind && Object.hasOwn(EFFECTS, op.kind), "operation order, identity or kind mismatch");
        seen.add(op.opId);
        requireShape(record(op.target) && nonempty(op.summary) && Array.isArray(op.preconditions) &&
            op.preconditions.length > 0 && Array.isArray(op.consequences) && op.consequences.length > 0,
        "operation lacks target, preconditions or effects");
        const provider = op.target.providerWorkItem != null;
        requireShape(provider
            ? op.kind !== "publish-seed" && providerReference(op.target.providerWorkItem) &&
                op.target.workItemId === null && op.target.stagedIdentity === null && op.target.seed == null
            : op.kind === "publish-seed"
                ? op.target.workItemId === null && nonempty(op.target.stagedIdentity)
                : publishedId(op.target.workItemId) && op.target.stagedIdentity === null, "invalid operation target");
        if (provider) markProvider(op.target.providerWorkItem, "target");
        if (op.kind !== "batch") requireShape(op.consequences.length === 1, "unexpected effect count");
        for (const pre of op.preconditions) {
            requireShape(record(pre) && pre.kind === (provider ? "expectedProviderRevision"
                : op.kind === "publish-seed" ? "expectedFingerprint" : "expectedRevision") &&
                nonempty(pre.value), "invalid native precondition");
        }
        for (const c of op.consequences) {
            consequence(c, op.kind, provider);
            if (provider && c.otherProviderWorkItem != null) markProvider(c.otherProviderWorkItem, "peer");
        }
        if (op.kind === "delete") requireShape(provider
            ? providerKey(op.consequences[0].otherProviderWorkItem) === providerKey(op.target.providerWorkItem)
            : op.consequences[0].otherId === op.target.workItemId, "deletion identity mismatch");
        if (op.capability != null) requireShape(record(op.capability) &&
            ["relationship", "declared", "availability", "reason"].every((key) => nonempty(op.capability[key])),
        "invalid capability advisory");
    });
    requireShape(providerRoles.size === providerCoverage.size &&
        [...providerRoles].every(([key, role]) => providerCoverage.get(key) === role),
    "provider affected coverage mismatch");
    for (const issue of envelope.issues) {
        requireShape(record(issue) && nonempty(issue.code) && text(issue.path) && text(issue.message), "invalid preview issue");
    }
    for (const blocker of model.blockers) {
        requireShape(record(blocker) && nonempty(blocker.kind) && nonempty(blocker.detail) &&
            nullableId(blocker.workItemId), "invalid blocker");
    }
    if (model.policyDecisions !== undefined) {
        requireShape(Array.isArray(model.policyDecisions), "invalid policy evidence");
        for (const decision of model.policyDecisions) {
            requireShape(record(decision) && ["ruleId", "kind", "outcome", "reason"].every((key) => nonempty(decision[key])) &&
                typeof decision.blocking === "boolean", "invalid policy evidence");
        }
    }
    // The full, detached envelope is retained, including additive fields and advisory choices.
    return freeze({
        captureId: expected.captureId, sourceKind: expected.sourceKind,
        provenance: "Reported by host; capture authenticity is not verified",
        envelope,
    });
}

function freeze(value, depth = 0) {
    requireShape(depth <= 64, "envelope nesting exceeds the supported bound");
    if (value && typeof value === "object") {
        for (const child of Object.values(value)) freeze(child, depth + 1);
        Object.freeze(value);
    }
    return value;
}

export function mountRetainedReview(root, rawJson, expected) {
    const doc = root.ownerDocument;
    const node = (tag, content) => {
        const element = doc.createElement(tag);
        if (content !== undefined) element.textContent = content;
        return element;
    };
    let retained;
    root.replaceChildren();
    try { retained = retainProposalReview(rawJson, expected); }
    catch (error) {
        const failure = node("p", error.message);
        failure.setAttribute("role", "alert");
        root.append(failure);
        return { available: false };
    }
    const model = retained.envelope.reviewModel;
    const heading = node("h1", "Retained proposal review - read only");
    const provenance = node("p", `${retained.provenance}. Capture: ${retained.captureId}; source: ${retained.sourceKind}.`);
    const identity = node("pre", `Reported workspace: ${model.workspace.organization} / ${model.workspace.project}\nReported digest: ${model.digest}`);
    const warning = node("p", "No acquisition, refresh, authorization or apply is available here. canApply and policy outcomes grant no authority. Preview acquisition can write the local journal.");
    const controls = node("div");
    const details = node("button", "Details");
    const back = node("button", "Back");
    details.type = back.type = "button";
    controls.append(details, back);
    const content = node("div");
    root.append(heading, provenance, identity, warning, controls, content);
    function render(full) {
        content.replaceChildren();
        details.disabled = full;
        back.disabled = !full;
        if (model.rationale) content.append(node("p", model.rationale));
        if (model.recipe != null) content.append(node("h2", "Recipe context (reported)"),
            node("pre", JSON.stringify(model.recipe, null, 2)));
        content.append(node("h2", "Reported authorization choices (evidence only)"),
            node("pre", JSON.stringify(model.authorizationChoices, null, 2)));
        content.append(node("h2", "Preview issues and blockers"));
        for (const issue of retained.envelope.issues) content.append(node("pre", JSON.stringify(issue, null, 2)));
        for (const blocker of model.blockers) content.append(node("pre", JSON.stringify(blocker, null, 2)));
        for (const decision of model.policyDecisions ?? []) content.append(node("pre", JSON.stringify(decision, null, 2)));
        content.append(node("h3", "Reported pending local changes"),
            node("pre", JSON.stringify(retained.envelope.pendingChanges, null, 2)));
        if (!retained.envelope.issues.length && !model.blockers.length) content.append(node("p", "No blockers reported in this retained observation; not authorization."));
        content.append(node("h2", "Affected items (reported context)"), node("pre", JSON.stringify(model.affectedItems, null, 2)));
        content.append(node("h2", "Provider-qualified affected items (reported context)"),
            node("pre", JSON.stringify(model.providerAffectedItems ?? [], null, 2)));
        content.append(node("h2", "Context only (not mutation targets)"), node("pre", JSON.stringify(model.contextItems ?? [], null, 2)));
        for (const op of model.operations) {
            const section = node("section");
            section.append(node("h2", `${op.ordinal + 1}. ${op.kind}: ${op.summary}`),
                node("pre", JSON.stringify(op.target, null, 2)));
            for (const c of op.consequences) {
                if ((c.kind === "field-set" || c.kind === "field-clear") && c.before == null)
                    section.append(node("p", `Before value for ${c.field}: unknown; no baseline observation supplied.`));
                const long = c.kind === "field-set" && c.field.toLowerCase() === "system.description" && c.to.length > 240;
                section.append(node("pre", JSON.stringify(!full && long
                    ? { ...c, to: "[Exact requested value retained; select Details]", textChange: c.textChange ?? null }
                    : c, null, 2)));
            }
            section.append(node("h3", "Native preconditions"), node("pre", JSON.stringify(op.preconditions, null, 2)));
            if (op.capability != null) section.append(node("h3", "Declared capability advisory (not observed availability)"),
                node("pre", JSON.stringify(op.capability, null, 2)));
            content.append(section);
        }
        if (full) {
            content.append(node("h2", "Complete retained envelope (inert evidence, not controls)"),
                node("pre", JSON.stringify(retained.envelope, null, 2)));
        }
    }
    details.addEventListener("click", () => render(true));
    back.addEventListener("click", () => render(false));
    render(false);
    return { available: true, retained };
}
