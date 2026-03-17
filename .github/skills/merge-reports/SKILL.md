---
name: merge-reports
description: Aggregates code review results from multiple batch reviews into a unified final report. Use this skill after all review batches complete to produce the final JSON and Markdown report.
---

# Merge Reports Skill

See [`scripts/merge_reports.py`](scripts/merge_reports.py) for the implementation.

Aggregates code review results from multiple batch reviews into a unified final report.

## When to Use

Use this skill **after all code review agent batches complete**. Each batch produces a
per-batch JSON result file; this skill merges them into a single final report.

## How to Invoke

```bash
python .github/skills/merge-reports/scripts/merge_reports.py --input-dir <batch_results_dir> --output-dir <output_dir>
```

Alternative — pass an explicit file list instead of a directory:

```bash
python .github/skills/merge-reports/scripts/merge_reports.py --input-files <file1.json> <file2.json> ... --output-dir <output_dir>
```

| Argument | Required | Description |
|---|---|---|
| `--input-dir` | One of `--input-dir` or `--input-files` | Directory containing batch result JSON files. |
| `--input-files` | One of `--input-dir` or `--input-files` | Explicit list of batch result JSON file paths. |
| `--output-dir` | No (default `.`) | Directory where output files are written. |

> **⚠️ Important**: When using `--input-dir`, the directory must contain **only** batch result JSON files. The script collects all `*.json` files in the directory. If non-batch JSON files (such as filter output or batch configuration) are present, they will be incorrectly processed as batch results. Use `--input-files` for explicit control over which files are merged, or ensure batch results are written to a dedicated directory.

## Input Format

Each per-batch JSON file must contain the following fields:

```json
{
  "guidelines_reviewed": ["path/to/guideline1.md", "path/to/guideline2.md"],
  "files_reviewed": ["src/foo.py", "src/bar.py"],
  "violations": [
    {
      "file_name": "src/foo.py",
      "startline": "10",
      "startrow": "1",
      "endline": "15",
      "endrow": "1",
      "detection": "Detection details",
      "violation": "Violation description",
      "guideline": "path/to/guideline1.md",
      "suggestion": "Suggested fix",
      "severity": "High"
    }
  ],
  "non_violations": [
    {
      "file_name": "src/bar.py",
      "reason": "No issues found"
    }
  ],
  "error": null
}
```

## Output Files

| File | Description |
|---|---|
| `final-review.json` | Machine-readable combined results (aggregated JSON). |
| `final-review-report.md` | Human-readable Markdown report with summary, tables, and details. |

## Example Usage in a Pipeline

```yaml
steps:
  # ... earlier steps run batch reviews and write results to ./batch-results/ ...

  - name: Merge review reports
    run: |
      python .github/skills/merge-reports/scripts/merge_reports.py \
        --input-dir ./batch-results \
        --output-dir ./review-output

  - name: Upload final report
    uses: actions/upload-artifact@v4
    with:
      name: final-review
      path: ./review-output/
```
