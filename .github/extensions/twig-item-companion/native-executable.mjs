import { execFile as nodeExecFile } from "node:child_process";
import { createHash } from "node:crypto";
import { constants, createReadStream } from "node:fs";
import { access, realpath, stat } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { CANVAS_CAPABILITIES, CAPABILITIES, helpSatisfies } from "./capabilities.mjs";

const PROBE_TIMEOUT_MS = 5000;
const PROBE_MAX_BYTES = 64 * 1024;
const PROBE_BUDGET_MS = 15000;

// Process-wide: admitted capabilities per exact real file and content hash, never per PATH label.
const admitted = new Map();
// Lookup outcomes that only mean "no usable twig here". Anything else is a real I/O fault and is
// reported instead of silently falling through to a later PATH entry.
const SKIPPED_LOOKUP_CODES = new Set(["ENOENT", "ENOTDIR", "EACCES", "EPERM", "ELOOP", "ENAMETOOLONG"]);

export class ExecutableError extends Error {
    constructor(code, message) {
        super(message);
        this.name = "ExecutableError";
        this.code = code;
    }
}

// Resolves `twig` without a shell. Only absolute PATH entries are searched, so the current
// directory and relative entries can never supply the executable. On Windows only .exe is
// accepted: script shims cannot be launched with shell:false.
export async function resolveTwigExecutable({ env = process.env, platform = process.platform, fs = { stat, access, realpath } } = {}) {
    const raw = (platform === "win32" ? env.Path ?? env.PATH : env.PATH ?? env.Path) ?? "";
    const names = platform === "win32" ? ["twig.exe"] : ["twig"];
    for (const entry of raw.split(platform === "win32" ? ";" : ":")) {
        if (!entry || !path.isAbsolute(entry)) continue;
        for (const name of names) {
            const candidate = path.join(entry, name);
            try {
                const info = await fs.stat(candidate);
                if (!info.isFile()) continue;
                if (platform !== "win32") await fs.access(candidate, constants.X_OK);
                return await fs.realpath(candidate);
            } catch (error) {
                if (SKIPPED_LOOKUP_CODES.has(error?.code)) continue;
                throw new ExecutableError("twig_executable_unavailable",
                    `The PATH candidate ${candidate} could not be checked (${typeof error?.code === "string" ? error.code : "unknown error"}). Resolve it, then reopen the canvas.`);
            }
        }
    }
    throw new ExecutableError("twig_executable_unavailable",
        "No twig executable was found on an absolute PATH entry. Install Twig, then reopen the canvas.");
}

// Adapters require an admitted executable handle and never fall back to a PATH-resolved name.
export function requireAdmitted(executable) {
    if (typeof executable?.prepare !== "function") {
        throw new ExecutableError("twig_executable_unavailable", "This reader was created without an admitted twig executable.");
    }
    return executable;
}

export function hashFile(file) {
    return new Promise((resolve, reject) => {
        const hash = createHash("sha256");
        createReadStream(file).on("error", reject).on("data", (chunk) => hash.update(chunk))
            .on("end", () => resolve(hash.digest("hex")));
    });
}

// Pins one canvas instance to the executable's real path and content hash. prepare() is a
// best-effort check run immediately before each spawn; it is not an OS-atomic attestation,
// since the file can still change between the final hash and process creation.
export async function admitTwigExecutable({ canvas, env, platform, execFile = nodeExecFile, now = Date.now } = {}) {
    const allowed = CANVAS_CAPABILITIES[canvas];
    if (!allowed) throw new ExecutableError("twig_capability_unlisted", `Unknown canvas '${canvas}'.`);
    const file = await resolveTwigExecutable({ env, platform });
    const sha256 = await hashFile(file).catch(() => {
        throw new ExecutableError("twig_executable_unavailable", `The twig executable at ${file} could not be read.`);
    });
    const label = `${file} (sha256 ${sha256.slice(0, 12)})`;
    const refused = new Map();
    let changed = false;
    let spentMs = 0;

    async function verify() {
        if (changed) throw changedError();
        const current = await hashFile(file).catch(() => null);
        if (current !== sha256) {
            changed = true;
            throw changedError();
        }
    }

    function changedError() {
        return new ExecutableError("executable_changed",
            `The twig executable at ${file} changed after this canvas opened. Reopen the canvas to use the new executable.`);
    }

    async function probe(id, signal) {
        const capability = CAPABILITIES[id];
        const remaining = PROBE_BUDGET_MS - spentMs;
        if (remaining <= 0) return `the help probe budget for this canvas was exhausted`;
        const started = now();
        const outcome = await new Promise((resolve) => {
            // The timeout ends only the direct child process. Help output from an older binary may
            // still run its startup work; the bound limits duration, not side effects.
            execFile(file, [...capability.command, "--help"], {
                cwd: os.tmpdir(), timeout: Math.min(PROBE_TIMEOUT_MS, remaining), maxBuffer: PROBE_MAX_BYTES,
                shell: false, windowsHide: true, encoding: "utf8", signal,
            }, (error, stdout) => resolve({ error, stdout }));
        });
        spentMs += Math.max(0, now() - started);
        const command = `twig ${capability.command.join(" ")} --help`;
        if (outcome.error?.name === "AbortError") throw outcome.error;
        if (outcome.error?.code === "ERR_CHILD_PROCESS_STDIO_MAXBUFFER") return `${command} exceeded ${PROBE_MAX_BYTES} bytes`;
        if (outcome.error?.killed || outcome.error?.signal) return `${command} did not finish within its time bound`;
        if (outcome.error) return `${command} exited unsuccessfully`;
        const result = helpSatisfies(outcome.stdout, capability);
        return result.ok ? null : `${command} does not offer ${result.missing.join(", ")}`;
    }

    async function prepare(id, { signal } = {}) {
        if (!allowed.includes(id)) {
            throw new ExecutableError("twig_capability_unlisted", `The ${canvas} canvas does not use '${id}'.`);
        }
        await verify();
        const key = `${file}\0${sha256}`;
        if (!admitted.get(key)?.has(id)) {
            if (refused.has(id)) throw missing(refused.get(id));
            const reason = await probe(id, signal);
            if (reason) {
                refused.set(id, reason);
                throw missing(reason);
            }
            if (!admitted.has(key)) admitted.set(key, new Set());
            admitted.get(key).add(id);
            await verify();
        }
        return file;
    }

    function missing(reason) {
        return new ExecutableError("twig_capability_missing",
            `The twig executable at ${label} is not compatible with this canvas: ${reason}. Update Twig, then reopen the canvas.`);
    }

    return Object.freeze({ path: file, sha256, canvas, prepare });
}
