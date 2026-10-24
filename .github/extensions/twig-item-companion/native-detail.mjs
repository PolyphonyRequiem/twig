import { execFile as nodeExecFile } from "node:child_process";
import { realpath as nodeRealpath } from "node:fs/promises";
import path from "node:path";

const CLI_TIMEOUT_MS = 15_000;
const MAX_OUTPUT_BYTES = 4 * 1024 * 1024;
const EXECUTABLE = "twig";

export function createNativeDetailReader({
    execFile = nodeExecFile,
    realpath = nodeRealpath,
} = {}) {
    return async function readNativeDetail({ workItemId, workspacePath, expected, signal }) {
        if (!Number.isInteger(workItemId) || workItemId === 0 || Math.abs(workItemId) > 2_147_483_647) {
            throw new Error("Work item ID must be a non-zero 32-bit integer.");
        }
        if (typeof workspacePath !== "string" || workspacePath.trim().length === 0) {
            throw new Error("The current session does not provide a Twig workspace path.");
        }

        const cwd = await realpath(workspacePath);
        const args = ["bench", "detail", String(workItemId), "--document", "-o", "json"];
        if (expected) {
            args.push("--expect-bench", expected.benchId);
            args.push("--expect-binding", expected.bindingId);
            args.push("--expect-identity", expected.identityId);
        }

        const { stdout } = await execute(execFile, args, {
            cwd,
            encoding: "utf8",
            maxBuffer: MAX_OUTPUT_BYTES,
            shell: false,
            signal,
            timeout: CLI_TIMEOUT_MS,
            windowsHide: true,
        });

        let observation;
        try {
            observation = JSON.parse(stdout);
        } catch (error) {
            throw new Error("Twig returned invalid semantic detail JSON.", { cause: error });
        }

        validateObservation(observation, workItemId, cwd);
        if (expected &&
            (observation.benchId !== expected.benchId ||
                observation.itemKey.bindingId !== expected.bindingId ||
                observation.identityId !== expected.identityId ||
                (expected.connectionId !== undefined && observation.itemKey.connectionId !== expected.connectionId))) {
            throw new Error("Twig detail no longer matches the captured Bench, binding, and identity.");
        }

        return observation;
    };
}

function execute(execFile, args, options) {
    return new Promise((resolve, reject) => {
        execFile(EXECUTABLE, args, options, (error, stdout, stderr) => {
            if (error) {
                const detail = typeof stderr === "string" ? stderr.trim() : "";
                reject(new Error(detail || `Twig detail command failed: ${error.message}`, { cause: error }));
                return;
            }

            resolve({
                stdout: typeof stdout === "string" ? stdout : "",
                stderr: typeof stderr === "string" ? stderr : "",
            });
        });
    });
}

function validateObservation(value, workItemId, workspacePath) {
    if (!isRecord(value) ||
        value.version !== 2 ||
        !isRecord(value.itemKey) ||
        !isRecord(value.item) ||
        !isRecord(value.document)) {
        throw new Error("Twig returned an unsupported semantic detail document; expected version 2.");
    }
    if (value.itemKey.workItemId !== workItemId) {
        throw new Error("Twig returned detail for a different work item.");
    }
    if (typeof value.itemKey.checkoutRoot !== "string" ||
        !samePath(value.itemKey.checkoutRoot, workspacePath)) {
        throw new Error("Twig resolved a different checkout than the current session workspace.");
    }
    if (value.editing !== "unavailable" ||
        value.relatedItemsCoverage !== "not-exported" ||
        value.iconAssetAuthority !== "unavailable") {
        throw new Error("Twig returned an unexpected semantic detail capability contract.");
    }

    const { itemKey, item, document } = value;
    if (![itemKey.connectionId, itemKey.bindingId, value.benchId, value.benchName, value.identityId].every(nonempty) ||
        item.id !== workItemId || document.workItemId !== workItemId ||
        !Number.isInteger(item.revision) || item.revision < 0 || document.revision !== item.revision ||
        ![value.bindingRevision, value.selectionRevision].every((n) => Number.isInteger(n) && n >= 0) ||
        typeof value.hasLocalChanges !== "boolean" || typeof item.isDirty !== "boolean" ||
        typeof item.isSeed !== "boolean" ||
        ![item.title, item.state, item.typeName].every((v) => typeof v === "string") ||
        !isRecord(item.fields) || !Object.values(item.fields).every(nullableString) ||
        ![document.workItemTypeReferenceName, document.processId].every(nonempty) ||
        !validLayout(value) || !validDocument(document) ||
        !validAppearance(value)) {
        throw new Error("Twig returned incomplete or contradictory semantic detail version 2.");
    }
}

function validLayout(value) {
    if (value.layoutStatus === "served") return value.layoutSource === "server";
    return ["locked", "unavailable"].includes(value.layoutStatus) &&
        value.layoutSource === "twig-fallback" &&
        value.document.processId === "twig.fallback" &&
        (value.layoutStatus !== "locked" || nonempty(value.lockedTypeReferenceName));
}

function validDocument(document) {
    return Array.isArray(document.systemControls) && document.systemControls.every(validControl) &&
        Array.isArray(document.pages) && document.pages.every((page) =>
            validLayoutNode(page) && typeof page.pageType === "string" &&
            Array.isArray(page.sections) && page.sections.every((section) =>
                isRecord(section) && typeof section.id === "string" &&
                Array.isArray(section.groups) && section.groups.every((group) =>
                    validLayoutNode(group) && Array.isArray(group.controls) && group.controls.every(validControl))));
}

function validLayoutNode(value) {
    return isRecord(value) && typeof value.id === "string" && typeof value.label === "string" &&
        typeof value.visible === "boolean" && typeof value.isContribution === "boolean";
}

function validControl(control) {
    if (!validLayoutNode(control) || typeof control.readOnly !== "boolean" ||
        typeof control.controlType !== "string") return false;
    if (control.isContribution) return control.value == null;
    const value = control.value;
    return isRecord(value) && [0, 1, 2].includes(value.state) &&
        (value.state === 0 ? typeof value.full === "string" : value.full == null) &&
        nullableString(value.short);
}

function validAppearance(value) {
    if (value.appearance == null) return value.appearanceSource === "unavailable";
    return value.appearanceSource === "cached-process-metadata" && isRecord(value.appearance) &&
        nonempty(value.appearance.name) && nullableString(value.appearance.color) && nullableString(value.appearance.iconId);
}

function nonempty(value) {
    return typeof value === "string" && value.trim().length > 0;
}

function nullableString(value) {
    return value == null || typeof value === "string";
}

function samePath(left, right) {
    const normalizedLeft = path.resolve(left);
    const normalizedRight = path.resolve(right);
    return process.platform === "win32"
        ? normalizedLeft.toLocaleLowerCase("en-US") === normalizedRight.toLocaleLowerCase("en-US")
        : normalizedLeft === normalizedRight;
}

function isRecord(value) {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}
