import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { Script, createContext } from "node:vm";
import test from "node:test";

test("registered SDK canvas actions expose no attachment download entrypoint", async () => {
    let configuration;
    let nativeCalls = 0;
    const source = await readFile(new URL("../extension.mjs", import.meta.url), "utf8");
    const context = createContext({
        Error,
        Map,
        CanvasError: class extends Error {},
        createCanvas: declaration => declaration,
        joinSession: async config => { configuration = config; return { workspacePath: "fixture" }; },
        createNativeAttachmentReader: () => { nativeCalls++; throw new Error("SDK must not download"); },
        createNativeAttachmentDownloader: () => { nativeCalls++; throw new Error("SDK must not download"); },
    });
    await new Script(`(async () => { ${source.replace(/^import .*;$/gm, "")}\n })()`).runInContext(context);
    assert.ok(configuration.canvases.length > 0, "exercise the actual extension declarations");
    for (const canvas of configuration.canvases) {
        const downloadActions = (canvas.actions ?? []).filter(action => /download|attachment/i.test(action.name));
        assert.equal(downloadActions.length, 0, `${canvas.id} must not expose a download SDK action`);
        assert.equal((canvas.actions ?? []).find(action => action.name === "download_attachment"), undefined);
    }
    assert.equal(configuration.tools, undefined, "the extension must not register an agent download tool");
    assert.equal(nativeCalls, 0);
});
