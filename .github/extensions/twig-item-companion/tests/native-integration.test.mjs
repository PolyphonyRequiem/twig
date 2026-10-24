// Real-binary integration for the explicit-Bench browse + detail readers. NOT synthetic.
// Opt-in: set TWIG_NATIVE_EXE_DIR (directory holding the exact built twig binary) and
// TWIG_NATIVE_FIXTURE (path to a provenance manifest). Entirely unset => skipped.
// Once either is set, any missing binary/manifest/field FAILS. No installed-twig fallback, no network, no init.
//
// Manifest (JSON) supplied by the native owner's sealed fixture:
//   { "nativeSha": "<git sha>", "binary": { "file": "twig.exe", "sha256": "<hex>" },
//     "workspacePath": "<bound checkout>", "bench": "<saved name or id>",
//     "memberId": <id>, "nonMemberId": <id>,
//     "currentBenchProbeArgs": ["<read-only twig args whose stdout names the current Bench>"] }
// Env for the child (checkout/home) is the fixture's responsibility: TWIG_NATIVE_ENV is an optional JSON object merged in.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { delimiter, dirname, resolve } from "node:path";
import test from "node:test";
import { createNativeBenchReader } from "../native-bench.mjs";
import { createNativeBenchDetailReader } from "../native-bench-detail.mjs";
import { captureBench } from "../native-bench.mjs";

const exeDir = process.env.TWIG_NATIVE_EXE_DIR;
const manifestPath = process.env.TWIG_NATIVE_FIXTURE;
const optedIn = exeDir !== undefined || manifestPath !== undefined;

test("real native twig: named Bench v2 browse, explicit-Bench detail, origin continuity, no current-Bench change",
    { skip: optedIn ? false : "native integration not opted in (TWIG_NATIVE_EXE_DIR/TWIG_NATIVE_FIXTURE unset)" },
    async () => {
        assert.ok(exeDir && manifestPath, "both TWIG_NATIVE_EXE_DIR and TWIG_NATIVE_FIXTURE are required once opted in");
        const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
        for (const key of ["nativeSha", "binary", "workspacePath", "bench", "memberId", "nonMemberId", "currentBenchProbeArgs", "env"]) {
            assert.ok(manifest[key] !== undefined, `manifest is missing ${key}`);
        }
        const exe = resolve(exeDir, manifest.binary.file);
        assert.equal(dirname(exe), resolve(exeDir), "manifest binary must be inside the exact build directory");
        const digest = createHash("sha256").update(await readFile(exe)).digest("hex");
        assert.equal(digest, manifest.binary.sha256, "binary under TWIG_NATIVE_EXE_DIR is not the manifest's exact build");

        const savedPath = process.env.PATH;
        const savedEnv = { ...process.env };
        const extra = { ...manifest.env, ...(process.env.TWIG_NATIVE_ENV ? JSON.parse(process.env.TWIG_NATIVE_ENV) : {}) };
        Object.assign(process.env, extra);
        // Twig's checkout admission needs git; put the verified binary ahead of inherited tools.
        process.env.PATH = resolve(exeDir) + delimiter + (savedPath ?? "");
        try {
            const probe = () => execFileSync(exe, manifest.currentBenchProbeArgs,
                { cwd: manifest.workspacePath, encoding: "utf8", timeout: 15000, windowsHide: true });
            const currentBefore = probe();
            assert.equal(JSON.parse(currentBefore).current, "Other", "fixture must start on the non-target Bench");

            const browse = await createNativeBenchReader()({ workspacePath: manifest.workspacePath, bench: manifest.bench });
            assert.equal(browse.version, 2);
            assert.equal(browse.observation, "raw-mirror");
            const expected = captureBench(browse);
            const member = browse.members.find((entry) => entry.id === manifest.memberId);
            assert.ok(member && member.loaded, "fixture member must be a loaded member of the named Bench");

            const readDetail = createNativeBenchDetailReader();
            const detail = await readDetail({ workItemId: manifest.memberId, workspacePath: manifest.workspacePath, expected });
            assert.equal(detail.benchId, browse.benchId);
            assert.deepEqual(
                { ...detail.origin, storageGeneration: detail.origin.storageGeneration ?? null },
                { ...browse.origin, storageGeneration: browse.origin.storageGeneration ?? null },
                "whole-origin continuity between browse and detail");
            assert.equal(detail.layoutStatus, "unavailable");
            assert.equal(detail.completeness.sourceComplete, false);

            await assert.rejects(
                readDetail({ workItemId: manifest.nonMemberId, workspacePath: manifest.workspacePath, expected }),
                /bench-item-not-member/);

            const currentAfter = probe();
            assert.equal(JSON.parse(currentAfter).current, "Other", "explicit reads must preserve the non-target Bench");
            assert.equal(currentAfter, currentBefore, "shared current-Bench pointer changed during explicit reads");
        } finally {
            for (const key of Object.keys(process.env)) if (!(key in savedEnv)) delete process.env[key];
            Object.assign(process.env, savedEnv);
            process.env.PATH = savedPath;
        }
    });
