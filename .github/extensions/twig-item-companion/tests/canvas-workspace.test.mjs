import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { fakeExecutable } from "./executable-fixture.mjs";

const sourceUrl = new URL("../extension.mjs", import.meta.url);
const source = await readFile(sourceUrl, "utf8");
const surfaces = [
    { id: "twig-item-companion", action: "load_detail", input: { workItemId: 42 } },
    { id: "twig-workbench", action: "load_bench", input: { bench: "default" } },
    { id: "twig-proposal-review", action: "acquire_review",
        input: { file: "proposal.json", digest: "a".repeat(64), workspace: { organization: "example", project: "example" } } },
];

async function harness(t) {
    const root = await mkdtemp(path.join(tmpdir(), "twig-canvas-workspace-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const checkout = path.join(root, "checkout");
    const other = path.join(root, "other");
    const sessionState = path.join(root, "session-state");
    await Promise.all([checkout, other, sessionState].map(value => mkdir(value)));
    await Promise.all([checkout, sessionState].map(value => writeFile(path.join(value, "proposal.json"), "{}")));
    const calls = [];
    const admissions = [];
    let declarations;
    class CanvasError extends Error {
        constructor(code, message) { super(message); this.code = code; }
    }
    const bindings = {
        Error, process, path, CanvasError, createCanvas: value => value,
        joinSession: async ({ canvases }) => {
            declarations = canvases;
            return { workspacePath: sessionState, sessionId: "runtime-session" };
        },
    };
    for (const match of source.matchAll(/^import \{ (.+) \} from "(.+)";$/gm)) {
        if (match[2] === "@github/copilot-sdk/extension") continue;
        const module = await import(new URL(match[2], sourceUrl));
        for (const symbol of match[1].split(", ")) {
            const [exported, local = exported] = symbol.split(" as ");
            bindings[local] = module[exported];
        }
    }
    bindings.admitTwigExecutable = async ({ canvas }) => {
        admissions.push(canvas);
        return fakeExecutable();
    };
    for (const name of ["createNativeDetailReader", "createNativeBenchReader", "createNativeProposalReader"]) {
        const factory = bindings[name];
        bindings[name] = options => factory({ ...options, execFile(file, args, options, callback) {
            calls.push({ file, args, cwd: options.cwd });
            callback(new Error("external native probe"), "", "external native probe");
        } });
    }
    await new Script("(async () => {\n" + source.replace(/^import .*;\r?$/gm, "") + "\n})()")
        .runInContext(createContext(bindings));
    for (const canvas of declarations) t.after(() => canvas.onClose({ instanceId: canvas.id }));
    const context = (canvas, workingDirectory = checkout) => ({
        instanceId: canvas.id, session: { workingDirectory },
    });
    return { checkout: await realpath(checkout), other, sessionState, calls, admissions, declarations, context };
}

for (const surface of surfaces) {
    test(`${surface.id}: native cwd is the canvas checkout, never the SDK session-state directory`, async t => {
        const h = await harness(t);
        const canvas = h.declarations.find(value => value.id === surface.id);
        await canvas.open(h.context(canvas));
        assert.equal(h.calls.length, 0, "opening does not read or mutate native work");
        const action = canvas.actions.find(value => value.name === surface.action);
        await assert.rejects(action.handler({ ...h.context(canvas), input: surface.input }), /external native probe/);
        assert.equal(h.calls.length, 1, "the real server and native adapter must reach the external boundary");
        assert.equal(h.calls[0].cwd, h.checkout);
        assert.notEqual(h.calls[0].cwd, h.sessionState);
    });

    test(`${surface.id}: missing, relative, invalid and changed checkout contexts fail closed on open and every action`, async t => {
        const h = await harness(t);
        const canvas = h.declarations.find(value => value.id === surface.id);
        const unavailable = error => error.code === "workspace_unavailable";
        const invalid = [undefined, "", "relative-checkout", path.join(h.checkout, "missing"), path.join(h.checkout, "proposal.json")];
        for (const workingDirectory of invalid) {
            await assert.rejects(canvas.open({ instanceId: canvas.id, session: { workingDirectory } }), unavailable);
        }
        assert.equal(h.admissions.length, 0, "invalid context must not admit an executable");
        await canvas.open(h.context(canvas));
        await canvas.open(h.context(canvas, path.join(h.checkout, "..", "checkout")));
        assert.equal(h.admissions.length, 1, "canonical aliases reuse the original instance");
        await assert.rejects(canvas.open(h.context(canvas, h.other)), unavailable);
        for (const action of canvas.actions) {
            for (const workingDirectory of [...invalid, h.other]) {
                await assert.rejects(action.handler({
                    instanceId: canvas.id, session: { workingDirectory }, input: surface.input,
                }), unavailable);
            }
        }
        assert.equal(h.calls.length, 0, "no action may reach native code under missing or changed authority");
        await canvas.onClose({ instanceId: canvas.id });
        await canvas.open(h.context(canvas, h.other));
        assert.equal(h.admissions.length, 2, "a closed instance may explicitly open a new checkout");
    });
}
