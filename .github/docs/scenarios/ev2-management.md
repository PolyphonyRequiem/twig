# EV2 Management

## Overview

The **EV2 Management** scenario provides comprehensive EV2 lifecycle management for Azure deployments. It combines two powerful workflows:

1. **Deployment Monitoring** - Track active deployments, diagnose issues, and correlate state across Azure DevOps and EV2 systems
2. **Artifact Authoring** - Create, modify, and validate EV2 configuration artifacts with integrated Bicep infrastructure-as-code support

This scenario streamlines both operational monitoring and configuration management, enabling teams to author safe deployments and monitor them through to completion.

## When to Use

Use this scenario when you need to:

**Deployment Monitoring**:
- Track the status of an active or recent EV2 rollout
- Diagnose deployment failures in Azure DevOps or EV2
- Correlate ADO builds with EV2 rollout execution
- Monitor multi-region staged deployments
- Respond to deployment incidents

**Artifact Authoring**:
- Create new EV2 configuration artifacts (serviceModel, rolloutSpec, scopeBindings)
- Modify existing EV2 artifacts with schema validation
- Author Bicep templates for Azure infrastructure
- Validate artifacts before registration
- Troubleshoot validation errors and schema violations

## Prerequisites

### Required MCP Servers

This scenario requires manual installation of three MCP servers:

1. **EV2 MCP Server** - EV2 deployment platform integration (.NET global tool)
2. **Azure DevOps MCP Server** - Azure DevOps pipeline and build management (npx-based)
3. **Bicep Extension** - Bicep infrastructure-as-code authoring tools (VS Code extension: ms-azuretools.vscode-bicep)

**Setup Instructions**: See the comprehensive [MCP Server Setup Guide](../../../docs/ev2-management/mcp-setup.md) for installation, configuration, and troubleshooting.

### Required Software

- **PowerShell** (Windows, pre-installed)
- **.NET SDK or Runtime** (for EV2 MCP Server)
- **Node.js 18+** (includes npx for ADO MCP Server)
- **Azure CLI** (for authentication)
- **Bicep VS Code Extension** ([ms-azuretools.vscode-bicep](https://marketplace.visualstudio.com/items?itemName=ms-azuretools.vscode-bicep)) - Provides Bicep MCP tools automatically
- **Azure Artifacts Credential Provider** (for NuGet authentication)

### Access Requirements

- Azure subscription with appropriate permissions
- Azure DevOps project access
- EV2 service configuration access
- Understanding of Azure deployment pipelines

## What's Included

### Custom Agents

- **[Deployer](agents/Deployer.agent.md)** - Senior DevOps Engineer specializing in deployment automation, monitoring, and production operations
- **[Ev2Author](agents/Ev2Author.agent.md)** - EV2 Configuration Architect specializing in artifact authoring and validation

### MCP Servers

- **[code-search](https://mcp.engcopilot.net/)** - Code search and navigation (Bluebird)
- **[ms-learn](https://learn.microsoft.com/api/mcp)** - Microsoft Learn documentation
- **[ev2-mcp](https://msazure.visualstudio.com/One/_git/Deployment-Ease)** - EV2 deployment telemetry and orchestration
- **[ado](https://github.com/microsoft/azure-devops-mcp)** - Azure DevOps pipeline and build management
- **[bicep-mcp](https://github.com/modelcontextprotocol/servers)** - Bicep infrastructure-as-code authoring and validation

### Prompts

- **[Octane.Ev2Deployer.Monitor](prompts/Octane.Ev2Deployer.Monitor.prompt.md)** - Monitor and track ADO pipeline and EV2 rollout status
- **[Octane.Ev2Author.CreateNewService](prompts/Octane.Ev2Author.CreateNewService.prompt.md)** - Create a new EV2 service from scratch with all required artifacts, validation, and registration
- **[Octane.Ev2Author.UpdateService](prompts/Octane.Ev2Author.UpdateService.prompt.md)** - Update service artifacts (infrastructure, configuration, orchestration) with coordinated validation

## Configuration

After installing the scenario, configure your service settings in `.config/octane.yaml`:

```yaml
ev2-management:
  ev2_deployer:
    default_service: my_service  # Your default service name
    auto-refresh: true
    services:
      my_service:  # Replace with your service name
        name: My Service Name
        description: Description of your service
        ev2:
          service_id: 00000000-0000-0000-0000-000000000000  # Your EV2 Service GUID
          service_group_name: Microsoft.YourProduct.YourService  # Your Service Group Name
          endpoint: Test  # Environment: Test, Prod
        ado:
          project_name: YourProjectName  # Your Azure DevOps project
          pipeline_id: 000000  # Your pipeline ID number
```

You'll also need to update Azure DevOps connection details in `.vscode/mcp.json` (see [MCP Setup Guide](../../../docs/ev2-management/mcp-setup.md) for details).

## Workflows

### 1. Monitor Deployment Status

Track the status of an active or recent deployment across both ADO and EV2 systems.

**Steps:**
1. Open GitHub Copilot Chat
2. Select **Ev2Deployer** from the agent dropdown
3. Use the Monitor prompt:
   ```
   /Octane.Ev2Deployer.Monitor
   ```

**What the Ev2Deployer Does:**
1. Loads service configuration from `octane.yaml`
2. Queries ADO for recent pipeline builds
3. Retrieves EV2 rollout details
4. Correlates state across both systems
5. Extracts deployment metadata (rollout ID, artifact version, regions)
6. Identifies issues or blockers
7. Provides comprehensive status report with recommendations

**Expected Output:**
- Executive summary of deployment status
- ADO pipeline details (build ID, status, duration)
- EV2 rollout details (rollout ID, current stage, progress)
- Any issues or blockers requiring attention
- Recommended next actions
- Timeline for updates or completion

### 2. Monitor Specific Build or Rollout

Track a specific ADO build ID or EV2 rollout ID.

> **Note**: These commands require your service to be configured in `octane.yaml` with EV2 service ID, service group name, and ADO project details.

**Steps:**
```
# Monitor specific build
/Octane.Ev2Deployer.Monitor BuildId=12345

# Monitor specific rollout
/Octane.Ev2Deployer.Monitor RolloutId=a1b2c3d4-e5f6-4a5b-9c8d-7e6f5a4b3c2d

# Monitor specific service and rollout
/Octane.Ev2Deployer.Monitor Service=my_service RolloutId=a1b2c3d4-e5f6-4a5b-9c8d-7e6f5a4b3c2d
```

### 3. Diagnose Deployment Failures

When a deployment fails, the Ev2Deployer agent will:
1. Identify where the failure occurred (ADO vs. EV2)
2. Extract detailed error messages and logs
3. Search EV2 knowledge base for similar issues
4. Compare with historical rollouts to identify patterns
5. Provide specific remediation steps
6. Suggest retry, skip, or escalation actions

### 4. Create New EV2 Service

Create a complete EV2 service from scratch, including all required artifacts, infrastructure templates, service registration, and validation.

**Steps:**
1. Open GitHub Copilot Chat
2. Select **Ev2Author** from the agent dropdown
3. Use the CreateNewService prompt:
   ```
   /Octane.Ev2Author.CreateNewService
   ```

**What the Ev2Author Does:**
1. Loads EV2 and Bicep best practices (mandatory first step)
2. Gathers required information interactively:
   - Service Tree ID and naming (service category, name, group)
   - Ownership details (AAD security group, contact email)
   - Deployment scope (regions, environment)
   - Infrastructure requirements (Azure resources, Bicep vs ARM)
   - Deployment strategy (stage map, orchestration)
3. Creates complete artifact structure:
   - Service specification and service group specification
   - Infrastructure templates (Bicep with modern syntax)
   - Service model (resource definitions)
   - Scope bindings (parameterization strategy)
   - Rollout specification (orchestration)
   - Version file and parameter files
4. Registers service with EV2 (with user approval)
5. Validates artifacts (static validation - schema and orchestration)
6. Registers artifacts (with user approval)
7. Executes validation rollout (dynamic validation - simulates deployment)
8. Provides comprehensive summary and next steps

**Expected Output:**
- Complete artifact directory structure
- All required JSON files (serviceSpec, serviceModel, rolloutSpec, scopeBindings)
- Bicep infrastructure templates with compilation instructions
- Service registration confirmation
- Static validation results (pass/fail with errors)
- Registration status (version, timestamp)
- Dynamic validation results (orchestration simulation)
- Comprehensive next steps for production deployment

**Example Usage:**
```
# Create service interactively (agent will prompt for details)
/Octane.Ev2Author.CreateNewService

# Create service with initial parameters
/Octane.Ev2Author.CreateNewService ServiceName="BlobService" ServiceCategory="Azure" DeploymentRegions=["centralus","eastus2"]
```

### 5. Update Existing EV2 Service

Modify existing EV2 deployment artifacts with integrated Bicep infrastructure-as-code support.

**Steps:**
1. Open GitHub Copilot Chat
2. Select **Ev2Author** from the agent dropdown
3. Use the UpdateService prompt:
   ```
   /Octane.Ev2Author.UpdateService
   ```

**What the Ev2Author Does:**
1. Loads EV2 and Bicep best practices (mandatory first step)
2. Discovers workspace context (existing artifacts, Bicep templates, build scripts)
3. Retrieves relevant schemas for artifact types
4. Authors or modifies artifacts following validation-first approach:
   - Static validation after each change (schema, files, orchestration)
   - Iterates on errors without incrementing version
   - Increments version only when validation passes
   - Registers artifacts (with user approval)
   - Dynamic validation (simulates orchestration)
5. Provides comprehensive status report with next steps

**Common Use Cases:**

**Add Azure Storage Account**:
```
/Octane.Ev2Author.UpdateService UpdateType=infrastructure Target="storage account for logging"
```

**Modify Rollout Ordering**:
```
/Octane.Ev2Author.UpdateService UpdateType=orchestration Target="ensure database before app"
```

**Add New Region**:
```
/Octane.Ev2Author.UpdateService UpdateType=configuration Target="enable Japan East region"
```

**Update Configuration Parameter**:
```
/Octane.Ev2Author.UpdateService UpdateType=configuration Target="connection timeout to 60s"
```

**Expected Output:**
- Workspace context discovery report
- Artifact modifications with schema validation
- Static validation results (pass/fail with actionable errors)
- Registration status (version, timestamp)
- Dynamic validation results (orchestration simulation)
- Bicep compilation reminders (if Bicep files modified)
- Next steps and recommendations

## Tips and Best Practices

### For Deployment Monitoring

- **Check Regularly**: Run Monitor prompt periodically during long deployments
- **Point-in-Time Status**: Remember that deployment status is a snapshot - re-run to get updates
- **Expect Delays**: ADO-to-EV2 handoff typically takes 30-120 seconds
- **Service Profiles**: Configure multiple services in `octane.yaml` for easy switching
- **Environment Separation**: Use different EV2 endpoints (Test vs. Prod) for different environments

### For Artifact Authoring

- **Always validate before registering** - The Ev2Author follows a validation-first approach; never skip static validation
- **Iterate with the same version** - Fix validation errors without incrementing version; increment only when clean
- **Use unique version numbers** - Each registration requires a new version; never reuse
- **Compile Bicep before registration** - EV2 doesn't understand Bicep; always compile to JSON first
- **Trust EV2 validation errors** - They are authoritative; feed errors to `ev2_knowledge_search` for clarification
- **Request user approval for registration** - Never register without explicit confirmation
- **Use Bicep best practices** - Always call `get_bicep_best_practices` before authoring Bicep code
- **Query schemas proactively** - Call `get_schema_ev2` and `get_az_resource_type_schema` before modifications
- **Test with validation rollouts** - Use dynamic validation to catch binding and orchestration issues

### Safety Considerations

- **Never** approve rollout continuations or artifact registrations without explicit confirmation
- **Always** review details before taking destructive actions (cancel, restart, register)
- **Verify** you're working with the correct rollout ID, service, and artifact version
- **Trust EV2** as the authoritative source when state conflicts exist

### Troubleshooting

If prompts don't work:
1. Verify MCP servers are installed and connected (check VS Code status bar)
2. Confirm service configuration in `octane.yaml` is correct
3. Check ADO connection details in `.vscode/mcp.json`
4. Review the [MCP Setup Guide](../../../docs/ev2-management/mcp-setup.md) troubleshooting section
5. Check VS Code Output panel for error messages
6. For Bicep issues, verify Bicep CLI is installed: `bicep --version`

## Related Scenarios

- **Repository Overview** - Generate codebase summaries before deployment changes
- **Spec-Driven Development** - Plan and implement features systematically
- **Test Analysis** - Ensure comprehensive test coverage before deployment

## Difficulty

**Advanced** - Requires deep understanding of:
- EV2 deployment orchestration and artifact structure
- Azure DevOps pipelines and build processes
- Azure infrastructure and services
- Bicep infrastructure-as-code authoring
- Incident response and troubleshooting
- Configuration management and parameterization strategies

## Tags

`deployment` `ev2` `azure-devops` `operations` `bicep` `infrastructure` `authoring` `validation`
