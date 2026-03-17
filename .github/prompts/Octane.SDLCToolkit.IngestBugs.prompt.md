---
agent: SdlcSpecialist
description: Ingest historical bugs into the Regression Oracle for pattern learning and risk prediction
tools: ['runCommands', 'search', 'edit']
---
# Ingest Bugs from Source

Help the user ingest historical bugs into the Regression Oracle knowledge base.

## Instructions

1. Determine the bug source:
   - **JSON file**: Ready to ingest
   - **GitHub Issues**: Need to export first
   - **JIRA**: Need to export to JSON
   - **Other**: Provide JSON format guidance

2. If JSON file is ready:
   ```bash
   sdlc-toolkit oracle-ingest <json-file>
   ```

3. If exporting from GitHub, help create the export:
   ```bash
   gh issue list --label bug --json number,title,body,labels,createdAt,closedAt,assignees --limit 100 > bugs.json
   ```
   Then transform to expected format.

4. Verify ingestion:
   ```bash
   sdlc-toolkit oracle-summary
   ```

## Expected JSON Format

```json
{
  "bugs": [
    {
      "id": "BUG-001",
      "title": "Short description of the bug",
      "description": "Detailed description",
      "severity": "low|medium|high|critical",
      "status": "open|in_progress|resolved|closed",
      "created_at": "2024-01-15T10:30:00Z",
      "resolved_at": "2024-01-16T14:00:00Z",
      "affected_files": ["src/path/file.py"],
      "component": "component-name",
      "root_cause": "What caused the bug",
      "root_cause_category": "validation|null_pointer|race_condition|etc",
      "labels": ["bug", "security"],
      "assignees": ["username"]
    }
  ]
}
```

## Key Fields for Better Predictions

| Field | Importance | Why |
|-------|------------|-----|
| affected_files | Critical | Links bugs to code locations |
| root_cause_category | High | Enables pattern detection |
| component | Medium | Groups related bugs |
| severity | Medium | Prioritizes risk assessment |

## Variables

- `{source}` (string, required): Bug source — one of `json`, `github`, or `jira`
- `{file_path}` (string, required): Path to JSON file containing bug data
