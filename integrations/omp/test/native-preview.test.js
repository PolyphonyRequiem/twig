import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { PassThrough } from "node:stream";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, test } from "node:test";
import {
  captureProposal,
  parsePreviewEnvelope,
  resolveProposalLocation,
  validatePreviewEnvelope,
} from "../src/native-preview.js";
import { ProposalViewport } from "../src/viewer-core.js";

const DIGEST = "opaque-digest-preserved-verbatim";

function envelope(overrides = {}) {
  const { reviewModel: reviewOverrides, ...topOverrides } = overrides;
  const reviewModel = {
    model: "twig.change-proposal.review",
    modelVersion: 1,
    digest: DIGEST,
    workspace: { organization: "example", project: "demo" },
    affectedItems: [],
    operations: [],
    authorizationChoices: [],
    blockers: [],
    ...reviewOverrides,
  };
  return {
    digest: DIGEST,
    canApply: true,
    issues: [],
    operations: [],
    pendingChanges: [],
    reviewModel,
    presentation: {
      version: 1,
      format: "ansi",
      width: 80,
      brief: "\u001b[32mBRIEF\u001b[0m",
      full: "\u001b[32mFULL\u001b[0m\nsecond line",
    },
    ...topOverrides,
  };
}

function fakeChild({ stdout = JSON.stringify(envelope()), stderr = "", code = 0, signalCode = null, waitForKill = false } = {}) {
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.killed = false;
  child.kill = () => {
    child.killed = true;
    child.stdout.end();
    child.stderr.end();
    queueMicrotask(() => child.emit("close", null, "SIGTERM"));
    return true;
  };
  if (!waitForKill) {
    queueMicrotask(() => {
      child.stdout.end(Buffer.from(stdout));
      child.stderr.end(Buffer.from(stderr));
      child.emit("close", code, signalCode);
    });
  }
  return child;
}

let temporaryDirectories = [];
afterEach(async () => {
  await Promise.all(temporaryDirectories.map((path) => rm(path, { recursive: true, force: true })));
  temporaryDirectories = [];
});

async function workspaceFixture(name = "workspace with spaces") {
  const root = await mkdtemp(join(tmpdir(), "twig-omp-presenter-"));
  const workspace = join(root, name);
  await mkdir(join(workspace, ".twig"), { recursive: true });
  await writeFile(join(workspace, "twig.json"), JSON.stringify({ organization: "example", project: "demo" }));
  const file = join(workspace, "proposal file.json");
  await writeFile(file, "{}", "utf8");
  temporaryDirectories.push(root);
  return { root, workspace, file };
}

describe("native preview validation", () => {
  test("rejects unsupported, missing, truncated, and cross-field drift", () => {
    assert.throws(() => validatePreviewEnvelope(envelope({ reviewModel: { modelVersion: 2 } })), /unsupported review model version/);
    const missingPresentation = envelope();
    delete missingPresentation.presentation;
    assert.throws(() => validatePreviewEnvelope(missingPresentation), /presentation/);
    assert.throws(() => parsePreviewEnvelope('{"digest":"'), /truncated or invalid JSON/);
    assert.throws(() => validatePreviewEnvelope(envelope({ reviewModel: { digest: "other" } })), /digest drifted/);
    const malformedReview = envelope({ reviewModel: { operations: [{}] } });
    assert.throws(() => validatePreviewEnvelope(malformedReview));
  });

  test("preserves opaque digest and unknown additive envelope members", () => {
    const input = envelope({ futureMember: { enabled: true } });
    const validated = validatePreviewEnvelope(input);
    assert.equal(validated.digest, DIGEST);
    assert.deepEqual(validated.futureMember, { enabled: true });
  });

  test("preserves SGR and OSC8 while dropping terminal control modes", () => {
    const input = envelope({
      presentation: {
        version: 1,
        format: "ansi",
        width: 80,
        brief: "\u001b[2J\u001b[31mred\u001b[0m\u001b]8;;https://example.invalid\u001b\\link\u001b]8;;\u001b\\",
        full: "full",
      },
    });
    const validated = validatePreviewEnvelope(input);
    assert.match(validated.presentation.brief, /\u001b\[31mred\u001b\[0m/);
    assert.match(validated.presentation.brief, /\u001b\]8;;https:\/\/example\.invalid/);
    assert.doesNotMatch(validated.presentation.brief, /\u001b\[2J/);
  });
});

describe("native preview location and child process safety", () => {
  test("resolves relative proposal files from explicit workspace rather than cwd", async () => {
    const { root, workspace, file } = await workspaceFixture();
    const receipt = await captureProposal({
      cwd: root,
      workspace,
      file: "proposal file.json",
      env: { PATH: process.env.PATH ?? "" },
      spawn: () => fakeChild(),
    });
    assert.equal(receipt.filePath, file);
    assert.equal(receipt.workspacePath, workspace);
  });

  test("skips empty nested .twig markers and uses the configured ancestor", async () => {
    const { workspace, file } = await workspaceFixture();
    const nested = join(workspace, "nested");
    await mkdir(join(nested, ".twig"), { recursive: true });
    const location = resolveProposalLocation({ cwd: nested, file: "../proposal file.json" });
    assert.deepEqual(location, { filePath: file, workspacePath: workspace });
    assert.throws(() => resolveProposalLocation({ cwd: nested, workspace: nested, file }), /workspace markers/);
  });

  test("rejects proposal path escape and preserves spaces in argv without shell", async () => {
    const { root, workspace, file } = await workspaceFixture();
    const outside = join(root, "outside.json");
    await writeFile(outside, "{}", "utf8");
    assert.throws(
      () => resolveProposalLocation({ cwd: workspace, workspace, file: "../outside.json" }),
      /outside the selected workspace/,
    );
    await assert.rejects(
      captureProposal({
        cwd: workspace,
        file,
        env: { TWIG_BIN: "twig;bad", PATH: process.env.PATH ?? "" },
        spawn: () => fakeChild(),
      }),
      /TWIG_BIN must be an absolute executable path/,
    );


    const twigBin = join(workspace, "bin", "twig");
    let invocation;
    const receipt = await captureProposal({
      cwd: workspace,
      workspace,
      file: file.slice(workspace.length + 1),
      env: { TWIG_BIN: twigBin, PATH: process.env.PATH ?? "" },
      spawn(binary, args, options) {
        invocation = { binary, args, options };
        return fakeChild();
      },
    });
    assert.equal(invocation.binary, twigBin);
    assert.equal(invocation.options.shell, false);
    assert.deepEqual(invocation.args.slice(0, 7), [
      "proposal", "preview", "--file", file, "-o", "json", "--include-rendering",
    ]);
    assert.equal(invocation.args.at(-1), "always");
    assert.equal(receipt.filePath, file);
  });

  test("uses explicit never color when NO_COLOR is present", async () => {
    const { workspace, file } = await workspaceFixture();
    let args;
    await captureProposal({
      cwd: workspace,
      file,
      env: { NO_COLOR: "", PATH: process.env.PATH ?? "" },
      spawn(_binary, childArgs) {
        args = childArgs;
        const plain = envelope();
        plain.presentation = { ...plain.presentation, format: "text", brief: "BRIEF", full: "FULL" };
        return fakeChild({ stdout: JSON.stringify(plain) });
      },
    });
    assert.equal(args.at(-1), "never");
  });

  test("fails closed on child failure and cancellation", async () => {
    const { workspace, file } = await workspaceFixture();
    await assert.rejects(
      captureProposal({
        cwd: workspace,
        file,
        spawn: () => fakeChild({ code: 1, stderr: "native failure" }),
      }),
      /native failure/,
    );

    const controller = new AbortController();
    let child;
    const pending = captureProposal({
      cwd: workspace,
      file,
      signal: controller.signal,
      spawn: () => {
        child = fakeChild({ waitForKill: true });
        return child;
      },
    });
    controller.abort();
    await assert.rejects(pending, /cancelled/);
    assert.equal(child.killed, true);
  });
});

test("retains brief and full frames while switching details and back", () => {
  const viewport = new ProposalViewport({ brief: "brief one\nbrief two", full: "full one\nfull two\nfull three" });
  viewport.setViewportRows(1);
  assert.equal(viewport.currentFrame(), "brief one\nbrief two");
  viewport.showDetails();
  assert.equal(viewport.visibleRange().start, 0);
  viewport.scrollBy(2);
  assert.equal(viewport.visibleRange().start, 2);
  viewport.showBrief();
  assert.equal(viewport.offset, 0);
  assert.equal(viewport.currentFrame(), "brief one\nbrief two");
});
