import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { attachmentOpenExtension, createAttachmentOpener } from "../attachment-opener.mjs";

test("default-app allowlist excludes executable, macro, path and active browser formats", () => {
    for (const name of ["readme.TXT", "diagram.png", "report.pdf", "report.docx"])
        assert.ok(attachmentOpenExtension(name), name);
    for (const name of ["run.exe", "run.cmd", "run.ps1", "run.url", "shortcut.lnk", "macro.docm",
        "index.html", "image.svg", "unknown", "../notes.txt", "folder\\notes.txt", "file.txt:run.exe",
        "notes.txt ", "notes.txt.", "notes\u0000.txt", "https://host/file.txt"])
        assert.equal(attachmentOpenExtension(name), null, name);
});

test("default-app handoff is unavailable on unsupported or detected remote hosts", () => {
    assert.equal(createAttachmentOpener({ platform: "linux" }).available, false);
    assert.equal(createAttachmentOpener({ platform: "win32", env: { SystemRoot: "C:\\Windows", SSH_CONNECTION: "remote" },
        tempRoot: "C:\\Temp" }).available, false);
    assert.equal(createAttachmentOpener({ platform: "win32", env: { SystemRoot: "C:\\Windows" },
        tempRoot: "\\\\server\\share" }).available, false);
});

test("real local copy has exact bytes and Internet-zone marking before a bounded OS handoff", {
    skip: process.platform !== "win32",
}, async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "twig-open-test-"));
    const content = Buffer.from("synthetic attachment, not opened by this test");
    const calls = [];
    try {
        const opener = createAttachmentOpener({ tempRoot: root, execFile: async (executable, args, options) => {
            calls.push({ executable, args, options });
            const file = options.env.TWIG_ATTACHMENT_OPEN_FILE;
            assert.equal(path.dirname(file), options.cwd);
            assert.equal(path.basename(file), "attachment.txt");
            assert.deepEqual(await readFile(file), content);
            assert.equal(await readFile(file + ":Zone.Identifier", "utf8"), "[ZoneTransfer]\r\nZoneId=3\r\n");
            assert.equal(options.shell, false);
            assert.equal(options.timeout, 5000);
            assert.equal(args.includes("-NonInteractive"), true);
            const script = Buffer.from(args.at(-1), "base64").toString("utf16le");
            assert.match(script, /UseShellExecute=\$true/);
            assert.match(script, /Verb='open'/);
            assert.equal(script.includes(file), false, "local paths are data, never interpolated code");
        } });
        assert.equal(opener.available, true, "fixture needs a local Windows host and local temp volume");
        let checks = 0;
        await opener.open({ fileName: "user's notes.txt", content, signal: new AbortController().signal,
            assertCurrent: () => { checks++; } });
        assert.equal(checks, 2, "selection must be checked before writing and immediately before launching");
        assert.equal(calls.length, 1);
        assert.equal((await readdir(root)).length, 1, "the handed-off copy remains for the external app");

        await assert.rejects(opener.open({ fileName: "run.cmd", content,
            signal: new AbortController().signal, assertCurrent() {} }), /unavailable/);
        await assert.rejects(opener.open({ fileName: "large.txt", content: Buffer.alloc(16 * 1024 * 1024 + 1),
            signal: new AbortController().signal, assertCurrent() {} }), /unavailable/);
        await assert.rejects(opener.open({ fileName: "canceled.txt", content,
            signal: AbortSignal.abort(), assertCurrent() {} }));
        assert.equal((await readdir(root)).length, 1, "invalid and canceled requests must not create copies");
        let checksBeforeStale = 0;
        await assert.rejects(opener.open({ fileName: "stale.txt", content,
            signal: new AbortController().signal, assertCurrent() {
                if (++checksBeforeStale === 2) throw new Error("stale selection");
            } }), /stale selection/);
        assert.equal(calls.length, 1, "superseded selection cannot launch");
    } finally { await rm(root, { recursive: true, force: true }); }
});
