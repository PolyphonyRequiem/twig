export class ProposalViewport {
  #brief;
  #full;
  #mode;
  #offset = 0;
  #pageSize = 1;
  #renderedLineCount;

  constructor({ brief, full, initialMode = "brief" }) {
    if (typeof brief !== "string" || brief.length === 0) {
      throw new Error("Proposal viewer needs a non-empty brief frame.");
    }
    if (typeof full !== "string" || full.length === 0) {
      throw new Error("Proposal viewer needs a non-empty full frame.");
    }
    if (initialMode !== "brief" && initialMode !== "full") {
      throw new Error(`Unknown proposal viewer mode: ${initialMode}`);
    }
    this.#brief = brief;
    this.#full = full;
    this.#mode = initialMode;
  }

  get mode() {
    return this.#mode;
  }

  get offset() {
    return this.#offset;
  }

  get pageSize() {
    return this.#pageSize;
  }

  currentFrame() {
    return this.#mode === "full" ? this.#full : this.#brief;
  }

  currentLines() {
    return this.currentFrame().replace(/\r\n?/g, "\n").split("\n");
  }

  setViewportRows(rows) {
    this.#pageSize = Number.isFinite(rows) ? Math.max(1, Math.floor(rows)) : 1;
    this.#clampOffset();
  }

  setRenderedLineCount(count) {
    this.#renderedLineCount = Number.isFinite(count) ? Math.max(0, Math.floor(count)) : undefined;
    this.#clampOffset();
  }

  setMode(mode) {
    if (mode !== "brief" && mode !== "full") {
      throw new Error(`Unknown proposal viewer mode: ${mode}`);
    }
    if (this.#mode !== mode) {
      this.#mode = mode;
      this.#offset = 0;
      this.#renderedLineCount = undefined;
    }
    this.#clampOffset();
  }

  showBrief() {
    this.setMode("brief");
  }

  showDetails() {
    this.setMode("full");
  }

  scrollBy(delta) {
    if (!Number.isFinite(delta)) {
      return;
    }
    this.#offset += Math.trunc(delta);
    this.#clampOffset();
  }

  visibleRange(totalLines = this.#renderedLineCount ?? this.currentLines().length) {
    const total = Math.max(0, Math.floor(totalLines));
    const start = Math.min(this.#offset, Math.max(0, total - this.#pageSize));
    return {
      start,
      end: Math.min(total, start + this.#pageSize),
      total,
    };
  }

  #clampOffset() {
    const total = this.#renderedLineCount ?? this.currentLines().length;
    this.#offset = Math.max(0, Math.min(this.#offset, Math.max(0, total - this.#pageSize)));
  }
}
