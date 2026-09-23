import { Text, matchesKey, truncateToWidth } from "@oh-my-pi/pi-tui";
import { ProposalViewport } from "./viewer-core.js";

function terminalRows() {
  const rows = Number(process.stdout?.rows);
  return Number.isFinite(rows) && rows > 0 ? rows : 24;
}

export class ProposalViewer {
  #viewport;
  #heading;
  #dim;
  #requestRender;
  #done;
  #onDisplayed;
  #displayed = false;
  #body;

  constructor({ receipt, initialMode = "brief", heading, dim, requestRender, done, onDisplayed }) {
    this.#viewport = new ProposalViewport({
      brief: receipt.presentation.brief,
      full: receipt.presentation.full,
      initialMode,
    });
    this.#heading = heading;
    this.#dim = dim;
    this.#requestRender = requestRender;
    this.#done = done;
    this.#onDisplayed = onDisplayed;
  }

  get mode() {
    return this.#viewport.mode;
  }

  render(width) {
    const safeWidth = Math.max(1, Number.isFinite(width) ? Math.floor(width) : 1);
    const body = new Text(this.#viewport.currentFrame(), 0, 0);
    const lines = body.render(Math.max(1, safeWidth - 2));
    this.#body = body;
    this.#viewport.setViewportRows(Math.max(1, terminalRows() - 5));
    this.#viewport.setRenderedLineCount(lines.length);
    const range = this.#viewport.visibleRange();
    const mode = this.#viewport.mode === "full" ? "details" : "brief";
    const footer = `${range.start + 1}–${Math.min(range.end, range.total)}/${range.total}  ` +
      "↑↓ PgUp/PgDn scroll · d details · b back · q/Esc close · Preview only";
    const output = [
      truncateToWidth(`${this.#heading} · ${mode}`, safeWidth),
      "",
      ...lines.slice(range.start, range.end).map((line) => ` ${line}`),
      "",
      truncateToWidth(this.#dim(footer), safeWidth),
    ];
    if (!this.#displayed) {
      this.#displayed = true;
      this.#onDisplayed?.();
    }
    return output;
  }

  handleInput(data) {
    if (data === "q" || matchesKey(data, "escape") || matchesKey(data, "ctrl+c")) {
      this.#done();
      return;
    }
    if (data === "d") {
      this.#viewport.showDetails();
    } else if (data === "b") {
      this.#viewport.showBrief();
    } else if (data === "j" || matchesKey(data, "down")) {
      this.#viewport.scrollBy(1);
    } else if (data === "k" || matchesKey(data, "up")) {
      this.#viewport.scrollBy(-1);
    } else if (data === " " || matchesKey(data, "pageDown")) {
      this.#viewport.scrollBy(this.#viewport.pageSize);
    } else if (matchesKey(data, "pageUp")) {
      this.#viewport.scrollBy(-this.#viewport.pageSize);
    } else if (matchesKey(data, "home")) {
      this.#viewport.scrollBy(-this.#viewport.offset);
    } else if (matchesKey(data, "end")) {
      const range = this.#viewport.visibleRange();
      this.#viewport.scrollBy(range.total);
    } else {
      return;
    }
    this.#requestRender();
  }

  invalidate() {
    this.#body?.invalidate();
  }
}
