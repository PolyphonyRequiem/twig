#!/usr/bin/env python3
"""Merge batch code-review results into a unified final report.

Reads per-batch JSON result files produced by code-review agents and
generates:
  - final-review.json   (machine-readable aggregated results)
  - final-review-report.md (human-readable Markdown report)

Usage:
    python merge_reports.py --input-dir <dir> --output-dir <dir>
    python merge_reports.py --input-files f1.json f2.json --output-dir <dir>
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from datetime import datetime, timezone
from pathlib import Path

logger = logging.getLogger("merge-reports")


# ---------------------------------------------------------------------------
# Aggregation helpers
# ---------------------------------------------------------------------------

def _collect_input_files(args: argparse.Namespace) -> list[Path]:
    """Return the list of JSON files to process."""
    if args.input_files:
        paths = [Path(f) for f in args.input_files]
    elif args.input_dir:
        input_dir = Path(args.input_dir)
        if not input_dir.is_dir():
            logger.error("Input directory does not exist: %s", input_dir)
            sys.exit(1)
        paths = sorted(input_dir.glob("*.json"))
    else:
        logger.error("Either --input-dir or --input-files must be provided")
        sys.exit(1)

    if not paths:
        logger.error("No JSON files found to process")
        sys.exit(1)

    return paths


def _load_batch(path: Path) -> dict | None:
    """Load a single batch JSON file, returning *None* on error."""
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        logger.info("Loaded %s", path)
        return data
    except (json.JSONDecodeError, OSError) as exc:
        logger.warning("Failed to load %s: %s", path, exc)
        return None


def _aggregate(batches: list[dict]) -> dict:
    """Merge a list of batch dicts into a single aggregated dict."""
    guidelines: set[str] = set()
    files: set[str] = set()
    violations: list[dict] = []
    non_violations: list[dict] = []
    errors: list[str] = []

    for batch in batches:
        guidelines.update(batch.get("guidelines_reviewed", []))
        files.update(batch.get("files_reviewed", []))
        for item in batch.get("violations", []):
            if isinstance(item, dict):
                violations.append(item)
            else:
                logger.warning("Skipping non-dict violation entry in batch: %s", type(item).__name__)
        for item in batch.get("non_violations", []):
            if isinstance(item, dict):
                non_violations.append(item)
            else:
                logger.warning("Skipping non-dict non_violation entry in batch: %s", type(item).__name__)
        err = batch.get("error")
        if err:
            errors.append(err)

    result: dict = {
        "guidelines_reviewed": sorted(guidelines),
        "files_reviewed": sorted(files),
        "violations": violations,
        "non_violations": non_violations,
    }

    if errors:
        result["errors"] = errors

    return result


# ---------------------------------------------------------------------------
# Report generation
# ---------------------------------------------------------------------------

_SEVERITY_ORDER = ["Critical", "High", "Medium", "Low", "Informational"]
_MAX_VIOLATION_TEXT_LEN = 80
_MAX_REASON_TEXT_LEN = 100


def _basename(value: str | None, default: str = "-") -> str:
    """Extract the basename from a path string, or return *default*."""
    return Path(str(value)).name if value else default


def _format_line_range(violation: dict, default: str = "-") -> str:
    """Format startline/endline from a violation dict into a display string."""
    startline = violation.get("startline") or ""
    endline = violation.get("endline") or ""
    sl, el = str(startline), str(endline)
    if sl and el and sl != el:
        return f"{sl}-{el}"
    return sl if sl else default


def _render_violation_table(violations: list[dict]) -> list[str]:
    """Render a Markdown table of violations (header + rows)."""
    rows: list[str] = []
    rows.append("| # | File | Lines | Guideline | Violation |")
    rows.append("|---|------|-------|-----------|-----------|")
    for idx, v in enumerate(violations, 1):
        file_name = _basename(v.get("file_name"))
        guideline_name = _basename(v.get("guideline"))
        violation_text = (v.get("violation") or "").replace("|", "\\|")[:_MAX_VIOLATION_TEXT_LEN]
        line_info = _format_line_range(v)
        rows.append(f"| {idx} | {file_name} | {line_info} | {guideline_name} | {violation_text} |")
    return rows


def _generate_markdown(aggregated: dict, batch_count: int, total_count: int = 0, failed_paths: list[str] | None = None) -> str:
    """Produce a human-readable Markdown report string."""
    lines: list[str] = []

    guidelines = aggregated["guidelines_reviewed"]
    files = aggregated["files_reviewed"]
    violations = aggregated["violations"]
    non_violations = aggregated["non_violations"]
    errors = aggregated.get("errors", [])

    # -- Header -----------------------------------------------------------
    lines.append("# Final Review Report\n")
    lines.append(f"**Date**: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')}\n")

    # -- Summary ----------------------------------------------------------
    lines.append("## Summary\n")
    if total_count:
        lines.append(f"- **Batches Processed**: {batch_count} / {total_count}")
    else:
        lines.append(f"- **Batches Processed**: {batch_count}")
    if failed_paths:
        lines.append(f"- **Failed Batches**: {len(failed_paths)}")
    lines.append(f"- **Guidelines Reviewed**: {len(guidelines)}")
    lines.append(f"- **Files Reviewed**: {len(files)}")
    lines.append(f"- **Total Violations**: {len(violations)}")
    lines.append(f"- **Files Without Violations**: {len(non_violations)}")
    if errors:
        lines.append(f"- **Errors Encountered**: {len(errors)}")
    lines.append("")

    # -- Guidelines reviewed ----------------------------------------------
    if guidelines:
        lines.append("## Guidelines Reviewed\n")
        for g in guidelines:
            lines.append(f"- {g}")
        lines.append("")

    # -- Files reviewed ---------------------------------------------------
    if files:
        lines.append("## Files Reviewed\n")
        for f in files:
            lines.append(f"- {f}")
        lines.append("")

    # -- Violations by severity -------------------------------------------
    if violations:
        lines.append("## Violations by Severity\n")
        known_lower = {s.casefold() for s in _SEVERITY_ORDER}
        for severity in _SEVERITY_ORDER:
            sev_violations = [
                v for v in violations
                if (v.get("severity") or "").casefold() == severity.casefold()
            ]
            if not sev_violations:
                continue
            lines.append(f"### {severity} ({len(sev_violations)})\n")
            lines.extend(_render_violation_table(sev_violations))
            lines.append("")

        # Collect violations with severities not in _SEVERITY_ORDER
        other_violations = [
            v for v in violations
            if (v.get("severity") or "").casefold() not in known_lower
        ]
        if other_violations:
            lines.append(f"### Other ({len(other_violations)})\n")
            lines.extend(_render_violation_table(other_violations))
            lines.append("")

        # -- Detailed violations ------------------------------------------
        lines.append("## Detailed Violations\n")
        for idx, v in enumerate(violations, 1):
            lines.append(f"### Violation {idx}\n")
            lines.append(f"- **Severity**: {v.get('severity') or 'N/A'}")
            lines.append(f"- **File**: {v.get('file_name') or 'N/A'}")
            lines.append(f"- **Lines**: {_format_line_range(v, default='N/A')}")
            lines.append(f"- **Guideline**: {v.get('guideline') or 'N/A'}")
            lines.append(f"\n**[DETECTION]**: {v.get('detection') or 'N/A'}")
            lines.append(f"\n**[VIOLATION]**: {v.get('violation') or 'N/A'}")
            suggestion = v.get("suggestion")
            if suggestion:
                lines.append(f"\n**[SUGGESTION]**:\n```\n{suggestion}\n```")
            lines.append("")
    else:
        lines.append("## No Violations Found\n")
        lines.append("The code review did not find any violations against the provided guidelines.\n")

    # -- Non-violations ---------------------------------------------------
    if non_violations:
        lines.append("## Files Without Violations\n")
        lines.append("| File | Reason |")
        lines.append("|------|--------|")
        for nv in non_violations:
            file_name = _basename(nv.get("file_name"))
            reason = (nv.get("reason") or "").replace("|", "\\|")[:_MAX_REASON_TEXT_LEN]
            lines.append(f"| {file_name} | {reason} |")
        lines.append("")

    # -- Errors -----------------------------------------------------------
    if errors or failed_paths:
        lines.append("## Errors Encountered\n")
        for err in errors:
            lines.append(f"- {err}")
        if failed_paths:
            lines.append("")
            lines.append("**Failed batch files**:\n")
            for fp in failed_paths:
                lines.append(f"- `{fp}`")
        lines.append("")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# File output
# ---------------------------------------------------------------------------

def _write_outputs(aggregated: dict, batch_count: int, output_dir: Path, total_count: int = 0, failed_paths: list[str] | None = None) -> tuple[Path, Path]:
    """Write final-review.json and final-review-report.md to *output_dir*."""
    output_dir.mkdir(parents=True, exist_ok=True)

    output_data = {**aggregated}
    if failed_paths:
        output_data["failed_batches"] = failed_paths

    json_path = output_dir / "final-review.json"
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(output_data, fh, indent=2)
    logger.info("Wrote %s", json_path)

    md_path = output_dir / "final-review-report.md"
    md_path.write_text(_generate_markdown(aggregated, batch_count, total_count=total_count, failed_paths=failed_paths), encoding="utf-8")
    logger.info("Wrote %s", md_path)

    return json_path, md_path


# ---------------------------------------------------------------------------
# CLI entry-point
# ---------------------------------------------------------------------------

def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Merge batch code-review JSON results into a unified report."
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument(
        "--input-dir",
        help="Directory containing batch result JSON files.",
    )
    group.add_argument(
        "--input-files",
        nargs="+",
        help="Explicit list of batch result JSON files.",
    )
    parser.add_argument(
        "--output-dir",
        default=".",
        help="Directory for output files (default: current directory).",
    )
    return parser


def main() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(levelname)s: %(message)s",
        stream=sys.stderr,
    )

    args = _build_parser().parse_args()
    input_files = _collect_input_files(args)

    # Load batches
    batches: list[dict] = []
    failed_paths: list[str] = []
    for path in input_files:
        data = _load_batch(path)
        if data is not None:
            batches.append(data)
        else:
            failed_paths.append(str(path))

    if not batches:
        logger.error("No valid batch results loaded — aborting")
        sys.exit(1)

    # Aggregate
    aggregated = _aggregate(batches)

    # Write outputs
    output_dir = Path(args.output_dir)
    json_path, md_path = _write_outputs(aggregated, len(batches), output_dir, total_count=len(input_files), failed_paths=failed_paths)

    # Summary to stdout
    violations = aggregated["violations"]
    print(
        f"Merge complete: {len(batches)} batches, "
        f"{len(aggregated['guidelines_reviewed'])} guidelines, "
        f"{len(aggregated['files_reviewed'])} files, "
        f"{len(violations)} violations."
    )
    print(f"  -> {json_path}")
    print(f"  -> {md_path}")


if __name__ == "__main__":
    main()
