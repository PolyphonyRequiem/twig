import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, readdir, rm, stat, utimes, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { CAPABILITIES, CANVAS_CAPABILITIES, helpSatisfies } from "../capabilities.mjs";
import { admitTwigExecutable, resolveTwigExecutable } from "../native-executable.mjs";

const win = process.platform === "win32";
const exeName = win ? "twig.exe" : "twig";

async function fixture() {
    const root = await mkdtemp(path.join(os.tmpdir(), "twig-exec-test-"));
    const bin = path.join(root, "bin");
    await mkdir(bin);
    const file = path.join(bin, exeName);
    await writeFile(file, "fake-twig-binary-v1", { mode: 0o755 });
    const pinned = new Date(Math.floor(Date.now() / 1000) * 1000 - 60000);
    await utimes(file, pinned, pinned);
    return { root, bin, file, env: { PATH: bin, Path: bin, PATHEXT: ".COM;.EXE;.BAT;.CMD" } };
}

function leafHelp(command, flags, extra = "") {
    const rows = flags.map((flag) => flag === "-o" ? "  -o, --output <string>    Output format." : `  ${flag} <string>    Description.`);
    return `Usage: ${command} [options...] [-h|--help] [--version]\n\nOptions:\n${rows.join("\n")}\n${extra}`;
}

function prober(responses, calls = []) {
    const execFile = (file, args, options, callback) => {
        calls.push({ file, args, options });
        const key = args.slice(0, -1).join(" ");
        const response = responses[key] ?? { error: Object.assign(new Error("exit 1"), { code: 1 }), stdout: "", stderr: "Unknown command" };
        queueMicrotask(() => callback(response.error ?? null, response.stdout ?? "", response.stderr ?? ""));
        return {};
    };
    return { execFile, calls };
}

function satisfied(...ids) {
    const responses = {};
    for (const id of ids) {
        const capability = CAPABILITIES[id];
        const command = capability.command.join(" ");
        responses[command] = { stdout: leafHelp(command, capability.flags) };
    }
    return responses;
}

test("resolves only an absolute real executable from absolute PATH entries, never cwd or relative entries", async () => {
    const f = await fixture();
    try {
        const relative = path.relative(process.cwd(), f.bin);
        const env = { PATH: ["", relative, "relative-bin", f.bin].join(path.delimiter), PATHEXT: ".CMD;.EXE" };
        const resolved = await resolveTwigExecutable({ env });
        assert.ok(path.isAbsolute(resolved));
        assert.equal(resolved.toLowerCase(), (await realpathOf(f.file)).toLowerCase());

        await rm(f.file);
        if (win) await writeFile(path.join(f.bin, "twig.cmd"), "@echo off");
        await assert.rejects(resolveTwigExecutable({ env: f.env }), { code: "twig_executable_unavailable" });
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("admission hashes the real executable without spawning anything", async () => {
    const f = await fixture();
    try {
        const { execFile, calls } = prober({});
        const handle = await admitTwigExecutable({ canvas: "item", env: f.env, execFile });
        assert.match(handle.sha256, /^[a-f0-9]{64}$/);
        assert.ok(path.isAbsolute(handle.path));
        assert.equal(calls.length, 0);
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("each prepare re-hashes immediately before spawn; same-size bytes with restored mtime are refused", async () => {
    const f = await fixture();
    try {
        const { execFile, calls } = prober(satisfied("detail"));
        const handle = await admitTwigExecutable({ canvas: "item", env: f.env, execFile });
        assert.equal(await handle.prepare("detail"), handle.path);
        assert.equal(await handle.prepare("detail"), handle.path);
        const before = await stat(f.file);
        await writeFile(f.file, "fake-twig-binary-v2");
        await utimes(f.file, before.atime, before.mtime);
        const after = await stat(f.file);
        assert.equal(after.size, before.size);
        assert.equal(after.mtimeMs, before.mtimeMs);
        const probes = calls.length;
        await assert.rejects(handle.prepare("detail"), { code: "executable_changed" });
        await assert.rejects(handle.prepare("detail"), { code: "executable_changed" });
        assert.equal(calls.length, probes, "a changed executable is never probed or re-resolved");
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("PATH search skips only expected lookup errors and refuses an unexpected I/O failure instead of falling through", async () => {
    const f = await fixture();
    try {
        const broken = path.join(f.root, "broken");
        const env = { PATH: [broken, path.join(f.root, "absent"), f.bin].join(path.delimiter), PATHEXT: ".EXE" };
        env.Path = env.PATH;
        const io = Object.assign(new Error("PRIVATE device detail"), { code: "EIO" });
        const fs = {
            stat: async (candidate) => { if (candidate.startsWith(broken)) throw io; return stat(candidate); },
            access: async () => {}, realpath: realpathOf,
        };
        await assert.rejects(resolveTwigExecutable({ env, fs }), (error) => {
            assert.equal(error.code, "twig_executable_unavailable");
            assert.ok(error.message.includes(path.join(broken, exeName)), error.message);
            assert.match(error.message, /\(EIO\)/);
            assert.doesNotMatch(error.message, /PRIVATE/);
            return true;
        });
        const skipping = { ...fs, stat: async (candidate) => {
            if (candidate.startsWith(broken)) throw Object.assign(new Error("denied"), { code: "EACCES" });
            return stat(candidate);
        } };
        assert.equal(await resolveTwigExecutable({ env, fs: skipping }), await realpathOf(f.file));
    } finally { await rm(f.root, { recursive: true, force: true }); }
});
test("capability cache is keyed by real path and content hash, not the PATH label", async () => {
    const f = await fixture();
    try {
        const { execFile, calls } = prober(satisfied("detail"));
        const first = await admitTwigExecutable({ canvas: "item", env: f.env, execFile });
        await first.prepare("detail");
        assert.equal(calls.length, 1);
        const otherLabel = { ...f.env, PATH: [path.join(f.root, "missing"), f.bin].join(path.delimiter) };
        otherLabel.Path = otherLabel.PATH;
        const second = await admitTwigExecutable({ canvas: "item", env: otherLabel, execFile });
        await second.prepare("detail");
        assert.equal(calls.length, 1, "same real file and hash reuses the admitted capability");

        await writeFile(f.file, "fake-twig-binary-v3-different");
        const third = await admitTwigExecutable({ canvas: "item", env: f.env, execFile });
        await third.prepare("detail");
        assert.equal(calls.length, 2, "new content must be probed again");
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("probes only the requested capability, lazily, with bounded absolute help invocation", async () => {
    const f = await fixture();
    try {
        const { execFile, calls } = prober(satisfied("detail", "draft-show"));
        const handle = await admitTwigExecutable({ canvas: "item", env: f.env, execFile });
        await handle.prepare("detail");
        assert.equal(calls.length, 1);
        assert.equal(calls[0].file, handle.path);
        assert.deepEqual(calls[0].args, ["bench", "detail", "--help"]);
        assert.equal(calls[0].options.shell, false);
        assert.equal(calls[0].options.windowsHide, true);
        assert.equal(calls[0].options.timeout, 5000);
        assert.equal(calls[0].options.maxBuffer, 64 * 1024);
        await assert.rejects(handle.prepare("discussion"), { code: "twig_capability_unlisted" });
        await assert.rejects(handle.prepare("proposal-preview"), { code: "twig_capability_unlisted" });
        assert.equal(calls.length, 1);
        await handle.prepare("draft-show");
        assert.deepEqual(calls[1].args, ["draft", "show", "--help"]);
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("canvas manifests list only the capabilities each canvas uses", () => {
    assert.deepEqual([...CANVAS_CAPABILITIES.item], ["detail", "draft-show"]);
    assert.deepEqual([...CANVAS_CAPABILITIES.review], ["proposal-preview"]);
    assert.deepEqual([...CANVAS_CAPABILITIES.workbench],
        ["workspace-browser", "bench-detail", "discussion", "draft-open", "draft-stage", "draft-retain"]);
});

test("help matching requires the exact command usage and exact flag tokens", () => {
    const cap = CAPABILITIES["detail"];
    assert.equal(helpSatisfies(leafHelp("bench detail", cap.flags), cap).ok, true);
    assert.equal(helpSatisfies(leafHelp("bench detail|bd", cap.flags), cap).ok, true);
    const prefixed = leafHelp("bench detail", cap.flags.map((flag) => flag === "--document" ? "--documents" : flag));
    assert.equal(helpSatisfies(prefixed, cap).ok, false);
    assert.equal(helpSatisfies(leafHelp("bench detail", cap.flags.filter((flag) => flag !== "--expect-identity")), cap).ok, false);
    assert.equal(helpSatisfies("Usage: twig bench <command> --help\n\nCommands in 'bench':\n  bench detail  --document -o --expect-bench --expect-binding --expect-identity\n", cap).ok, false);
    assert.equal(helpSatisfies(leafHelp("bench discussion", cap.flags), cap).ok, false);
    assert.equal(helpSatisfies(leafHelp("bench detailed", cap.flags), cap).ok, false);
    const described = `Usage: bench detail [options...]\n\nOptions:\n  -o, --output <string>    Output.\n  --x <string>    mentions --document --expect-bench --expect-binding --expect-identity\n`;
    assert.equal(helpSatisfies(described, cap).ok, false, "flags mentioned only in descriptions do not count");
    const example = leafHelp("bench detail", ["-o"], "\nExamples:\n  --document --expect-bench --expect-binding --expect-identity\n");
    assert.equal(helpSatisfies(example, cap).ok, false, "flags outside the Options section do not count");
    assert.match(helpSatisfies(prefixed, cap).missing.join(","), /--document/);
});

test("probe failures are named, latched for the handle and never retried automatically", async () => {
    const f = await fixture();
    try {
        for (const [response, pattern] of [
            [{ error: Object.assign(new Error("exit 1"), { code: 1 }), stdout: leafHelp("bench detail", CAPABILITIES.detail.flags) }, /exited unsuccessfully/],
            [{ error: Object.assign(new Error("timed out"), { killed: true, signal: "SIGTERM" }), stdout: "" }, /did not finish/],
            [{ error: Object.assign(new Error("maxBuffer"), { code: "ERR_CHILD_PROCESS_STDIO_MAXBUFFER" }), stdout: "" }, /exceeded/],
            [{ stdout: "Usage: twig bench <command> --help\n" }, /usage 'bench detail'/],
        ]) {
            const { execFile, calls } = prober({ "bench detail": response });
            const handle = await admitTwigExecutable({ canvas: "item", env: f.env, execFile });
            const failure = await handle.prepare("detail").catch((error) => error);
            assert.equal(failure.code, "twig_capability_missing");
            assert.match(failure.message, pattern);
            assert.match(failure.message, new RegExp(handle.sha256.slice(0, 12)));
            await assert.rejects(handle.prepare("detail"), { code: "twig_capability_missing" });
            assert.equal(calls.length, 1, "a refused capability is latched until the canvas is reopened");
        }
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("probe time is bounded in total across one canvas handle", async () => {
    const f = await fixture();
    try {
        let clock = 0;
        const calls = [];
        const execFile = (file, args, options, callback) => {
            calls.push(options.timeout);
            clock += 8000;
            const flags = [...new Set(Object.values(CAPABILITIES).flatMap((value) => value.flags))];
            queueMicrotask(() => callback(null, leafHelp(args.slice(0, -1).join(" "), flags), ""));
            return {};
        };
        const handle = await admitTwigExecutable({ canvas: "workbench", env: f.env, execFile, now: () => clock });
        await handle.prepare("workspace-browser");
        await handle.prepare("bench-detail");
        await assert.rejects(handle.prepare("discussion"), (error) =>
            error.code === "twig_capability_missing" && /budget/.test(error.message));
        assert.ok(calls.every((timeout) => timeout <= 5000));
        assert.equal(calls.length, 2);
    } finally { await rm(f.root, { recursive: true, force: true }); }
});

async function realpathOf(file) {
    const { realpath } = await import("node:fs/promises");
    return realpath(file);
}
