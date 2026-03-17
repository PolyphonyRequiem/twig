---
name: batch-files
description: Groups file-guideline filter results into optimized review batches. Use this skill after the filter stage to create batches for parallel code review. Invoke the batch_files.py script to process filter output.
---

# Batch Files Skill

See [`scripts/batch_files.py`](scripts/batch_files.py) for the implementation.

## When to Use

Use this skill **after the filter agent** produces its filter output JSON. The filter stage maps each guideline to glob patterns and optional content regexes. This skill takes that output, resolves matching files against the repository, and groups everything into optimally sized batches for parallel code review.

## How to Invoke

```bash
python .github/skills/batch-files/scripts/batch_files.py \
  --input <filter_output.json> \
  --output <batches.json> \
  [--repo-path <repository_root>] \
  [--max-batch-size 10] \
  [--max-guidelines-per-batch 10]
```

### Arguments

| Argument | Required | Default | Description |
|---|---|---|---|
| `--input` | Yes | — | Path to the filter output JSON file |
| `--output` | Yes | — | Path to write the batches JSON output |
| `--repo-path` | No | `.` | Repository root directory for resolving glob patterns |
| `--max-batch-size` | No | `10` | Maximum number of files per batch |
| `--max-guidelines-per-batch` | No | `10` | Maximum number of guidelines per batch |

## Input Format

The input JSON is an object keyed by guideline filename. Each value contains:

- `glob_patterns`: array of glob pattern strings to match files
- `content_regex`: array of regex strings for content-level filtering

Guideline entries with a `null` value (from diff-mode filtering) are skipped.

```json
{
  "error-handling.md": {
    "glob_patterns": ["src/**/*.py", "lib/**/*.py"],
    "content_regex": ["raise\\s+\\w+Error", "except\\s+"]
  },
  "naming-conventions.md": {
    "glob_patterns": ["**/*.ts", "**/*.tsx"],
    "content_regex": []
  },
  "deleted-guideline.md": null
}
```

## Output Format

The output JSON contains three top-level keys:

- **`configuration`**: the batching parameters used (max_batch_size, max_guidelines_per_batch)
- **`statistics`**: summary metrics (total_batches, total_files, total_guidelines, avg/min/max files per batch)
- **`batches`**: array of batch objects

Each batch object contains:

| Field | Type | Description |
|---|---|---|
| `batch_id` | string | Unique batch identifier (e.g. `batch_001`) |
| `files` | array | List of matched file paths |
| `guidelines` | array | List of guideline names for this batch |
| `file_count` | integer | Number of files in the batch |
| `guideline_count` | integer | Number of guidelines in the batch |

```json
{
  "configuration": {
    "max_batch_size": 10,
    "max_guidelines_per_batch": 10
  },
  "statistics": {
    "total_batches": 3,
    "total_files": 25,
    "total_guidelines": 4,
    "avg_files_per_batch": 8.3,
    "avg_guidelines_per_batch": 2.0,
    "min_files": 5,
    "max_files": 10
  },
  "batches": [
    {
      "batch_id": "batch_001",
      "files": ["src/auth.py", "src/utils.py"],
      "guidelines": ["error-handling.md", "naming-conventions.md"],
      "file_count": 2,
      "guideline_count": 2
    }
  ]
}
```

## Example Pipeline Usage

```bash
# 1. Filter stage produces guideline-to-pattern mapping
python .github/skills/filter-guidelines/filter.py --output filter_output.json

# 2. Batch stage groups files into review batches
python .github/skills/batch-files/scripts/batch_files.py \
  --input filter_output.json \
  --output batches.json \
  --repo-path . \
  --max-batch-size 10 \
  --max-guidelines-per-batch 10

# 3. Review stage processes each batch in parallel
python .github/skills/review/review.py --batches batches.json
```
