Create a PR for the current PR group.
**Branch:** {{ implementation_manager.output.branch_name }}
**PR Group:** {{ implementation_manager.output.current_pr_group }}
**Issue:** #{{ implementation_manager.output.current_issue_id }} — {{ implementation_manager.output.current_issue_title }}
**Completed Tasks:** {{ implementation_manager.output.completed_tasks | json }}
## Steps
1. **Pre-submit validation** — check for stale references and build errors:
   - `dotnet build --no-restore 2>&1` — must produce zero errors
   - If build fails, fix the issues (stale references to renamed/removed methods,
     missing usings, broken call sites) and commit the fixes before proceeding
   - `dotnet test --settings test.runsettings` — all tests must pass
2. Push the branch: `git push -u origin {{ implementation_manager.output.branch_name }}`
3. Create the PR:
   ```
   gh pr create --base main --head {{ implementation_manager.output.branch_name }} \
     --title "<PR group title>" \
     --body "<description with AB# references>"
   ```
4. The PR body should include:
   - Summary of changes
   - List of ADO work items (AB#<id> format for linking)
   - Files changed summary
   - Test coverage notes
