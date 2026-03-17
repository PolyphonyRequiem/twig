---
description: Conduct structured interview to capture team knowledge and production learnings
argument-hint: What to capture knowledge for (optional - will prompt if not provided)
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `Name` (string, optional): What to capture knowledge for. If not provided, ask what to document.
- `DocumentPath` (string, optional): Path to existing documentation to update with insights.

## PRIMARY DIRECTIVE

Conduct a **structured interview** to capture team knowledge that cannot be found in code. This is the most valuable part of living documentation - the "why" behind decisions and the lessons learned from production.

## INTERVIEW PHILOSOPHY

Code shows **what** was built. Interviews capture:
- **Why** it was built that way
- **What went wrong** along the way
- **What would you change** knowing what you know now
- **What trips people up** when working with this code

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Identify Target**  
   If `${input:Name}` not provided, ask:
   > "What would you like to capture knowledge for?"

2. **Check Existing Documentation**  
   Look for existing docs at `${config:documentation.output_path}/${input:Name}.doc.md`
   - If exists: Interview will add to Key Insights section
   - If not exists: Suggest running `/Octane.Doc.Create` first, or proceed with standalone interview

3. **Pre-load Enterprise Context**  
   Before interviewing, use `work-iq/*` tools to gather background context:
   - Search for design docs, tech specs, and architecture discussions related to `${input:Name}`
   - Find meeting notes and email threads with decision context
   - Identify recent incidents or post-mortems
   
   Use this context to ask **more targeted follow-up questions** during the interview. For example, if a design doc mentions a trade-off, ask the interviewee to elaborate on their experience with that decision in practice.

4. **Conduct Interview**  
   Ask questions **ONE AT A TIME**. Wait for response before next question.
   
   Select 5-7 questions from the bank below based on the type of code being documented.

5. **Synthesize Responses**  
   After interview, organize responses into:
   - Design Decisions (with rationale)
   - Key Insights → Gotchas
   - Key Insights → Production Learnings
   - Key Insights → Performance

6. **Update Documentation**  
   Add synthesized insights to the documentation.
   If no existing doc, create a new insights file.

7. **Confirm Capture**  
   > "✓ Captured [N] insights from interview. Added to `${input:Name}.doc.md`:
   > - [X] design decisions
   > - [Y] gotchas  
   > - [Z] production learnings"

## QUESTION BANK

### Design Rationale (pick 1-2)
- "What was the hardest technical decision here? What were the alternatives?"
- "If you were starting over, what would you do differently?"
- "What constraints or requirements drove the current design?"
- "Why [specific pattern observed in code] instead of [common alternative]?"

### Edge Cases & Gotchas (pick 1-2)
- "What's the most confusing part of this code for new developers?"
- "Are there any non-obvious behaviors that surprise people?"
- "What configuration mistakes do people commonly make?"
- "Are there any 'magic values' or implicit assumptions in the code?"

### Production Experience (pick 1-2)
- "What was the worst bug or incident here?"
- "What monitoring or alerts did you wish you had earlier?"
- "What performance issues have you discovered in production?"
- "What's the most common support question about this?"

### Dependencies & Integration (pick 1-2)
- "What happens when [key dependency] fails or is slow?"
- "Are there any ordering dependencies or race conditions to watch for?"
- "What external services does this depend on, and what's the fallback?"

### Technical Debt (pick 1)
- "What would you refactor if you had a week?"
- "Are there any known workarounds or hacks in the code?"
- "What's the scariest part of the codebase to change?"

### Type-Specific Questions

**For ML/AI Code:**
- "How accurate is this in practice vs. training metrics?"
- "What inputs cause it to fail or degrade?"
- "How do you debug when predictions are wrong?"

**For Pipeline/Workflow Code:**
- "Where do jobs most commonly fail?"
- "How do you recover from partial failures?"
- "Are there any checkpoint or retry edge cases?"

**For API/Service Code:**
- "What are the most common client errors?"
- "How do you handle rate limiting or throttling?"
- "What authentication edge cases exist?"

**For Data/Storage Code:**
- "How does this scale with data volume?"
- "What data corruption scenarios have you seen?"
- "How do you handle schema changes?"

## INTERVIEW RULES

1. **One question at a time** - Don't overwhelm, wait for answers
2. **Follow up on interesting answers** - "Tell me more about that..."
3. **Capture specific examples** - "Can you give me a concrete example?"
4. **Note uncertainty** - If they say "I think...", mark as [unverified]
5. **Thank them** - End with appreciation for sharing knowledge

## OUTPUT FORMAT

After interview, format insights like this:

```markdown
## Key Insights (from interview with [Name] on [Date])

### ⚠️ Gotchas
- [Gotcha from interview with context]

### 🎯 Production Learnings  
- [Learning from interview with incident context if available]

### Design Decisions
**Why [Decision]?**
- **Rationale:** [From interview]
- **Context:** [Constraints mentioned]
- **Alternatives considered:** [What they said they rejected]
```
