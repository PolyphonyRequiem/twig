---
agent: SdlcSpecialist
description: Search the knowledge base across code, enterprise docs, and indexed documentation
tools: ['runCommands', 'search', 'code-search/*', 'work-iq/*', 'ms-learn/*']
---

# Search Knowledge Base

Semantic search across indexed documentation and artifacts.

## Instructions

When the user asks to search documentation:

1. **Run search**:
   ```bash
   sdlc-toolkit kb-search "<query>"
   ```

2. **With path** (indexes first):
   ```bash
   sdlc-toolkit kb-search "<query>" --path ./repo
   ```

3. **Present results** with:
   - Title and type
   - Relevance score
   - Snippet preview

## Example User Prompts

- "Search for authentication docs"
- "Find info about the API"
- "What does our documentation say about caching?"
- "Search: deployment process"
