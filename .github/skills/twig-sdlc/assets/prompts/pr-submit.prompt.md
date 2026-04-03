Create a PR for the current PR group.
**Branch:** {{ pr_group_manager.output.branch_name }}
**PR Group:** {{ pr_group_manager.output.current_pr_group }}
**Completed Issues:** {{ pr_group_manager.output.pr_group_issue_ids | json }}
## Steps
1. **Pre-submit validation** — check for stale references and build errors:
   - `dotnet build --no-restore 2>&1` — must produce zero errors
   - If build fails, fix the issues (stale references to renamed/removed methods,
     missing usings, broken call sites) and commit the fixes before proceeding
   - `dotnet test --settings test.runsettings` — all tests must pass
2. Push the branch: `git push -u origin {{ pr_group_manager.output.branch_name }}`
3. Create the PR:
   ```
   gh pr create --base main --head {{ pr_group_manager.output.branch_name }} \
     --title "<PR group title>" \
     --body "<description with AB# references>"
   ```
4. The PR body should include:
   - Summary of changes
   - List of ADO work items (AB#<id> format for linking)
   - Files changed summary
   - Test coverage notes
