import { Text, matchesKey, replaceTabs, truncateToWidth, visibleWidth } from "@oh-my-pi/pi-tui";
import { ProposalViewport } from "./viewer-core.js";

function terminalRows() {
  const rows = Number(process.stdout?.rows);
  return Number.isFinite(rows) && rows > 0 ? rows : 24;
}

export class ProposalViewer {
  #viewport;
  #heading;
  #dim;
  #border;
  #requestRender;
  #done;
  #onDisplayed;
  #displayed = false;
  #body;

  constructor({ receipt, initialMode = "brief", heading, border, dim, requestRender, done, onDisplayed }) {
    this.#viewport = new ProposalViewport({
      brief: receipt.presentation.brief,
      full: receipt.presentation.full,
      initialMode,
    });
    this.#heading = heading;
    this.#border = border;
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
    const framed = safeWidth >= 8;
    const contentWidth = framed ? safeWidth - 4 : safeWidth;
    const body = new Text(this.#viewport.currentFrame(), 0, 0);
    const lines = body.render(contentWidth);
    this.#body = body;
    // A host pane may be shorter than the PTY-reported row count. Keep the entire
    // window (including its header and controls) inside a usable split pane.
    this.#viewport.setViewportRows(Math.max(1, Math.min(20, terminalRows() - (framed ? 6 : 3))));
    this.#viewport.setRenderedLineCount(lines.length);
    const range = this.#viewport.visibleRange();
    const mode = this.#viewport.mode === "full" ? "full" : "brief";
    const position = `${range.start + 1}–${Math.min(range.end, range.total)}/${range.total}`;
    const controls = `${position} · q close · d full · b brief`;
    const extendedControls = `${controls} · ↑↓/PgUp/PgDn`;
    const footer = visibleWidth(extendedControls) <= contentWidth ? extendedControls : controls;
    let output;
    if (framed) {
      const label = truncateToWidth(`${this.#heading} · read-only · ${mode}`, safeWidth - 5);
      const rule = "─".repeat(Math.max(0, safeWidth - 5 - visibleWidth(label)));
      const row = (content) => {
        const text = truncateToWidth(replaceTabs(content), contentWidth);
        return `${this.#border("│")} ${text}${" ".repeat(Math.max(0, contentWidth - visibleWidth(text)))} ${this.#border("│")}`;
      };
      output = [
        this.#border("╭─ ") + label + this.#border(` ${rule}╮`),
        row(""),
        ...lines.slice(range.start, range.end).map(row),
        row(""),
        row(this.#dim(footer)),
        this.#border(`╰${"─".repeat(safeWidth - 2)}╯`),
      ];
    } else {
      output = [
        truncateToWidth(this.#heading, safeWidth),
        ...lines.slice(range.start, range.end).map((line) => truncateToWidth(replaceTabs(line), safeWidth)),
        truncateToWidth(this.#dim(footer), safeWidth),
      ];
    }
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
