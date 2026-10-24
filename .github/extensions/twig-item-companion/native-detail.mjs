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

        const { stdout, stderr } = await execute(execFile, args, {
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
                observation.identityId !== expected.identityId)) {
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
