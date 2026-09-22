import { spawn as childSpawn } from "node:child_process";
import { readdirSync, realpathSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";

const REVIEW_MODEL = "twig.change-proposal.review";
const MIN_WIDTH = 20;
const MAX_WIDTH = 400;
const DEFAULT_WIDTH = 120;

function abortError() {
  const error = new Error("Twig proposal preview was cancelled.");
  error.name = "AbortError";
  return error;
}

function assertObject(value, label) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`Twig preview returned an invalid ${label}.`);
  }
  return value;
}

function assertString(value, label) {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`Twig preview returned a missing ${label}.`);
  }
  return value;
}

function assertBoolean(value, label) {
  if (typeof value !== "boolean") {
    throw new Error(`Twig preview returned a missing ${label}.`);
  }
  return value;
}

function assertArray(value, label) {
  if (!Array.isArray(value)) {
    throw new Error(`Twig preview returned a missing ${label}.`);
  }
  return value;
}

function assertInteger(value, label) {
  if (!Number.isInteger(value)) {
    throw new Error(`Twig preview returned a missing ${label}.`);
  }
  return value;
}
function validateReviewModel(reviewModel, digest) {
  if (reviewModel.model !== REVIEW_MODEL) {
    throw new Error("Twig preview returned an unsupported review model.");
  }
  if (reviewModel.modelVersion !== 1) {
    throw new Error("Twig preview returned an unsupported review model version.");
  }
  if (reviewModel.digest !== digest) {
    throw new Error("Twig preview digest drifted between envelope and reviewModel.");
  }

  const reviewWorkspace = assertObject(reviewModel.workspace, "reviewModel.workspace");
  assertString(reviewWorkspace.organization, "reviewModel.workspace.organization");
  assertString(reviewWorkspace.project, "reviewModel.workspace.project");

  const affectedItems = assertArray(reviewModel.affectedItems, "reviewModel.affectedItems array");
  for (const item of affectedItems) {
    const affected = assertObject(item, "reviewModel.affectedItems entry");
    assertInteger(affected.id, "reviewModel.affectedItems.id");
    assertString(affected.role, "reviewModel.affectedItems.role");
  }

  const operations = assertArray(reviewModel.operations, "reviewModel.operations array");
  for (const operation of operations) {
    const reviewOperation = assertObject(operation, "reviewModel.operations entry");
    assertInteger(reviewOperation.ordinal, "reviewModel.operations.ordinal");
    assertString(reviewOperation.opId, "reviewModel.operations.opId");
    assertString(reviewOperation.kind, "reviewModel.operations.kind");
    assertObject(reviewOperation.target, "reviewModel.operations.target");
    assertString(reviewOperation.summary, "reviewModel.operations.summary");
    const preconditions = assertArray(reviewOperation.preconditions, "reviewModel.operations.preconditions");
    for (const precondition of preconditions) {
      const item = assertObject(precondition, "reviewModel.operations.precondition");
      assertString(item.kind, "reviewModel.operations.precondition.kind");
      assertString(item.value, "reviewModel.operations.precondition.value");
    }
    const consequences = assertArray(reviewOperation.consequences, "reviewModel.operations.consequences");
    for (const consequence of consequences) {
      const item = assertObject(consequence, "reviewModel.operations.consequence");
      assertString(item.kind, "reviewModel.operations.consequence.kind");
    }
  }

  const choices = assertArray(reviewModel.authorizationChoices, "reviewModel.authorizationChoices array");
  for (const choice of choices) assertString(choice, "reviewModel.authorizationChoices entry");

  const blockers = assertArray(reviewModel.blockers, "reviewModel.blockers array");
  for (const blocker of blockers) {
    const item = assertObject(blocker, "reviewModel.blockers entry");
    assertString(item.kind, "reviewModel.blockers.kind");
    assertString(item.detail, "reviewModel.blockers.detail");
  }
  if (reviewModel.contextItems != null) assertArray(reviewModel.contextItems, "reviewModel.contextItems array");
}

function isFile(path) {
  try { return statSync(path).isFile(); } catch { return false; }
}

function isDirectory(path) {
  try { return statSync(path).isDirectory(); } catch { return false; }
}

function isWorkspaceRoot(dir) {
  if (isFile(join(dir, "twig.json"))) return true;
  const twigDir = join(dir, ".twig");
  if (!isDirectory(twigDir)) return false;

  // The global Twig home holds binaries/profiles, not a worktree. Match the
  // native WorkspaceDiscovery predicate before handing a path to the CLI.
  const stateHome = process.platform === "linux" && process.env.XDG_STATE_HOME
    ? join(process.env.XDG_STATE_HOME, "twig") : join(homedir(), ".twig");
  if (resolve(twigDir).toLowerCase() === resolve(stateHome).toLowerCase()) return false;
  if (isFile(join(twigDir, "config"))) return true;

  try {
    for (const org of readdirSync(twigDir, { withFileTypes: true })) {
      if (!org.isDirectory()) continue;
      for (const project of readdirSync(join(twigDir, org.name), { withFileTypes: true })) {
        if (!project.isDirectory()) continue;
        const context = join(twigDir, org.name, project.name);
        if (isFile(join(context, "twig.db")) || isFile(join(context, "config"))) return true;
      }
    }
  } catch { return false; }
  return false;
}

function findWorkspaceRoot(start) {
  let dir = realpathSync(start);
  for (;;) {
    if (isWorkspaceRoot(dir)) return dir;
    const parent = dirname(dir);
    if (parent === dir) return undefined;
    dir = parent;
  }
}
function findStringTerminator(frame, start) {
  const bel = frame.indexOf("\x07", start);
  const st = frame.indexOf("\x1b\\", start);
  if (bel < 0) return st < 0 ? -1 : st + 2;
  if (st < 0) return bel + 1;
  return Math.min(bel + 1, st + 2);
}

export function sanitizeNativeFrame(frame) {
  if (typeof frame !== "string") {
    throw new Error("Twig preview returned a non-string native frame.");
  }
  let safe = "";
  for (let index = 0; index < frame.length;) {
    const code = frame.charCodeAt(index);
    if (code === 0x1b) {
      if (frame.startsWith("\x1b]8;", index)) {
        const end = findStringTerminator(frame, index + 4);
        if (end < 0) break;
        safe += frame.slice(index, end);
        index = end;
        continue;
      }
      if (frame[index + 1] === "[") {
        let end = index + 2;
        while (end < frame.length && (frame.charCodeAt(end) < 0x40 || frame.charCodeAt(end) > 0x7e)) end += 1;
        if (end < frame.length && frame[end] === "m" && /^[0-9;:]*$/.test(frame.slice(index + 2, end))) {
          safe += frame.slice(index, end + 1);
        }
        index = end < frame.length ? end + 1 : frame.length;
        continue;
      }
      if (frame[index + 1] === "]" || "PX^_".includes(frame[index + 1] ?? "")) {
        const end = findStringTerminator(frame, index + 2);
        index = end < 0 ? frame.length : end;
        continue;
      }
      index += 1;
      continue;
    }
    if ((code <= 0x08) || (code >= 0x0b && code <= 0x0c) || (code >= 0x0e && code <= 0x1f) ||
        (code >= 0x7f && code <= 0x9f)) {
      index += 1;
      continue;
    }
    safe += frame[index];
    index += 1;
  }
  return safe.replace(/\r\n?/g, "\n");
}

function resolveTwigBinary(env) {
  const configured = env.TWIG_BIN;
  if (configured == null || configured.trim() === "") {
    return "twig";
  }
  if (configured.includes("\0")) {
    throw new Error("TWIG_BIN contains an unsafe NUL character.");
  }
  if (!isAbsolute(configured)) {
    throw new Error("TWIG_BIN must be an absolute executable path.");
  }
  return configured;
}

export function clampPreviewWidth(width) {
  if (typeof width !== "number" || !Number.isFinite(width)) {
    return DEFAULT_WIDTH;
  }
  return Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, Math.floor(width)));
}

export function resolveProposalLocation({ cwd, workspace, file }) {
  if (typeof cwd !== "string" || cwd.length === 0) {
    throw new Error("Twig proposal preview needs an invocation cwd.");
  }
  if (typeof file !== "string" || file.length === 0) {
    throw new Error("Twig proposal preview needs a proposal file path.");
  }
  if (cwd.includes("\0") || file.includes("\0") || (workspace && workspace.includes("\0"))) {
    throw new Error("Twig proposal preview paths contain an unsafe NUL character.");
  }

  const invocationCwd = realpathSync(resolve(cwd));
  const workspaceRoot = workspace
    ? realpathSync(resolve(invocationCwd, workspace))
    : findWorkspaceRoot(invocationCwd) ?? invocationCwd;
  const fileBase = workspace ? workspaceRoot : invocationCwd;
  const absoluteFile = realpathSync(resolve(fileBase, file));

  const workspaceStat = statSync(workspaceRoot);
  if (!workspaceStat.isDirectory()) {
    throw new Error(`Twig workspace is not a directory: ${workspaceRoot}`);
  }
  if (!isWorkspaceRoot(workspaceRoot)) {
    throw new Error(`No Twig workspace markers were found at ${workspaceRoot}`);
  }

  const fileStat = statSync(absoluteFile);
  if (!fileStat.isFile()) {
    throw new Error(`Not a proposal file: ${absoluteFile}`);
  }

  const inside = relative(workspaceRoot, absoluteFile);
  if (inside === ".." || inside.startsWith(`..${sep}`) || isAbsolute(inside)) {
    throw new Error(`Proposal file is outside the selected workspace: ${absoluteFile}`);
  }

  return {
    filePath: absoluteFile,
    workspacePath: workspaceRoot,
  };
}

export function parsePreviewEnvelope(jsonText) {
  if (typeof jsonText !== "string" || jsonText.trim() === "") {
    throw new Error("Twig preview returned no JSON payload.");
  }
  let parsed;
  try {
    parsed = JSON.parse(jsonText);
  } catch {
    throw new Error("Twig preview returned truncated or invalid JSON.");
  }
  return validatePreviewEnvelope(parsed);
}

export function validatePreviewEnvelope(envelope) {
  const top = assertObject(envelope, "preview envelope");
  const digest = assertString(top.digest, "proposal digest");
  assertBoolean(top.canApply, "canApply flag");
  assertArray(top.issues, "issues array");
  assertArray(top.operations, "operations array");
  assertArray(top.pendingChanges, "pendingChanges array");

  const reviewModel = assertObject(top.reviewModel, "reviewModel");
  validateReviewModel(reviewModel, digest);

  if (top.workspace != null) {
    const topWorkspace = assertObject(top.workspace, "workspace");
    assertString(topWorkspace.organization, "workspace.organization");
    assertString(topWorkspace.project, "workspace.project");
  }

  const presentation = assertObject(top.presentation, "presentation");
  if (presentation.version !== 1) {
    throw new Error("Twig preview returned an unsupported presentation version.");
  }
  if (presentation.format !== "ansi" && presentation.format !== "text") {
    throw new Error("Twig preview returned an unsupported presentation format.");
  }
  const width = assertInteger(presentation.width, "presentation.width");
  if (width < MIN_WIDTH || width > MAX_WIDTH) {
    throw new Error("Twig preview returned a presentation width outside the supported range.");
  }
  const brief = sanitizeNativeFrame(assertString(presentation.brief, "presentation.brief"));
  const full = sanitizeNativeFrame(assertString(presentation.full, "presentation.full"));
  assertString(brief, "presentation.brief");
  assertString(full, "presentation.full");
  top.presentation = { ...presentation, brief, full };

  return top;
}

export function buildPreviewReceipt(envelope, { filePath, workspacePath, displayed, approved, applied }) {
  return {
    ...envelope,
    filePath,
    workspacePath,
    displayed,
    approved,
    applied,
  };
}

export async function captureProposal({
  file,
  workspace,
  cwd,
  columns = DEFAULT_WIDTH,
  signal,
  spawn = childSpawn,
  env = process.env,
}) {
  if (signal?.aborted) {
    throw abortError();
  }

  const { filePath, workspacePath } = resolveProposalLocation({ cwd, workspace, file });
  const width = clampPreviewWidth(columns);
  const color = Object.prototype.hasOwnProperty.call(env, "NO_COLOR") ? "never" : "always";
  const args = [
    "proposal",
    "preview",
    "--file",
    filePath,
    "-o",
    "json",
    "--include-rendering",
    "--width",
    String(width),
    "--color",
    color,
  ];

  const child = spawn(resolveTwigBinary(env), args, {
    cwd: workspacePath,
    env: { ...env },
    stdio: ["ignore", "pipe", "pipe"],
    shell: false,
    windowsHide: true,
  });

  if (!child.stdout || !child.stderr) {
    throw new Error("Twig preview child process did not expose ordinary pipes.");
  }

  let stdout = "";
  let stderr = "";
  child.stdout.on("data", (chunk) => {
    stdout += chunk.toString("utf8");
  });
  child.stderr.on("data", (chunk) => {
    stderr += chunk.toString("utf8");
  });

  let aborted = false;
  const onAbort = () => {
    aborted = true;
    if (typeof child.kill === "function") {
      child.kill();
    }
  };
  signal?.addEventListener("abort", onAbort, { once: true });
  if (signal?.aborted) onAbort();

  try {
    const outcome = await new Promise((resolve, reject) => {
      child.once("error", reject);
      child.once("close", (code, signalCode) => resolve({ code, signalCode }));
    });

    if (aborted || signal?.aborted) {
      throw abortError();
    }
    if (outcome.signalCode != null) {
      throw new Error(stderr.trim() || `Twig preview was terminated by ${outcome.signalCode}.`);
    }
    if (outcome.code !== 0) {
      throw new Error(stderr.trim() || `Twig preview failed with exit code ${outcome.code}.`);
    }

    const envelope = parsePreviewEnvelope(stdout);
    const expectedFormat = color === "always" ? "ansi" : "text";
    if (envelope.presentation.format !== expectedFormat) {
      throw new Error(`Twig preview returned ${envelope.presentation.format} for --color ${color}; expected ${expectedFormat}.`);
    }
    return buildPreviewReceipt(envelope, {
      filePath,
      workspacePath,
      displayed: false,
      approved: false,
      applied: false,
    });
  } finally {
    signal?.removeEventListener("abort", onAbort);
  }
}

export const previewConstants = Object.freeze({
  reviewModel: REVIEW_MODEL,
  minWidth: MIN_WIDTH,
  maxWidth: MAX_WIDTH,
  defaultWidth: DEFAULT_WIDTH,
});
