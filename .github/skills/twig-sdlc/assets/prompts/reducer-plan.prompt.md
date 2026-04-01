Review the implementation plan for unnecessary complexity.
**Plan:** Read the file at `{{ architect.output.plan_path }}`
**Original request:** #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}
**Description:** {{ intake.output.epic_description }}
Apply **plan-level reduction** principles:
1. Flag features not in the original requirements (scope creep)
2. Identify abstractions added for hypothetical future needs
3. Challenge config options with only one realistic value
4. Merge steps that can be combined
5. Spot polish work that exceeds the ask
If you find issues, **edit the plan file directly** to simplify it.
If the plan is already lean, say so and move on.
