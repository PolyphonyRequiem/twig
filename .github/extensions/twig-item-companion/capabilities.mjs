// Capability admission is advisory compatibility checking only. It grants no authority:
// every native payload is still validated by its own adapter and guarded by the CLI.
const capability = (command, flags) => Object.freeze({ command: Object.freeze(command), flags: Object.freeze(flags) });

const detailFlags = ["--document", "-o", "--expect-bench", "--expect-binding", "--expect-identity"];

export const CAPABILITIES = Object.freeze({
    "detail": capability(["bench", "detail"], detailFlags),
    "bench-detail": capability(["bench", "detail"], ["--bench", ...detailFlags]),
    "draft-show": capability(["draft", "show"], ["--document", "-o"]),
    "workspace-browser": capability(["workspace"], ["--bench", "--include-browser", "--browser-version", "--view", "-o",
        "--expect-bench", "--expect-binding", "--expect-identity"]),
    "discussion": capability(["bench", "discussion"], ["--stdin", "-o"]),
    "draft-open": capability(["draft", "open"], ["--bench", "-o", "--expect-binding", "--expect-identity"]),
    "draft-stage": capability(["draft", "stage"], ["--stdin", "-o"]),
    "draft-retain": capability(["draft", "retain"], ["--stdin", "-o"]),
    "proposal-preview": capability(["proposal", "preview"], ["--file", "--expect-digest", "-o"]),
});

export const CANVAS_CAPABILITIES = Object.freeze({
    item: Object.freeze(["detail", "draft-show"]),
    workbench: Object.freeze(["workspace-browser", "bench-detail", "discussion", "draft-open", "draft-stage", "draft-retain"]),
    review: Object.freeze(["proposal-preview"]),
});

// Requires a generated leaf usage line naming the exact command, plus every flag as an exact
// option token inside the Options section. Group help, flag prefixes and prose mentions fail.
export function helpSatisfies(text, { command, flags }) {
    const name = command.join(" ");
    const lines = String(text).split(/\r?\n/);
    const usage = lines.some((line) => line.startsWith("Usage: ") && line.slice(7).split("|")
        .some((alternative) => alternative === name || alternative.startsWith(name + " ")));
    const tokens = new Set();
    let inOptions = false;
    for (const line of lines) {
        if (/^\S/.test(line)) { inOptions = line.trim() === "Options:"; continue; }
        if (!inOptions || !/^ {2}-/.test(line)) continue;
        const declaration = line.slice(2).split(/ {2,}/, 1)[0];
        for (const token of declaration.split(/[\s,|]+/)) {
            if (token && !/^[<[]/.test(token)) tokens.add(token);
        }
    }
    const missing = usage ? flags.filter((flag) => !tokens.has(flag)) : [`usage '${name}'`];
    return { ok: missing.length === 0, missing };
}
