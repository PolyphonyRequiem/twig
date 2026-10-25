import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import test from "node:test";

test("no production module launches a bare PATH-resolved twig", async () => {
    const dir = new URL("../", import.meta.url);
    const offenders = [];
    for (const name of await readdir(dir)) {
        if (!name.endsWith(".mjs")) continue;
        const source = await readFile(new URL(name, dir), "utf8");
        if (/\b(execFile|spawn|launch)\(\s*["'`]twig(\.exe)?["'`]/.test(source) || /=\s*["'`]twig["'`];/.test(source)) offenders.push(name);
    }
    assert.deepEqual(offenders, []);
});
