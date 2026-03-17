---
agent: Ev2Author
description: Update EV2 service artifacts (infrastructure, configuration, orchestration) with coordinated validation
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `UpdateType` (string, optional): Type of update (infrastructure, configuration, orchestration, comprehensive)
- `Target` (string, optional): Specific target (resource type, artifact name, region)
- `Scope` (string, optional): Scope of change (single-resource, service-wide, multi-region)

If inputs are not provided, the agent will discover workspace context and prompt for required information.

## OBJECTIVE

Execute the **UpdateService** workflow: authoring or modifying EV2 artifacts for a specific service change. You inherit all capabilities from the Ev2Author agent (validation workflow, Bicep expertise, tool catalog, principles).

This prompt applies the agent's VALIDATION-DRIVEN WORKFLOW to the specific task of updating service artifacts.

## WORKFLOW PHASES

### Phase 1: Discovery & Context

**Load Foundation**:
1. Call `mcp_ev2-mcp_get_ev2_best_practices` (MANDATORY first call)
2. If Bicep changes expected, call `mcp_bicep_experim_get_bicep_best_practices` (MANDATORY)

**Analyze Workspace**:
1. Identify EV2 service structure:
   - Service ID and service group name
   - Existing artifacts (serviceModel, rolloutSpec, scopeBindings, stageMap)
   - Bicep templates and parameter files (if present)
   - Build scripts and compilation pipelines
   - Configuration management (version.txt, config files)

2. Determine update scope:
   - Infrastructure change (requires Bicep + serviceModel updates)
   - Configuration change (scopeBindings, configurationOverrides)
   - Orchestration change (rolloutSpec, stageMap modifications)
   - Comprehensive change (multiple artifact types)

3. Query service state:
   - Call `mcp_ev2-mcp_get_service_info` for current registration
   - Call `mcp_ev2-mcp_get_artifacts` to review registered versions
   - Call `mcp_ev2-mcp_get_latest_rollouts` to understand deployment patterns

### Phase 2: Impact Analysis

**Assess Dependencies**:
1. Read existing artifacts to understand current state
2. Call `mcp_ev2-mcp_get_schema_ev2` for relevant schemas
3. Identify downstream impacts:
   - Will serviceModel changes affect rolloutSpec orchestration?
   - Will new resources require new scope bindings?
   - Will changes break existing rollout specs or stage maps?
   - Are there multi-service dependencies in the workspace?

**Document Baseline**:
- Note current version number
- Record existing resource topology
- Identify proven patterns to preserve

### Phase 3: Synchronized Modification

#### 3A: Infrastructure Updates (Bicep + EV2 Coordination)

**When adding or modifying Azure resources**:

1. **Author Bicep Infrastructure**:
   - Call `mcp_bicep_experim_get_az_resource_type_schema` for resource types
   - Author Bicep files using modern syntax (symbolic references, type safety, `parent` properties)
   - Check `mcp_bicep_experim_list_avm_metadata` for reusable modules
   - Create `.bicepparam` files with placeholder parameters (e.g., `__LOCATION__`, `__STORAGE_SKU__`)

2. **Update EV2 ServiceModel**:
   - Add extension referencing COMPILED template path
   ```json
   {
     "name": "NewResourceExtension",
     "templatePath": "templates/new-resource.template.json",
     "parametersPath": "parameters/new-resource.parameters.json",
     "deploymentLevel": "ResourceGroup"
   }
   ```

3. **Update ScopeBindings**:
   - Map Bicep placeholders to EV2 system variables or $config() references
   ```json
   {
     "key": "__LOCATION__",
     "value": "$(Location)"
   },
   {
     "key": "__STORAGE_SKU__",
     "value": "$config(StorageAccountSku)"
   }
   ```

4. **Update RolloutSpec (if orchestration changes)**:
   - Adjust orchestrationGraph if new extension alters deployment order
   - Add execution constraints for dependency management
   - Validate ordering (e.g., database before application)

5. **Provide Compilation Commands**:
   ```powershell
   # Compile Bicep template
   bicep build <workspace-path>/new-resource.bicep --outfile <workspace-path>/ev2/templates/new-resource.template.json
   
   # Compile Bicep parameters
   bicep build-params <workspace-path>/new-resource.bicepparam --outfile <workspace-path>/ev2/parameters/new-resource.parameters.json
   ```

#### 3B: Configuration Updates (ScopeBindings / ConfigurationOverrides)

**When modifying configuration without infrastructure changes**:

1. Update scopeBindings.json or configurationOverrides:
   - Add new configuration parameters
   - Ensure scopeTagName consistency with serviceModel
   - Validate against schema with `mcp_ev2-mcp_get_schema_ev2`

2. Update rolloutSpec if configuration affects orchestration

3. Validate parameter references across artifacts

#### 3C: Orchestration Updates (RolloutSpec / StageMap)

**When modifying deployment ordering or staging**:

1. **Modify RolloutSpec**:
   - Retrieve schema: `mcp_ev2-mcp_get_schema_ev2 schemaName=RegionAgnosticRolloutSpecification`
   - Update orchestrationGraph to reflect new ordering
   - Add/modify executionConstraints for dependencies
   - Adjust rolloutPolicy if needed (retry, timeout settings)

2. **Modify StageMap** (if stage progression changes):
   - Retrieve schema: `mcp_ev2-mcp_get_schema_ev2 schemaName=StageMap`
   - Update stage definitions and region groupings
   - Register new stageMap version: `mcp_ev2-mcp_register_stage_map`

### Phase 4: Validation Cycle

**Apply agent's validation workflow (see agent.md VALIDATION-DRIVEN WORKFLOW)**:

1. **Static Validation** (iterate without version increment):
   ```
   mcp_ev2-mcp_validate_region_agnostic_artifacts(
     serviceGroupRoot=<path>,
     rolloutSpecPath=<path>,
     selectionScope=["regions(*)"],
     exclusionScope=[]
   )
   ```
   - Iterate on errors WITHOUT incrementing version
   - Query `mcp_ev2-mcp_ev2_knowledge_search` for error clarification
   - Fix issues and re-validate until clean

2. **Version Increment** (only after clean validation):
   - Update version.txt with unique value (timestamp or semantic version)
   - Ensure version not already registered

3. **Registration** (requires user approval):
   - **REQUEST EXPLICIT USER APPROVAL**
   - Call `mcp_ev2-mcp_register_region_agnostic_artifacts`
   - Handle warnings (forceRegistration only if justified)

4. **Dynamic Validation** (post-registration):
   ```
   mcp_ev2-mcp_start_validation_rollout(
     serviceId=<guid>,
     serviceGroupName=<name>,
     stageMapName=<name>,
     selectionScope=["regions(*)"]
   )
   ```
   - Simulates orchestration without deploying resources
   - Validates ordering, bindings, permissions
   - If errors: fix artifacts → increment version → re-register → re-validate

### Phase 5: Deliverables

**Provide comprehensive status report**:

1. **Files Modified**:
   - List all changed files with git diff summary
   - Highlight new files vs. modifications

2. **Validation Results**:
   - Static validation: Pass/Fail, error count, warnings
   - Dynamic validation: Orchestration graph status, binding validation

3. **Registration Status**:
   - Version number registered
   - Timestamp of registration
   - Artifact version ID

4. **Required User Actions**:
   - Bicep compilation commands (if Bicep changed)
   - Build pipeline steps (if applicable)
   - Next validation steps (if issues remain)

5. **Rollback Plan**:
   - Document how to revert changes if deployment fails
   - Note previous working version number

## UPDATESERVICE-SPECIFIC CONSTRAINTS

1. **Multi-Resource Coordination**: When adding/modifying Azure resources, update ALL related artifacts atomically (Bicep + serviceModel + scopeBindings + rolloutSpec)

2. **Backward Compatibility Assessment**: Evaluate whether changes break existing rollout specs, stage maps, or deployed resources

3. **Parameter Hygiene**: Ensure all new parameters have:
   - Defaults in Bicep (or marked required)
   - Bindings in scopeBindings.json
   - Documentation in comments

4. **Compilation Checkpoints**: After Bicep changes, explicitly provide compilation commands before proceeding to EV2 validation

5. **Version History Context**: Review recent rollout history to understand deployment patterns before making structural changes

6. **Tool Constraint**: Do NOT use rollout management tools (start_rollout, cancel_rollout, etc.) in UpdateService workflow. Focus on artifact authoring and validation.

## EXAMPLE SCENARIOS

### Scenario A: Add Azure Storage Account for Logging

**User Request**: "Add storage account for application logging"

**Expected Workflow**:
1. Load EV2 and Bicep best practices
2. Query `mcp_bicep_experim_get_az_resource_type_schema` for Microsoft.Storage/storageAccounts
3. Author Bicep module:
   ```bicep
   @description('Storage account for application logs')
   param storageAccountName string
   param location string = resourceGroup().location
   param sku string
   
   resource logStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
     name: storageAccountName
     location: location
     sku: { name: sku }
     kind: 'StorageV2'
     properties: {
       accessTier: 'Hot'
       supportsHttpsTrafficOnly: true
       minimumTlsVersion: 'TLS1_2'
     }
   }
   ```
4. Create `.bicepparam` with placeholders:
   ```bicep
   using './log-storage.bicep'
   param storageAccountName = '__STORAGE_ACCOUNT_NAME__'
   param location = '__LOCATION__'
   param sku = '__STORAGE_SKU__'
   ```
5. Update serviceModel.json → add extension:
   ```json
   {
     "name": "LogStorageExtension",
     "templatePath": "templates/log-storage.template.json",
     "parametersPath": "parameters/log-storage.parameters.json",
     "deploymentLevel": "ResourceGroup"
   }
   ```
6. Update scopeBindings.json → map placeholders:
   ```json
   {
     "key": "__STORAGE_ACCOUNT_NAME__",
     "value": "$config(LogStorageAccountName)"
   },
   {
     "key": "__LOCATION__",
     "value": "$(Location)"
   },
   {
     "key": "__STORAGE_SKU__",
     "value": "Standard_LRS"
   }
   ```
7. Validate statically (iterate until clean)
8. Provide compilation commands:
   ```powershell
   bicep build infra/bicep/log-storage.bicep --outfile infra/ev2/templates/log-storage.template.json
   bicep build-params infra/bicep/log-storage.bicepparam --outfile infra/ev2/parameters/log-storage.parameters.json
   ```
9. Increment version → request approval → register → dynamic validation
10. Report status with rollback plan

### Scenario B: Modify Rollout Ordering (Database Before App)

**User Request**: "Ensure database deploys before application service"

**Expected Workflow**:
1. Load EV2 best practices
2. Read current rolloutSpec.json
3. Retrieve schema: `mcp_ev2-mcp_get_schema_ev2 schemaName=RegionAgnosticRolloutSpecification`
4. Analyze orchestrationGraph:
   - Identify database extension name (e.g., "DatabaseExtension")
   - Identify app extension name (e.g., "AppServiceExtension")
5. Modify orchestrationGraph to enforce ordering:
   ```json
   "orchestrationGraph": [
     {
       "name": "InfrastructurePhase",
       "extensions": ["DatabaseExtension"]
     },
     {
       "name": "ApplicationPhase",
       "extensions": ["AppServiceExtension"],
       "executionConstraints": {
         "dependsOn": ["InfrastructurePhase"]
       }
     }
   ]
   ```
6. Validate statically (verify orchestration graph integrity)
7. Increment version → request approval → register
8. Start validation rollout to confirm ordering
9. Report orchestration graph summary

### Scenario C: Add New Deployment Region

**User Request**: "Enable deployment to Japan East"

**Expected Workflow**:
1. Load EV2 best practices
2. Query valid regions: `mcp_ev2-mcp_get_valid_geography_and_regions`
3. Update scopeBindings.json → add "japaneast" scope tag binding:
   ```json
   {
     "scopeTagName": "Region",
     "scopeTags": [
       { "scopeTagValue": "westus", ... },
       { "scopeTagValue": "japaneast", ... }
     ]
   }
   ```
4. Verify serviceModel.json uses `$(Location)` system variable (region-agnostic)
5. Update rolloutSpec.json → confirm region-agnostic orchestration
6. Validate statically → increment version → register
7. Start validation rollout with selectionScope: `["regions(japaneast)"]`
8. Report region-specific validation results

### Scenario D: Update Configuration Parameter

**User Request**: "Change connection string timeout from 30s to 60s"

**Expected Workflow**:
1. Load EV2 best practices
2. Locate parameter in scopeBindings.json or configurationOverrides
3. Update value:
   ```json
   {
     "key": "ConnectionStringTimeout",
     "value": "60"
   }
   ```
4. Validate against schema
5. Increment version → request approval → register
6. Start validation rollout to confirm configuration binding
7. Report configuration change with before/after values

---

# Task

Execute the UpdateService workflow: discover workspace context, analyze impact, modify artifacts with coordination, validate rigorously, and register with approval. Deliver comprehensive status report with actionable next steps.
