import { execFile as nodeExecFile } from "node:child_process";
import { mkdtemp, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";

const extensions = new Set(["txt", "log", "md", "markdown", "csv", "tsv", "json", "xml",
    "yaml", "yml", "ini", "cfg", "conf", "diff", "patch", "pdf", "docx", "xlsx", "pptx",
    "png", "jpg", "jpeg", "gif", "webp", "bmp"]);
const launchScript = "$ErrorActionPreference='Stop';" +
    "$s=[System.Diagnostics.ProcessStartInfo]::new();" +
    "$s.FileName=$env:TWIG_ATTACHMENT_OPEN_FILE;" +
    "$s.WorkingDirectory=[System.IO.Path]::GetDirectoryName($s.FileName);" +
    "$s.UseShellExecute=$true;$s.Verb='open';[void][System.Diagnostics.Process]::Start($s)";

export function attachmentOpenExtension(fileName) {
    if (typeof fileName !== "string" || fileName.length > 255 ||
        /[\x00-\x1f\x7f<>:"/\\|?*]/.test(fileName) || /[. ]$/.test(fileName)) return null;
    const extension = fileName.split(".").at(-1).toLowerCase();
    return fileName.includes(".") && extensions.has(extension) ? extension : null;
}

export function createAttachmentOpener({ platform = process.platform, env = process.env,
    tempRoot = os.tmpdir(), execFile = promisify(nodeExecFile) } = {}) {
    const available = platform === "win32" && !env.SSH_CONNECTION && !env.SSH_CLIENT && !env.SSH_TTY &&
        !env.CODESPACES && /^[a-z]:\\/i.test(tempRoot) && /^[a-z]:\\/i.test(env.SystemRoot ?? "");
    return {
        available,
        async open({ fileName, content, signal, assertCurrent }) {
            const extension = attachmentOpenExtension(fileName);
            if (!available || !extension || !Buffer.isBuffer(content) || content.length > 16 * 1024 * 1024)
                throw new Error("Default-app opening is unavailable for this host or attachment.");
            signal.throwIfAborted();
            assertCurrent();
            const directory = await mkdtemp(path.join(tempRoot, "twig-attachment-"));
            // Never use a provider filename as a path or a shell argument.
            const file = path.join(directory, "attachment." + extension);
            await writeFile(file, content, { flag: "wx", signal });
            // Fail closed on filesystems that cannot retain Windows' downloaded-file marking.
            await writeFile(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", { flag: "wx", signal });
            signal.throwIfAborted();
            assertCurrent();
            await execFile(path.win32.join(env.SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Buffer.from(launchScript, "utf16le").toString("base64")],
                { shell: false, windowsHide: true, timeout: 5000, maxBuffer: 16384, signal,
                    env: { ...env, TWIG_ATTACHMENT_OPEN_FILE: file }, cwd: directory });
        },
    };
}
