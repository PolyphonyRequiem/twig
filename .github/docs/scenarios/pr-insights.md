# PR Insights Scenario

> **Stop wondering why your PRs take forever.** Get AI-powered data-driven insights for your Azure DevOps pull requests in seconds.

PR Insights provides comprehensive pull request analysis to help engineers and teams understand their code review patterns, identify bottlenecks, and improve development velocity.

> **Report Viewer**: Some Azure Core teams have access to formatted reports at [https://aka.ms/pr-insights](https://aka.ms/pr-insights). If your team is not onboarded, discuss with your org leader to request onboarding.

## When to Use

- Your PRs are taking longer than expected and you want to understand why
- You need data-driven insights into team code review patterns
- Engineering managers need a team PR health dashboard
- You want to improve your code review skills with personalized feedback
- You need PR history or reviewer comments for a file before making changes
- You want to explore PR data interactively for custom analysis

## Prerequisites

### CloudMine Access (Required)

PR Insights queries Azure DevOps data stored in Microsoft's **CloudMine** Kusto cluster.

**[Request CloudMine Azure DevOps Data access here](https://coreidentity.microsoft.com/manage/entitlement/entitlement/cloudmineev-vysd)**

- Approval takes 2-3 business days
- **VPN must be connected** when using PR Insights

### MCP Server

- **pr-insights**: `https://app-prinsights-01-mcp.azurewebsites.net/`

## Workflows

1. Ensure VPN is connected and CloudMine access is approved
2. Open GitHub Copilot Chat in VS Code
3. Invoke the `@PRInsights` agent with a natural language query
4. Review the structured report with insights and recommendations
5. Run follow-up analyses as suggested by the agent

## What Can You Ask?

### 1. Why Are My PRs Taking So Long?

Understand your PR patterns and identify what's slowing you down.

```
@PRInsights Why do my-email@microsoft.com PRs take so long?
@PRInsights Why do my-email@microsoft.com PRs take so long in repositories 'Compute-CPlat-Core'?
@PRInsights Why do my-email@microsoft.com PRs take so long from 2025-01-01 to 2025-06-30?
```

**Insights Included:**
- PR Duration Analysis - patterns, outliers, what's stuck
- Low Value Comment Analysis - nit comments that slow you down
- PR Focus Analysis - are you trying too much in one PR?
- Author & Reviewer Responsiveness - who's the bottleneck?
- File Complexity Analysis - what's slowing reviewers down?

### 2. Deep Dive Into a Specific PR

Analyze a single PR from author or reviewer perspective.

```
@PRInsights Analyze https://msazure.visualstudio.com/.../pullrequest/123
@PRInsights Analyze PR #123 from reviewer perspective (email@microsoft.com)
```

**Insights Included:**
- PR Timeline - where did the time go?
- Comment Back-and-Forth - time for a quick call?
- Disruption Factor - did a late reviewer reset everything?
- Complexity metrics

### 3. Level Up Your Code Review Skills

Get personalized feedback on your code review patterns.

```
@PRInsights How do I improve my code review skills? (email@microsoft.com)
@PRInsights How do I improve my (email@microsoft.com) code review skills in repositories 'Compute-CPlat-Core'?
```

**Insights Included:**
- Engagement Continuity - do you ghost PRs mid-review?
- Thread Closure patterns - are you leaving threads hanging?
- Turnaround Time - how fast do you respond?
- Missed Issues Analysis - what % of bugs are you catching?

### 4. Team PR Health Dashboard

Perfect for Engineering Managers - see team velocity and blockers.

```
@PRInsights Team snapshot for manager@microsoft.com
@PRInsights Team snapshot for manager@microsoft.com with depth 2
@PRInsights Team snapshot for alice, bob, charlie, diana
```

**Insights Included:**
- Team Velocity Overview - PRs active, blocked, completed
- PR Current State - who's working vs waiting?
- Reviewer Capacity & Availability
- Cross-Team SME Engagement
- Convergence Health Check

### 5. Team Performance Analysis

Aggregate team metrics for performance reviews and process improvement.

```
@PRInsights Analyze team performance for manager@microsoft.com
@PRInsights Analyze team performance for alice, bob, charlie
```

### 6. PR History & Comments for File

Quick context before modifying a file.

```
@PRInsights Show PR history for MyClass.cs
@PRInsights Show comments for MyClass.cs in repository 'Compute-CPlat-Core'
@PRInsights Show comments for MyClass.cs. What do reviewers care about?
```

### 7. Explore PR Data

Interactive data exploration for custom AI analysis.

```
@PRInsights Explore PR data for email@microsoft.com
@PRInsights Explore PR data for email@microsoft.com from 2025-01-01 to 2025-03-31
```

## Expected Output

Each analysis produces a structured report containing:

- **Summary** — Key findings in 2-3 sentences
- **Detailed Insights** — Tables and metrics organized by analysis type (e.g., duration, comments, responsiveness)
- **Recommendations** — Actionable next steps based on the data
- **Follow-Up Suggestions** — Related analyses to run for deeper understanding

See [Sample Reports](https://github.com/Azure/pr-insights/tree/main/docs/sample-reports) for example outputs.

## Custom Agents

- [Octane.PRInsights](agents/Octane.PRInsights.agent.md) — AI-powered PR analysis agent that routes queries to the appropriate PR Insights MCP tool

## Prompts

- [Octane.PRInsights.Analyze](prompts/Octane.PRInsights.Analyze.prompt.md) — Analyze Azure DevOps PR patterns, review bottlenecks, and team velocity

## Security & Privacy

- Queries run with **YOUR credentials** via On-Behalf-Of (OBO) authentication
- You only see data you already have access to
- No data is stored by the MCP server

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Authentication errors | Ensure VPN is connected |
| No data returned | Check CloudMine access is approved |
| Token expired | Sign out and back in |

## Learn More

- **[PR Insights GitHub Repository](https://github.com/Azure/pr-insights)**
- **[Insights Reference](https://github.com/Azure/pr-insights/blob/main/docs/insight-reference.md)** - Complete reference of all insights with explanations and metrics
- **[Sample Reports](https://github.com/Azure/pr-insights/tree/main/docs/sample-reports)** - Example outputs (use these for formatting guidance)

## Support

- **Email**: [PR Insights Team](mailto:anupv@microsoft.com,pragar@microsoft.com?subject=PR%20Insights%20Support)
- **Issues**: [GitHub Issues](https://github.com/Azure/pr-insights/issues)
