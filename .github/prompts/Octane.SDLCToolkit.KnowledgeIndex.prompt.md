---
agent: SdlcSpecialist
description: Index repository for knowledge base search
tools: ['runCommands']
---

# Index Repository

Index a repository's documentation for semantic search.

## Instructions

When the user asks to index a repository or set up knowledge search:

1. **Index the repository**:
   ```bash
   sdlc-toolkit kb-index <path>
   ```

2. **Show what was indexed**:
   - Total artifacts
   - Types of content
   - Chunk count for search

3. **Explain next steps** (use kb-search to query)

## Example User Prompts

- "Index this repo for search"
- "Set up knowledge base"
- "Make this repo searchable"
- "Index our documentation"
