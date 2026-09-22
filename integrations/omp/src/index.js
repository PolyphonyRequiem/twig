import { Text } from "@oh-my-pi/pi-tui";
import {
  buildPreviewReceipt,
  captureProposal,
  previewConstants,
} from "./native-preview.js";
import { ProposalViewer } from "./viewer.js";

export {
  buildPreviewReceipt,
  captureProposal,
  parsePreviewEnvelope,
  previewConstants,
  resolveProposalLocation,
  sanitizeNativeFrame,
  validatePreviewEnvelope,
} from "./native-preview.js";
export { ProposalViewer } from "./viewer.js";
export { ProposalViewport } from "./viewer-core.js";
function terminalColumns() {
  const columns = Number(process.stdout?.columns);
  return Number.isFinite(columns) && columns > 0 ? columns : previewConstants.defaultWidth;
}

function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

export function parseReviewArgs(args) {
  let file = String(args ?? "").trim();
  let full = false;
  if (/^--full(?:\s|$)/.test(file)) {
    full = true;
    file = file.slice(6).trim();
  }
  if (file.length >= 2 && (file[0] === '"' || file[0] === "'") && file.at(-1) === file[0]) {
    file = file.slice(1, -1);
  }
  if (!file) {
    throw new Error("Usage: /twig-review [--full] <proposal.json>");
  }
  return { file, full };
}

function semanticReceipt(receipt) {
  const { presentation: _presentation, ...semantic } = receipt;
  return semantic;
}

function receiptText(receipt) {
  return JSON.stringify(semanticReceipt(receipt));
}

async function displayReceipt(receipt, initialMode, ctx, signal) {
  if (!ctx.hasUI) {
    throw new Error("Twig proposal preview needs an interactive OMP UI; no preview was displayed.");
  }

  if (signal?.aborted) {
    const error = new Error("Twig proposal preview was cancelled.");
    error.name = "AbortError";
    throw error;
  }
  let displayed = false;
  let cancelled = false;
  let viewer;
  let removeAbortListener;
  try {
    await ctx.ui.custom(
      (tui, theme, _keybindings, done) => {
        if (signal) {
          const onAbort = () => {
            cancelled = true;
            done();
          };
          signal.addEventListener("abort", onAbort, { once: true });
          removeAbortListener = () => signal.removeEventListener("abort", onAbort);
        }
        const fg = typeof theme?.fg === "function" ? theme.fg.bind(theme) : (_style, text) => text;
        viewer = new ProposalViewer({
          receipt,
          initialMode,
          heading: fg("accent", "Twig proposal · native rendering · preview only"),
          dim: (text) => fg("dim", text),
          requestRender: () => tui.requestRender(),
          done,
          onDisplayed: () => {
            displayed = true;
          },
        });
        return viewer;
      },
      { overlay: true },
    );
  } finally {
    removeAbortListener?.();
  }
  if (cancelled || signal?.aborted) {
    const error = new Error("Twig proposal preview was cancelled.");
    error.name = "AbortError";
    throw error;
  }

  if (!displayed || !viewer) {
    throw new Error("Twig proposal preview closed before its native frame was displayed.");
  }
  return {
    ...buildPreviewReceipt(receipt, {
      filePath: receipt.filePath,
      workspacePath: receipt.workspacePath,
      displayed: true,
      approved: false,
      applied: false,
    }),
    viewMode: viewer.mode,
  };
}

function renderToolResult(result, _options, theme) {
  if (result?.isError) {
    const text = (result.content ?? [])
      .filter((part) => part?.type === "text")
      .map((part) => part.text)
      .join("\n");
    const errorColor = typeof theme?.fg === "function" ? theme.fg("error", text) : text;
    return new Text(errorColor, 0, 0);
  }
  const receipt = result?.details;
  const status = receipt?.displayed === true
    ? "Twig proposal review displayed · preview only · not approved or applied"
    : "Twig proposal review unavailable · no preview displayed";
  const statusColor = typeof theme?.fg === "function" ? theme.fg("dim", status) : status;
  return new Text(statusColor, 0, 0);
}

export default function twigProposalPresenter(pi) {
  const z = pi.zod;
  pi.registerTool({
    name: "twig_proposal_render",
    label: "Twig Proposal",
    description:
      "Display one native Twig proposal review in OMP. Brief by default; full exposes exact long bodies. " +
      "Preview only: this never approves or applies the proposal.",
    parameters: z.object({
      file: z.string().describe("Proposal JSON path, relative to the invocation cwd or explicit workspace."),
      workspace: z.string().optional().describe("Workspace path containing the proposal file."),
      full: z.boolean().optional().describe("Open the retained full frame instead of brief; no second preview is run."),
    }),
    async execute(_toolCallId, params, signal, _onUpdate, ctx) {
      if (signal?.aborted) {
        return {
          content: [{ type: "text", text: "Twig proposal preview cancelled; displayed=false; approved=false; applied=false." }],
          isError: true,
        };
      }
      if (!ctx.hasUI) {
        return {
          content: [{ type: "text", text: "Twig proposal preview needs an interactive OMP UI; displayed=false; approved=false; applied=false." }],
          isError: true,
        };
      }

      try {
        const receipt = await captureProposal({
          ...params,
          cwd: ctx.cwd,
          columns: Math.max(previewConstants.minWidth, terminalColumns() - 4),
          signal,
        });
        const displayedReceipt = await displayReceipt(receipt, params.full ? "full" : "brief", ctx, signal);
        return {
          content: [{ type: "text", text: receiptText(displayedReceipt) }],
          details: displayedReceipt,
        };
      } catch (error) {
        const message = errorMessage(error);
        return {
          content: [{ type: "text", text: `Twig proposal preview failed: ${message}` }],
          isError: true,
        };
      }
    },
    renderResult: renderToolResult,
  });

  pi.registerCommand("twig-review", {
    description: "Show native colored proposal review: /twig-review [--full] <proposal.json>. Preview only.",
    async handler(args, ctx) {
      if (!ctx.hasUI) {
        throw new Error("Twig proposal preview needs an interactive OMP UI; no preview was displayed.");
      }
      try {
        const request = parseReviewArgs(args);
        const receipt = await captureProposal({
          ...request,
          cwd: ctx.cwd,
          columns: Math.max(previewConstants.minWidth, terminalColumns() - 2),
        });
        const displayedReceipt = await displayReceipt(receipt, request.full ? "full" : "brief", ctx);
        ctx.ui.notify(
          `Twig proposal preview closed (${displayedReceipt.digest}). Nothing approved or applied.`,
          "info",
        );
      } catch (error) {
        ctx.ui.notify(errorMessage(error), "error");
      }
    },
  });
}
