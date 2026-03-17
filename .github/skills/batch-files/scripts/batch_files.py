#!/usr/bin/env python3
"""Standalone batch-files skill for grouping filter output into review batches.

Reads filter-stage JSON (guideline -> {glob_patterns, content_regex}), resolves
matching files against the repository, and produces optimised review batches
respecting configurable size limits.

Usage:
    python batch_files.py --input <filter.json> --output <batches.json> \
        [--repo-path .] [--max-batch-size 10] [--max-guidelines-per-batch 10]
"""

from __future__ import annotations

import argparse
import json
import logging
import re
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any, Dict, FrozenSet, List, Set

logging.basicConfig(
    level=logging.INFO,
    format="%(levelname)s: %(message)s",
    stream=sys.stderr,
)
logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# File matching helpers
# ---------------------------------------------------------------------------

def _glob_match_files(repo_path: Path, pattern: str) -> List[str]:
    """Return repo-relative paths matching *pattern* under *repo_path*.

    Supports recursive ``**`` globs via :pymeth:`pathlib.Path.glob`.
    """
    matched: List[str] = []
    try:
        for p in repo_path.glob(pattern):
            if p.is_file():
                try:
                    matched.append(p.relative_to(repo_path).as_posix())
                except ValueError:
                    continue
    except Exception as exc:
        logger.warning("Glob pattern '%s' failed: %s", pattern, exc)
    return matched


def _file_matches_content_regex(
    file_path: Path, compiled_regexes: List[re.Pattern[str]]
) -> bool:
    """Return True if file content matches at least one compiled regex."""
    try:
        content = file_path.read_text(encoding="utf-8", errors="replace")
    except Exception as exc:
        logger.debug("Cannot read %s for regex matching: %s", file_path, exc)
        return False
    return any(rx.search(content) for rx in compiled_regexes)


# ---------------------------------------------------------------------------
# Core algorithm
# ---------------------------------------------------------------------------

def _apply_filters(
    guideline_filters: Dict[str, Any],
    repo_path: Path,
) -> Dict[str, Set[str]]:
    """Apply glob + content-regex filters and return guideline -> matched files."""

    guideline_files: Dict[str, Set[str]] = {}

    for guideline, gf in guideline_filters.items():
        # Skip null entries (diff-mode placeholders)
        if gf is None:
            logger.info("Skipping null guideline entry: '%s'", guideline)
            continue

        glob_patterns: List[str] = gf.get("glob_patterns", [])
        content_regex: List[str] = gf.get("content_regex", [])
        if not isinstance(glob_patterns, list):
            logger.warning(
                "Guideline '%s': glob_patterns is not a list, skipping",
                guideline,
            )
            glob_patterns = []
        if not isinstance(content_regex, list):
            logger.warning(
                "Guideline '%s': content_regex is not a list, skipping",
                guideline,
            )
            content_regex = []
        if not glob_patterns and not content_regex:
            logger.warning("Guideline '%s': no glob patterns or content regexes defined", guideline)
            continue

        matched_files: Set[str] = set()

        for pattern in glob_patterns:
            if not pattern:
                continue
            # Handle compound " AND " filters
            if " AND " in pattern:
                sub_patterns = [p.strip() for p in pattern.split(" AND ") if p.strip()]
                if not sub_patterns:
                    continue
                sub_result: Set[str] | None = None
                for sp in sub_patterns:
                    matches = set(_glob_match_files(repo_path, sp))
                    sub_result = matches if sub_result is None else sub_result & matches
                if sub_result:
                    matched_files.update(sub_result)
            else:
                matched_files.update(_glob_match_files(repo_path, pattern))

        # Content-regex filtering (AND with glob results)
        if matched_files and content_regex:
            compiled: List[re.Pattern[str]] = []
            for rx in content_regex:
                try:
                    compiled.append(re.compile(rx))
                except re.error as exc:
                    logger.warning(
                        "Skipping invalid content regex for '%s': '%s' — %s",
                        guideline, rx, exc,
                    )
            if compiled:
                before = len(matched_files)
                matched_files = {
                    fp
                    for fp in matched_files
                    if _file_matches_content_regex(repo_path / fp, compiled)
                }
                logger.info(
                    "Content-regex filter for '%s': %d → %d files",
                    guideline, before, len(matched_files),
                )

        if matched_files:
            guideline_files[guideline] = matched_files
            logger.info("Guideline '%s': %d files matched", guideline, len(matched_files))
        else:
            logger.warning("Guideline '%s': no files matched", guideline)

    return guideline_files


def _invert_mapping(guideline_files: Dict[str, Set[str]]) -> Dict[str, Set[str]]:
    """Invert guideline->files to file->guidelines."""

    file_to_guidelines: Dict[str, Set[str]] = defaultdict(set)
    for guideline, files in guideline_files.items():
        for f in files:
            file_to_guidelines[f].add(guideline)
    return dict(file_to_guidelines)


def _group_by_signature(
    file_to_guidelines: Dict[str, Set[str]],
) -> Dict[FrozenSet[str], List[str]]:
    """Group files by their guideline signature (frozenset of guidelines)."""

    sig_to_files: Dict[FrozenSet[str], List[str]] = defaultdict(list)
    for f, guidelines in file_to_guidelines.items():
        sig_to_files[frozenset(guidelines)].append(f)
    return dict(sig_to_files)


def _create_initial_batches(
    sig_to_files: Dict[FrozenSet[str], List[str]],
    max_batch_size: int,
    max_guidelines_per_batch: int,
) -> List[Dict[str, Any]]:
    """Create initial batches from signature groups respecting size limits."""

    batches: List[Dict[str, Any]] = []
    batch_number = 1

    # Process larger/more complex signatures first
    sorted_sigs = sorted(
        sig_to_files.items(),
        key=lambda x: (len(x[0]), len(x[1])),
        reverse=True,
    )

    for signature, files in sorted_sigs:
        guidelines_list = sorted(signature)
        files_list = sorted(files)

        # Split guidelines into chunks if needed
        if len(guidelines_list) > max_guidelines_per_batch:
            for gi in range(0, len(guidelines_list), max_guidelines_per_batch):
                g_chunk = guidelines_list[gi : gi + max_guidelines_per_batch]
                for fi in range(0, len(files_list), max_batch_size):
                    f_chunk = files_list[fi : fi + max_batch_size]
                    batches.append({
                        "batch_id": f"batch_{batch_number:03d}",
                        "files": f_chunk,
                        "guidelines": g_chunk,
                    })
                    batch_number += 1
        else:
            for fi in range(0, len(files_list), max_batch_size):
                f_chunk = files_list[fi : fi + max_batch_size]
                batches.append({
                    "batch_id": f"batch_{batch_number:03d}",
                    "files": f_chunk,
                    "guidelines": guidelines_list,
                })
                batch_number += 1

    return batches


def _merge_batches(
    batches: List[Dict[str, Any]],
    max_batch_size: int,
    max_guidelines_per_batch: int,
) -> List[Dict[str, Any]]:
    """Greedy first-fit-decreasing bin-packing to reduce batch count."""

    if len(batches) <= 1:
        return batches

    original_count = len(batches)

    # Sort by descending weight
    sorted_batches = sorted(
        batches,
        key=lambda b: (len(b["files"]) * len(b["guidelines"]), len(b["guidelines"])),
        reverse=True,
    )

    bins: List[Dict[str, set]] = []

    for batch in sorted_batches:
        b_files = set(batch["files"])
        b_guidelines = set(batch["guidelines"])
        merged = False

        for bin_entry in bins:
            combined_files = bin_entry["files"] | b_files
            combined_guidelines = bin_entry["guidelines"] | b_guidelines
            if (
                len(combined_files) <= max_batch_size
                and len(combined_guidelines) <= max_guidelines_per_batch
            ):
                bin_entry["files"] = combined_files
                bin_entry["guidelines"] = combined_guidelines
                merged = True
                break

        if not merged:
            bins.append({"files": b_files, "guidelines": b_guidelines})

    merged_batches: List[Dict[str, Any]] = []
    for idx, bin_entry in enumerate(bins, 1):
        merged_batches.append({
            "batch_id": f"batch_{idx:03d}",
            "files": sorted(bin_entry["files"]),
            "guidelines": sorted(bin_entry["guidelines"]),
        })

    logger.info("Batch merging: %d → %d batches", original_count, len(merged_batches))
    return merged_batches


def _compute_statistics(batches: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Compute summary statistics for the batch set."""

    file_counts = [len(b["files"]) for b in batches]
    all_files: Set[str] = set()
    all_guidelines: Set[str] = set()
    for b in batches:
        all_files.update(b["files"])
        all_guidelines.update(b["guidelines"])

    return {
        "total_batches": len(batches),
        "total_files": len(all_files),
        "total_guidelines": len(all_guidelines),
        "avg_files_per_batch": round(sum(file_counts) / len(batches), 2) if batches else 0,
        "avg_guidelines_per_batch": round(
            sum(len(b["guidelines"]) for b in batches) / len(batches), 2
        ) if batches else 0,
        "min_files": min(file_counts, default=0),
        "max_files": max(file_counts, default=0),
    }


# ---------------------------------------------------------------------------
# Main entry point
# ---------------------------------------------------------------------------

def run(
    input_path: str,
    output_path: str,
    repo_path: str = ".",
    max_batch_size: int = 10,
    max_guidelines_per_batch: int = 10,
) -> None:
    """Execute the full batching pipeline."""

    repo = Path(repo_path).resolve()
    if not repo.is_dir():
        raise FileNotFoundError(f"Repository path is not a directory: {repo}")

    # Read filter output
    try:
        with open(input_path, "r", encoding="utf-8") as f:
            guideline_filters: Dict[str, Any] = json.load(f)
    except (json.JSONDecodeError, OSError) as exc:
        raise ValueError(f"Failed to read input file '{input_path}': {exc}") from exc

    if not isinstance(guideline_filters, dict):
        raise ValueError("Input JSON must be an object keyed by guideline name")

    logger.info(
        "Processing %d guideline entries (repo: %s)", len(guideline_filters), repo
    )

    # Step 1: Apply filters
    guideline_files = _apply_filters(guideline_filters, repo)

    if not guideline_files:
        logger.warning("No files matched any guideline — writing empty batch output")

    # Step 2: Invert mapping
    file_to_guidelines = _invert_mapping(guideline_files)

    # Step 3: Group by guideline signature
    sig_to_files = _group_by_signature(file_to_guidelines)

    # Step 4: Create initial batches
    batches = _create_initial_batches(
        sig_to_files, max_batch_size, max_guidelines_per_batch
    )

    # Step 5: Merge/optimise batches
    batches = _merge_batches(batches, max_batch_size, max_guidelines_per_batch)

    # Step 6: Build output
    for b in batches:
        b["file_count"] = len(b["files"])
        b["guideline_count"] = len(b["guidelines"])

    output = {
        "configuration": {
            "max_batch_size": max_batch_size,
            "max_guidelines_per_batch": max_guidelines_per_batch,
        },
        "statistics": _compute_statistics(batches),
        "batches": batches,
    }

    # Write output
    try:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            json.dump(output, f, indent=2)
    except OSError as exc:
        raise OSError(f"Failed to write output file '{output_path}': {exc}") from exc

    logger.info(
        "Wrote %d batches (%d files, %d guidelines) to %s",
        output["statistics"]["total_batches"],
        output["statistics"]["total_files"],
        output["statistics"]["total_guidelines"],
        output_path,
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Batch filter output into optimised review batches.",
    )
    parser.add_argument(
        "--input", required=True, help="Path to filter output JSON file"
    )
    parser.add_argument(
        "--output", required=True, help="Path to write batches JSON output"
    )
    parser.add_argument(
        "--repo-path", default=".", help="Repository root directory (default: .)"
    )
    parser.add_argument(
        "--max-batch-size",
        type=int,
        default=10,
        help="Maximum files per batch (default: 10)",
    )
    parser.add_argument(
        "--max-guidelines-per-batch",
        type=int,
        default=10,
        help="Maximum guidelines per batch (default: 10)",
    )
    args = parser.parse_args()

    if args.max_batch_size < 1:
        parser.error("--max-batch-size must be at least 1")
    if args.max_guidelines_per_batch < 1:
        parser.error("--max-guidelines-per-batch must be at least 1")

    try:
        run(
            input_path=args.input,
            output_path=args.output,
            repo_path=args.repo_path,
            max_batch_size=args.max_batch_size,
            max_guidelines_per_batch=args.max_guidelines_per_batch,
        )
    except (ValueError, FileNotFoundError, OSError) as exc:
        logger.error("%s", exc)
        sys.exit(1)


if __name__ == "__main__":
    main()
