---
description: Update existing documentation with new insights, decisions, or gotchas
argument-hint: The name of the documentation to update (e.g., Service.Authentication)
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `Name` (string, required): The name of the documentation to update
- `UpdateType` (string, optional): Type of update - "insight", "decision", "gotcha", "scenario", "troubleshooting"
- `Content` (string, optional): The content to add

If you do not have a `Name`, ask the user which documentation they want to update.

## PRIMARY DIRECTIVE

Update existing documentation for `${input:Name}` with new information. This is a **targeted update** - do NOT regenerate the entire document. Only modify the relevant section.

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Read Existing Documentation**  
   Load the current documentation from `${config:documentation.output_path}/${input:Name}.doc.md`
   - If file doesn't exist, suggest using `/Octane.Doc.Create` first

2. **Identify Update Section**  
   Based on `${input:UpdateType}` or content analysis, determine target section:
   
   | Update Type | Target Section |
   |-------------|----------------|
   | insight | Key Insights |
   | decision | Design Decisions |
   | gotcha | Key Insights → ⚠️ Gotchas |
   | scenario | Common Scenarios |
   | troubleshooting | Common Scenarios → Troubleshooting |
   | performance | Key Insights → ⚡ Performance |
   | production | Key Insights → 🎯 Production Learnings |

3. **Clarify If Needed**  
   If `${input:UpdateType}` is not provided and content is ambiguous, ask:
   > "What type of update is this? Options: insight, decision, gotcha, scenario, troubleshooting, performance, production"

4. **Format Content**  
   Format the new content to match the existing section style:
   - Use consistent bullet points or table rows
   - Match emoji conventions if present
   - Include date if relevant (e.g., production learnings)

5. **Apply Update**  
   Use targeted file editing to:
   - Add new content to appropriate section
   - Preserve all other sections unchanged
   - Update "Last Updated" date at bottom

6. **Confirm Update**  
   Report what was changed:
   > "✓ Added [update type] to [section name] in `${input:Name}.doc.md`"

## EXAMPLE UPDATES

**Adding a gotcha:**
```
User: /Octane.Doc.Update Service.Auth add gotcha: JWT tokens aren't validated on WebSocket reconnect

Action: Add to Key Insights → ⚠️ Gotchas:
- JWT tokens aren't validated on WebSocket reconnect - must manually validate on reconnection handler
```

**Adding a design decision:**
```
User: /Octane.Doc.Update PaymentService add decision: Why Stripe over PayPal

Action: Add to Design Decisions:
**Why Stripe instead of PayPal?**
- **Decision:** Use Stripe as primary payment processor
- **Rationale:** [Ask user for details]
- **Tradeoffs:** [Ask user for details]
```

**Adding a production learning:**
```
User: /Octane.Doc.Update CacheService add: Redis connections timeout silently after 30s idle

Action: Add to Key Insights → 🎯 Production Learnings:
- Redis connections timeout silently after 30s idle - implement connection keep-alive or reconnect logic
```

## RULES

1. **Never regenerate entire document** - Only modify target section
2. **Preserve formatting** - Match existing style and conventions
3. **Ask for details** - If update needs more context (like design decision rationale), ask
4. **Update timestamp** - Always update "Last Updated" date
5. **Confirm changes** - Show what was added and where
