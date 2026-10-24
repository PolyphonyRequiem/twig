import { execFile as nodeExecFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";

export function createNativeBenchReader({ execFile = nodeExecFile, realpath = nodeRealpath } = {}) {
    return async ({ bench, workspacePath, expected, signal }) => {
        if (typeof bench !== "string" || !bench.trim() || bench.length > 512) {
            throw new Error("Enter a saved Bench name or decimal ID (at most 512 characters).");
        }
        if (typeof workspacePath !== "string" || !workspacePath.trim()) {
            throw new Error("The current session does not provide a Twig workspace path.");
        }
        const cwd = await realpath(workspacePath);
        const args = ["workspace", "--bench", bench, "--include-browser", "--browser-version", "2", "--view", "tree", "-o", "json"];
        if (expected) args.push("--expect-bench", expected.benchId,
            "--expect-binding", expected.origin.bindingId, "--expect-identity", expected.origin.identityId);
        const stdout = await new Promise((resolve, reject) => {
            execFile("twig", args, {
                cwd, encoding: "utf8", shell: false, windowsHide: true,
                timeout: 15_000, maxBuffer: 4 * 1024 * 1024, signal,
            }, (error, output, stderr) => {
                if (error) reject(new Error(String(stderr || "").trim() || `Twig Bench read failed: ${error.message}`, { cause: error }));
                else resolve(output);
            });
        });
        let value;
        try { value = JSON.parse(stdout)?.browser; }
        catch (error) { throw new Error("Twig returned invalid Bench JSON.", { cause: error }); }
        validateBench(value, bench, cwd);
        if (expected && (value.benchId !== expected.benchId ||
            Object.keys(expected.origin).some((key) => value.origin[key] !== expected.origin[key]))) {
            throw new Error("Twig Bench origin or captured target changed; explicitly load a new observation.");
        }
        return value;
    };
}

function validateBench(value, requested, cwd) {
    if (!record(value) || value.version !== 2 || value.observation !== "raw-mirror") {
        throw new Error("Unsupported native Bench document; browser version 2 raw-mirror is required.");
    }
    const origin = value.origin;
    if (value.requestedBench !== requested || !nonempty(value.benchId) || !nonempty(value.benchName) ||
        !record(origin) || !["connectionRef", "organization", "project", "bindingId", "identityId",
            "worktreeRoot", "worktreeFingerprint", "selectionSource"].every((key) => nonempty(origin[key])) ||
        typeof origin.team !== "string" ||
        !["bindingRevision", "attachmentRevision", "selectionRevision"].every((key) => count(origin[key])) ||
        !(origin.storageGeneration == null || typeof origin.storageGeneration === "string") ||
        !samePath(origin.worktreeRoot, cwd)) {
        throw new Error("Native Bench target or qualified checkout evidence is invalid.");
    }
    const configuration = value.configuration;
    if (!Array.isArray(value.members) || !record(configuration) || configuration.version !== 1 ||
        !nonempty(configuration.settingsDigest) || typeof configuration.automaticEnabled !== "boolean" ||
        typeof configuration.assigneeSummary !== "string" ||
        !Array.isArray(configuration.areas) || !configuration.areas.every((area) =>
            record(area) && typeof area.path === "string" && typeof area.includeChildren === "boolean") ||
        !Array.isArray(configuration.sprints) || !configuration.sprints.every((sprint) =>
            record(sprint) && typeof sprint.expression === "string") ||
        !Array.isArray(configuration.pins) || !configuration.pins.every((pin) =>
            record(pin) && id(pin.id) && ["single", "tree"].includes(pin.mode) && typeof pin.cached === "boolean")) {
        throw new Error("Native Bench membership/configuration is incomplete.");
    }
    const members = new Map();
    for (const member of value.members) {
        if (!record(member) || !id(member.id) || members.has(member.id) ||
            !["loaded", "selected", "seed", "pending"].every((key) => typeof member[key] === "boolean") ||
            (member.loaded ? !["title", "type", "state"].every((key) => typeof member[key] === "string")
                : ["title", "type", "state", "parentId"].some((key) => member[key] != null)) ||
            !(member.parentId == null || id(member.parentId))) {
            throw new Error("Native Bench member values are invalid or duplicated.");
        }
        members.set(member.id, member);
    }
    const emitted = new Set();
    function nodes(list, depth = 0) {
        if (!Array.isArray(list) || depth > 256) throw new Error("Native Bench hierarchy is invalid.");
        for (const node of list) {
            if (!record(node) || !id(node.id) || emitted.has(node.id) || node.key !== `item:${node.id}` ||
                !["title", "type", "state", "membership"].every((key) => typeof node[key] === "string") ||
                typeof node.isSeed !== "boolean" || !Array.isArray(node.pins) ||
                !node.pins.every((pin) => ["single", "tree"].includes(pin)) ||
                !Array.isArray(node.owningSubtreeIds) || !node.owningSubtreeIds.every(id)) {
                throw new Error("Native Bench hierarchy values are invalid or duplicated.");
            }
            const member = members.get(node.id);
            if (member && (!member.loaded || node.title !== member.title || node.type !== member.type || node.state !== member.state)) {
                throw new Error("Native Bench raw member and hierarchy disagree.");
            }
            emitted.add(node.id);
            nodes(node.children, depth + 1);
        }
    }
    nodes(value.roots);
    const c = value.completeness;
    const loaded = value.members.filter((member) => member.loaded).length;
    const arrays = ["omittedLoadedMemberIds", "missingAncestorIds", "prunedAncestorIds", "depthOmittedIds"];
    if (!record(c) || c.knownMemberCount !== members.size || c.loadedMemberCount !== loaded ||
        c.unloadedKnownMemberCount !== members.size - loaded || c.renderedNodeCount !== emitted.size ||
        c.sourceMemberCount !== null || c.unknownMemberCount !== null || c.sourceComplete !== false ||
        typeof c.projectionComplete !== "boolean" || !count(c.depthDown) || !count(c.depthUp) ||
        !arrays.every((key) => Array.isArray(c[key]) && c[key].every(id) && new Set(c[key]).size === c[key].length) ||
        !Array.isArray(c.reasons) || !c.reasons.every(nonempty) ||
        !["cache-only", "source-total-unknown"].every((reason) => c.reasons.includes(reason))) {
        throw new Error("Native Bench completeness evidence is missing or contradictory.");
    }
    const omitted = value.members.filter((member) => member.loaded && !emitted.has(member.id)).map((member) => member.id);
    if (omitted.length !== c.omittedLoadedMemberIds.length || omitted.some((key) => !c.omittedLoadedMemberIds.includes(key)) ||
        (c.projectionComplete && (loaded !== members.size || arrays.some((key) => c[key].length) || c.reasons.includes("cached-parent-cycle")))) {
        throw new Error("Native Bench projection completeness contradicts its rows.");
    }
}

export function captureBench(value) {
    return { benchId: value.benchId, origin: { ...value.origin } };
}

function samePath(a, b) {
    const normalize = (value) => process.platform === "win32" ? path.resolve(value).toLowerCase() : path.resolve(value);
    return normalize(a) === normalize(b);
}
function record(value) { return value !== null && typeof value === "object" && !Array.isArray(value); }
function nonempty(value) { return typeof value === "string" && value.trim().length > 0; }
function count(value) { return Number.isSafeInteger(value) && value >= 0; }
function id(value) { return Number.isInteger(value) && value !== 0 && Math.abs(value) <= 2147483647; }
