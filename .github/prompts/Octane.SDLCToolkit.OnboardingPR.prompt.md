---
agent: SdlcSpecialist
description: Create a PR with the generated onboarding guide for SME review.
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'todo', 'work-iq/*', 'ado/*']
model: Claude Opus 4.6 (copilot)
---

## PRIMARY DIRECTIVE

After generating an onboarding guide (via `/Octane.SDLCToolkit.RepoOverview`), create a **pull request** in the repository so subject matter experts can review, refine, and merge the documentation.

## Inputs

- `overview_path` (string, optional): Path to the onboarding guide file. Defaults to `${config.project.overview_path}` or `./docs/onboarding_guide.md`

## WORKFLOW STEPS

1. **Verify Onboarding Guide Exists**
   - Check that the onboarding guide file exists at `${config.project.overview_path}` (default: `./docs/onboarding_guide.md`)
   - If it doesn't exist, inform the user to run `/Octane.SDLCToolkit.RepoOverview` first

2. **Identify Reviewers**
   - Use the `work-iq/*` tools to discover the top subject matter experts for this repository:
     - Authors of design documents related to this repo
     - Frequent participants in architecture discussions
     - Recent active contributors
   - Use the `ado/*` tools to find:
     - Recent PR authors and reviewers
     - Work item assignees for this repo area
   - Select the top 3-5 most relevant reviewers

3. **Create the PR**
   - Create a new branch named `docs/onboarding-guide-{date}` (e.g., `docs/onboarding-guide-2026-02-13`)
   - Stage and commit the onboarding guide file with message: `docs: add auto-generated onboarding guide`
   - Push the branch and create a pull request:
     - **Title:** `docs: Auto-generated onboarding guide for <repo-name>`
     - **Description:** Include a summary explaining:
       - This guide was auto-generated using Octane's SDLC Toolkit
       - It combines codebase analysis with enterprise knowledge (design docs, meeting notes, work items)
       - SMEs should review for accuracy, add missing context, and approve
       - The guide will serve as the single entry point for new developers
     - **Reviewers:** Assign the discovered SMEs as reviewers
     - **Labels:** `documentation`, `onboarding`

4. **Report Results**
   - Display the PR URL to the user
   - List the assigned reviewers and why they were selected
   - Suggest next steps (e.g., "Once merged, new developers can use `/Octane.SDLCToolkit.OnboardingBuddy` for follow-up questions")

## EXECUTION

Use `git` commands via the execute tool to create the branch, commit, and push. Use `ado/*` tools or `gh` CLI to create the pull request depending on the repository host (ADO or GitHub).

### For ADO Repositories
```bash
git checkout -b docs/onboarding-guide-$(date +%Y-%m-%d)
git add docs/onboarding_guide.md
git commit -m "docs: add auto-generated onboarding guide"
git push -u origin HEAD
# Use ado/* tools to create the PR
```

### For GitHub Repositories
```bash
git checkout -b docs/onboarding-guide-$(date +%Y-%m-%d)
git add docs/onboarding_guide.md
git commit -m "docs: add auto-generated onboarding guide"
git push -u origin HEAD
gh pr create --title "docs: Auto-generated onboarding guide" --body "..." --reviewer reviewer1,reviewer2
```
