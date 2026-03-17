# MSTest v4 Unit Testing

This scenario installs the **`mstest-v4` Agent Skill** into your repo under `.github/skills/mstest-v4/`. The skill provides guidance for writing and migrating .NET unit tests using MSTest v4 best practices, including analyzer fixes and safe parallelization patterns.

## When to Use

- Writing new MSTest v4 unit tests with modern assertion APIs
- Migrating test code from MSTest v2/v3 to MSTest v4
- Fixing MSTest analyzer warnings (`MSTEST0001`–`MSTEST0045`)
- Configuring parallel test execution while avoiding race conditions

## Prerequisites

- VS Code setting `chat.useAgentSkills` enabled
- A repo containing .NET test projects using MSTest (or being migrated to MSTest v4)

## What Gets Installed

- `.github/skills/mstest-v4/SKILL.md`
- `.github/skills/mstest-v4/references/*.md`

## Using the Skill

After installing this scenario, open GitHub Copilot Chat and ask questions like:

- “Migrate these tests from MSTest v2 to v4”
- “Fix this MSTest analyzer warning: MSTEST0017”
- “Show the correct MSTest v4 pattern for testing exceptions in async methods”
- “How should I set up safe parallelization for these tests?”

The skill includes detailed references:

- `references/migration.md`
- `references/analyzers.md`
- `references/assertions.md`
- `references/parallelization.md`
