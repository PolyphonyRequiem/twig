---
agent: Ev2Author
description: Create a new EV2 service from scratch with all required artifacts, infrastructure templates, and validation
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `ServiceName` (string, optional): Name of the new service
- `ServiceCategory` (string, optional): Service category for naming (e.g., MyProduct)
- `DeploymentRegions` (array, optional): Initial deployment regions (e.g., ["centralus", "eastus"])
- `InfrastructureType` (string, optional): Infrastructure approach (bicep, arm, existing-templates)

If inputs are not provided, the agent will prompt for required information interactively.

## OBJECTIVE

Execute the **CreateNewService** workflow: author a complete, production-ready EV2 service from scratch, including all required artifacts, infrastructure templates, service registration, and comprehensive validation. You inherit all capabilities from the Ev2Author agent (validation workflow, Bicep expertise, tool catalog, principles).

This prompt guides you through the complete service creation lifecycle, from initial information gathering through service registration and validation rollout.

## WORKFLOW PHASES

### Phase 0: Foundation Loading (MANDATORY)

**Load Best Practices**:
1. Call `mcp_ev2-mcp_get_ev2_best_practices` (MANDATORY first call - anchor all decisions to current guidance)
2. Call `mcp_bicep_experim_get_bicep_best_practices` (MANDATORY if creating Bicep templates - avoid outdated patterns)

These calls are NON-NEGOTIABLE. They ensure all artifacts follow current best practices.

---

### Phase 1: Information Gathering

**Collect Required Service Information**:

You must gather the following information from the user. Ask clearly and provide examples:

1. **Service Identity**:
   - **Service Tree ID** (GUID): "What is your Service Tree identifier (GUID)? You can find this in Service Tree."
   - **Service Category**: "What is your service category? (e.g., Azure, MyProduct, InternalTools)"
   - **Service Name**: "What is your service name? (e.g., StorageService, ComputeManager)"
   - **Service Group Name**: "What is your service group name? Format: Microsoft.{Category}.{Service}.{Group}"
     - Example: `Microsoft.Azure.Storage.BlobService`

2. **Service Ownership**:
   - **Owner Group ObjectId** (GUID): "What is your AAD security group ObjectId? (You must be a member of this group)"
   - **Owner Contact Email**: "What is your service distribution list email?"

3. **Deployment Scope**:
   - **Initial Regions**: "Which Azure regions will you deploy to initially?" 
     - Suggest calling `mcp_ev2-mcp_get_valid_geography_and_regions` to show valid options
     - Recommend starting with 1-2 regions (e.g., centralus, eastus2)
   - **Environment**: "Which EV2 environment? (Test for initial development, Prod for production)"

4. **Infrastructure Requirements**:
   - **Resource Types**: "What Azure resources does your service need?"
     - Examples: Storage Accounts, App Services, Virtual Machines, Cosmos DB, Key Vaults
     - For each resource, note configuration requirements (SKU, networking, etc.)
   - **Template Approach**: "How will you define infrastructure?"
     - Option A: "I'll create new Bicep templates" (recommended for new services)
     - Option B: "I have existing ARM templates"
     - Option C: "I need help determining what resources to create"

5. **Deployment Strategy**:
   - **Stage Map**: "Will you use the standard Microsoft.Azure.SDP.Standard stage map or need a custom one?"
     - Recommend standard for most services
   - **Rollout Orchestration**: "Do you need specific deployment ordering or dependencies?"
     - Example: "Deploy infrastructure before application code"

6. **Configuration Parameters**:
   - "What configuration values will differ across regions/environments?"
     - Examples: location, SKU sizes, storage redundancy, networking settings
   - These become scope bindings and configuration overrides

**Validate Prerequisites**:
- Confirm user is a member of the owner security group
- Confirm user has necessary EV2 environment access (Test or Prod)
- Confirm Service Tree registration exists

---

### Phase 2: Artifact Structure Setup

**Create Directory Structure**:

Create a well-organized artifact folder structure:

```
<service-artifacts-root>/
├── version.txt                          # Build version (start with "1.0.0")
├── serviceSpec.json                     # Service specification
├── serviceGroupSpec.json                # Service group specification
├── serviceModel.json                    # Service model (what to deploy)
├── rolloutSpec.json                     # Rollout specification (how to deploy)
├── scopeBindings.json                   # Scope bindings (parameterization)
├── templates/                           # Bicep/ARM templates
│   ├── infrastructure.bicep             # Main infrastructure template
│   ├── infrastructure.template.json     # Compiled ARM template (generated)
│   └── (additional resource templates)
└── parameters/                          # Parameter files
    ├── infrastructure.parameters.json   # Templatized parameter file
    └── (additional parameter files)
```

**Explain Compilation Requirement**:
> **CRITICAL**: Bicep files MUST be compiled to JSON before EV2 registration. The `serviceModel.json` references the compiled `.template.json` files, NOT the `.bicep` source files.
> 
> Compilation command: `bicep build templates/infrastructure.bicep --outfile templates/infrastructure.template.json`

---

### Phase 3: Core Artifact Authoring

#### 3A: Service Specification (`serviceSpec.json`)

**Retrieve Schema**:
```
mcp_ev2-mcp_get_schema_ev2 schemaName=ServiceSpecification
```

**Author Service Spec**:
```json
{
  "$schema": "https://ev2schema.azure.net/schemas/2020-01-01/serviceSpecification.json",
  "contentVersion": "1.0.0.0",
  "providerType": "ServiceTree",
  "identifier": "<Service-Tree-GUID>",
  "description": "<Service Description>",
  "ownerGroupObjectId": "<AAD-Security-Group-ObjectId>",
  "ownerGroupContactEmail": "<service-dl@microsoft.com>",
  "policyCheckEnabled": true
}
```

**Replace Placeholders**:
- `<Service-Tree-GUID>`: User's Service Tree identifier
- `<Service Description>`: Brief description of the service
- `<AAD-Security-Group-ObjectId>`: User's security group ObjectId
- `<service-dl@microsoft.com>`: User's distribution list email

#### 3B: Service Group Specification (`serviceGroupSpec.json`)

**Retrieve Schema**:
```
mcp_ev2-mcp_get_schema_ev2 schemaName=ServiceSpecification
```

**Author Service Group Spec**:
```json
{
  "name": "Microsoft.<Category>.<Service>.<Group>",
  "description": "<Service Group Description>",
  "ownerGroupObjectId": "<AAD-Security-Group-ObjectId>",
  "ownerGroupContactEmail": "<service-dl@microsoft.com>",
  "contentVersion": "1.0.0.0"
}
```

**Naming Convention**:
- Format: `Microsoft.<Category>.<Service>.<Group>`
- Example: `Microsoft.Azure.Storage.BlobService`

#### 3C: Infrastructure Templates (Bicep)

**For Each Required Resource Type**:

1. **Query Resource Schema**:
   ```
   mcp_bicep_experim_get_az_resource_type_schema(
     resourceType="Microsoft.Storage/storageAccounts",
     apiVersion="2023-01-01"
   )
   ```

2. **Check for Azure Verified Modules**:
   ```
   mcp_bicep_experim_list_avm_metadata
   ```
   Search results for relevant modules (e.g., "avm/res/storage/storage-account")

3. **Author Bicep Template** (`templates/infrastructure.bicep`):

**Use Modern Bicep Syntax**:
- Symbolic references (NOT `resourceId()` function calls)
- Type-safe parameters with `@allowed()`, `@minLength()`, etc.
- User-defined types for complex objects
- `parent` property for child resources (NOT `/` in names)
- Safe dereference `?.` and coalesce `??` operators

**Example Template Structure**:
```bicep
@description('Primary deployment region')
param location string = '__LOCATION__'

@description('Storage account SKU')
@allowed(['Standard_LRS', 'Standard_GRS', 'Premium_LRS'])
param storageAccountSku string = '__STORAGE_SKU__'

@description('Storage account name')
@minLength(3)
@maxLength(24)
param storageAccountName string = '__STORAGE_ACCOUNT_NAME__'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: storageAccountSku
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
```

**Parameterization Strategy**:
- Use placeholder tokens like `__LOCATION__`, `__STORAGE_SKU__`
- These will be replaced via scope bindings
- Avoid hardcoding region-specific or environment-specific values

4. **Create Parameter File** (`parameters/infrastructure.parameters.json`):
```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "location": {
      "value": "__LOCATION__"
    },
    "storageAccountSku": {
      "value": "__STORAGE_SKU__"
    },
    "storageAccountName": {
      "value": "__STORAGE_ACCOUNT_NAME__"
    }
  }
}
```

**Important Reminder**:
> Explain to user: "You'll need to compile this Bicep template before registration:
> 
> `bicep build templates/infrastructure.bicep --outfile templates/infrastructure.template.json`
> 
> This compilation typically happens in your CI/CD build pipeline."

#### 3D: Service Model (`serviceModel.json`)

**Retrieve Schema**:
```
mcp_ev2-mcp_get_schema_ev2 schemaName=RegionAgnosticServiceModel
```

**Author Service Model**:
```json
{
  "$schema": "https://ev2schema.azure.net/schemas/2020-01-01/serviceModel.json",
  "contentVersion": "1.0.0.0",
  "serviceIdentifier": "<Service-Tree-GUID>",
  "serviceGroup": "Microsoft.<Category>.<Service>.<Group>",
  "serviceGroupSpecificationPath": "serviceGroupSpec.json",
  "serviceResourceGroupDefinitions": [
    {
      "name": "InfrastructureResources",
      "serviceResourceGroups": [
        {
          "name": "<Service>-$(Location)-rg",
          "location": "$(Location)",
          "scopeTags": ["InfraScope"],
          "serviceResources": [
            {
              "name": "InfrastructureDeployment",
              "type": "ARM",
              "armTemplate": {
                "templatePath": "templates/infrastructure.template.json",
                "parametersPath": "parameters/infrastructure.parameters.json",
                "deploymentLevel": "ResourceGroup"
              }
            }
          ]
        }
      ]
    }
  ]
}
```

**Key Elements**:
- `$(Location)`: System variable replaced at deployment time
- `scopeTags`: References scope bindings (defined in scopeBindings.json)
- `templatePath`: References COMPILED `.template.json` file (not `.bicep`)
- `deploymentLevel`: Usually "ResourceGroup" (also supports "Subscription", "ManagementGroup")

**Resource Group Naming**:
- Use format: `<Service>-$(Location)-rg`
- Example: `BlobService-centralus-rg`

#### 3E: Scope Bindings (`scopeBindings.json`)

**Retrieve Schema**:
```
mcp_ev2-mcp_get_schema_ev2 schemaName=ScopeBindings
```

**Author Scope Bindings**:
```json
{
  "$schema": "https://ev2schema.azure.net/schemas/2020-01-01/scopeBindings.json",
  "contentVersion": "1.0.0.0",
  "scopeBindings": [
    {
      "scopeTagName": "InfraScope",
      "bindings": [
        {
          "key": "__LOCATION__",
          "value": "$(Location)"
        },
        {
          "key": "__STORAGE_SKU__",
          "value": "$config(StorageAccountSku)"
        },
        {
          "key": "__STORAGE_ACCOUNT_NAME__",
          "value": "$config(StorageAccountNamePrefix)$(Location)"
        }
      ]
    }
  ]
}
```

**Binding Types**:
- **System Variables**: `$(Location)`, `$(Environment)`, `$(Subscription)`
- **Configuration References**: `$config(SettingName)` - retrieved from EV2 configuration
- **Concatenation**: Combine multiple values: `prefix$(Location)suffix`

**Scope Tag Alignment**:
- `scopeTagName` MUST match `scopeTags` array in serviceModel
- One binding set per scope tag

#### 3F: Rollout Specification (`rolloutSpec.json`)

**Retrieve Schema**:
```
mcp_ev2-mcp_get_schema_ev2 schemaName=RegionAgnosticRolloutSpecification
```

**Author Rollout Spec**:
```json
{
  "$schema": "https://ev2schema.azure.net/schemas/2020-01-01/rolloutSpec.json",
  "contentVersion": "1.0.0.0",
  "rolloutMetadata": {
    "name": "<Service> Deployment",
    "rolloutType": "Major",
    "serviceModelPath": "serviceModel.json",
    "buildSource": {
      "parameters": {
        "versionFile": "version.txt"
      }
    },
    "scopeBindingsPath": "scopeBindings.json"
  },
  "orchestratedSteps": [
    {
      "name": "DeployInfrastructure",
      "targetType": "ServiceResourceGroupDefinition",
      "targetName": "InfrastructureResources",
      "actions": ["Create", "Update"]
    }
  ]
}
```

**Orchestration Design**:
- Each step deploys one `ServiceResourceGroupDefinition` from serviceModel
- `actions`: Usually ["Create", "Update"] for idempotent deployments
- Multiple steps can define deployment order (e.g., infrastructure before application)

**Rollout Types**:
- `Major`: Significant changes, full deployment
- `Minor`: Incremental updates
- `Hotfix`: Emergency fixes

#### 3G: Version File (`version.txt`)

**Create Initial Version**:
```
1.0.0
```

**Version Strategy**:
- Each registration requires a unique version
- Increment for every registration attempt
- Common formats: semantic versioning (1.0.0), timestamp (20240101.1), build number

---

### Phase 4: Service Registration (Requires User Approval)

**Before Registration - Explain the Process**:
> "Service registration is a one-time operation per EV2 environment (Test/Prod) that registers your service identity with EV2. This is required before you can register artifacts or start rollouts."

**Explain Declarative Registration**:
> "We'll use declarative registration (Option 3), which automatically registers your service when you register region-agnostic artifacts. This is the recommended approach for new services."

**USER APPROVAL REQUIRED**:
Ask: "Ready to proceed with service registration and artifact registration? This will:
1. Register your service with EV2 (one-time)
2. Register your service group (if not already registered)
3. Register the initial artifact version (version 1.0.0)

Type 'yes' to proceed or 'no' to cancel."

**If Approved - Register Service**:

The user can choose between manual registration or declarative registration:

**Option 1: Manual Service Registration** (separate step):
```
mcp_ev2-mcp_register_service(
  serviceSpecFilePath="<path-to-serviceSpec.json>",
  endpoint="Test"  // or "Prod"
)
```

**Option 3: Declarative Registration** (recommended - combines service + artifact registration):
Continue to Phase 5 (artifact registration will trigger service registration automatically)

**Verify Registration**:
```
mcp_ev2-mcp_get_service_info(
  serviceId="<Service-Tree-GUID>",
  endpoint="Test"  // or "Prod"
)
```

**Expected Output**:
- Service registration details
- Service group information
- Owner group validation

---

### Phase 5: Artifact Validation (Static)

**Before Validation - Compilation Reminder**:
> **CRITICAL CHECK**: "Have you compiled your Bicep templates to JSON? The following command should have been run:
> 
> `bicep build templates/infrastructure.bicep --outfile templates/infrastructure.template.json`
> 
> Validation will fail if the compiled `.template.json` file doesn't exist."

**Static Validation** (NO registration required):
```
mcp_ev2-mcp_validate_region_agnostic_artifacts(
  serviceGroupRoot="<path-to-artifact-root>",
  rolloutSpecPath="rolloutSpec.json"
)
```

**Validation Coverage**:
- JSON schema compliance for all artifacts
- File existence checks (all referenced templates and parameters)
- Orchestration graph integrity (no circular dependencies)
- Scope binding references (scopeTagName alignment)
- Resource naming conventions

**Iterate on Errors**:

If validation fails:

1. **Review Error Messages**: Identify which artifact and what violation
2. **Query Knowledge Base**:
   ```
   mcp_ev2-mcp_ev2_knowledge_search(query="<specific error message>")
   ```
3. **Retrieve Schema** (if schema violation):
   ```
   mcp_ev2-mcp_get_schema_ev2(schemaName="<relevant-schema>")
   ```
4. **Fix Artifact**: Correct the issue
5. **Re-Validate**: Run validation again with SAME version (1.0.0)
6. **Repeat**: Continue until validation passes

**DO NOT increment version.txt during iteration**. Only increment after validation passes.

---

### Phase 6: Artifact Registration (Requires User Approval)

**Once Static Validation Passes**:

**USER APPROVAL REQUIRED**:
Ask: "Static validation passed! Ready to register artifacts with EV2? This will:
1. Register artifact version 1.0.0 with EV2
2. Enable validation rollouts and deployment rollouts
3. Create a versioned snapshot of your artifacts

Type 'yes' to proceed or 'no' to cancel."

**If Approved - Register Artifacts**:
```
mcp_ev2-mcp_register_region_agnostic_artifacts(
  serviceGroupRoot="<path-to-artifact-root>",
  rolloutSpecPath="rolloutSpec.json",
  endpoint="Test",  // or "Prod"
  forceRegistration=false
)
```

**Important Notes**:
- `forceRegistration=false`: Prevents accidental overwrites
- Each registration creates an immutable artifact version
- Version in `version.txt` must be unique (never reuse)

**Verify Registration**:
```
mcp_ev2-mcp_get_artifacts(
  serviceId="<Service-Tree-GUID>",
  serviceGroupName="Microsoft.<Category>.<Service>.<Group>",
  endpoint="Test"
)
```

**Expected Output**:
- Registered artifact versions
- Registration timestamp
- Artifact metadata

---

### Phase 7: Dynamic Validation (Validation Rollout)

**Explain Dynamic Validation**:
> "Dynamic validation simulates a real rollout without deploying resources. It validates:
> - Orchestration ordering and stage progression
> - Scope binding resolution across regions
> - ARM template parameter binding
> - Subscription and permission checks
> 
> This is the final validation step before production deployment."

**Start Validation Rollout**:
```
mcp_ev2-mcp_start_validation_rollout(
  serviceId="<Service-Tree-GUID>",
  serviceGroupName="Microsoft.<Category>.<Service>.<Group>",
  stageMapName="Microsoft.Azure.SDP.Standard",
  selectionScope=["regions(<initial-regions>)"],
  endpoint="Test",
  artifactVersionNumber="1.0.0"
)
```

**Selection Scope Examples**:
- Single region: `["regions(centralus)"]`
- Multiple regions: `["regions(centralus,eastus2)"]`
- All regions: `["regions(*)"]`
- Specific steps: `["regions(centralus).steps(DeployInfrastructure)"]`

**Monitor Validation Rollout**:

1. **Check Rollout Status**:
   ```
   mcp_ev2-mcp_get_latest_rollouts(
     serviceId="<Service-Tree-GUID>",
     serviceGroupName="Microsoft.<Category>.<Service>.<Group>",
     endpoint="Test",
     rolloutCount=1
   )
   ```

2. **Get Detailed Results**:
   ```
   mcp_ev2-mcp_get_rollout_summary(
     rolloutId="<rollout-id-from-previous-call>",
     serviceGroupName="Microsoft.<Category>.<Service>.<Group>",
     endpoint="Test"
   )
   ```

**Handle Validation Failures**:

If validation rollout fails:

1. **Analyze Failure Reason**: Review error messages from rollout details
2. **Query Knowledge Base**:
   ```
   mcp_ev2-mcp_ev2_knowledge_search(query="<specific error message>")
   ```
3. **Fix Artifacts**: Correct the identified issues
4. **Increment Version**: Update `version.txt` to a new unique version (e.g., "1.0.1")
5. **Re-Register**: Register artifacts again with the new version
6. **Re-Validate**: Start a new validation rollout with the new version

**Iterate until validation rollout succeeds.**

---

### Phase 8: Configuration Registration (Optional)

**Explain Configuration Management**:
> "EV2 configuration allows you to define environment-specific or region-specific settings that can be referenced in scope bindings using `$config(SettingName)`. This is useful for values that change across environments but don't warrant separate parameter files."

**Common Configuration Settings**:
- SKU sizes (e.g., Standard_LRS vs. Premium_LRS)
- Feature flags
- Resource name prefixes
- Network configurations
- Connection strings (non-sensitive parts)

**Configuration Levels**:
- **Service-level**: Applies to all service groups
- **Service Group-level**: Applies to specific service group
- **Ring-level**: Applies to specific deployment ring (e.g., Canary, Production)

**Register Configuration**:

Create `configurationSpec.json`:
```json
{
  "$schema": "https://ev2schema.azure.net/schemas/2020-01-01/configurationSpecification.json",
  "contentVersion": "1.0.0.0",
  "configurations": {
    "StorageAccountSku": {
      "value": "Standard_LRS",
      "description": "Default storage account SKU"
    },
    "StorageAccountNamePrefix": {
      "value": "myservice",
      "description": "Storage account name prefix"
    }
  }
}
```

**Register with EV2**:
```
mcp_ev2-mcp_set_registered_configuration(
  serviceId="<Service-Tree-GUID>",
  serviceGroup="Microsoft.<Category>.<Service>.<Group>",
  configSpecFilePath="configurationSpec.json",
  endpoint="Test"
)
```

---

### Phase 9: Stage Map Registration (Only if Custom Required)

**Most services should use the standard stage map**. Skip this phase unless you have specific orchestration requirements.

**When to Use Custom Stage Map**:
- Non-standard region progression
- Custom stamp isolation requirements
- Specific concurrency constraints

**Retrieve Standard Stage Map** (for reference):
```
mcp_ev2-mcp_get_standard_stagemap(
  stageMapName="Microsoft.Azure.SDP.Standard",
  endpoint="Test"
)
```

**If Custom Stage Map Required**:

1. **Author Stage Map JSON** (complex, consult EV2 documentation)
2. **Register Stage Map**:
   ```
   mcp_ev2-mcp_register_stage_map(
     serviceId="<Service-Tree-GUID>",
     serviceGroup="Microsoft.<Category>.<Service>.<Group>",
     stageMapFilePath="<path-to-stageMap.json>",
     endpoint="Test"
   )
   ```

---

### Phase 10: Summary and Next Steps

**Provide Comprehensive Summary**:

Generate a detailed report including:

1. **Service Registration**:
   - Service Tree ID
   - Service Group Name
   - EV2 Environment (Test/Prod)
   - Registration timestamp

2. **Artifact Summary**:
   - Registered artifact version
   - Artifact structure (list all files created)
   - Infrastructure components (list Bicep templates and resources)

3. **Validation Results**:
   - Static validation: PASSED
   - Dynamic validation: PASSED (if completed)

4. **Configuration**:
   - Registered configuration settings (if any)
   - Scope binding strategy

5. **Files Created**:
   ```
   ├── version.txt (version: 1.0.0)
   ├── serviceSpec.json
   ├── serviceGroupSpec.json
   ├── serviceModel.json
   ├── rolloutSpec.json
   ├── scopeBindings.json
   ├── templates/
   │   ├── infrastructure.bicep
   │   └── infrastructure.template.json
   └── parameters/
       └── infrastructure.parameters.json
   ```

6. **Next Steps**:

   a. **Production Readiness**:
      - Review all artifacts for security and compliance
      - Test in Test environment thoroughly
      - Document rollback procedures
      - Set up monitoring and alerting

   b. **Production Registration** (when ready):
      ```
      # Update endpoint to "Prod" and re-register
      mcp_ev2-mcp_register_region_agnostic_artifacts(
        serviceGroupRoot="<path>",
        rolloutSpecPath="rolloutSpec.json",
        endpoint="Prod"
      )
      ```

   c. **First Production Deployment**:
      ```
      mcp_ev2-mcp_start_rollout(
        serviceId="<Service-Tree-GUID>",
        serviceGroupName="Microsoft.<Category>.<Service>.<Group>",
        stageMapName="Microsoft.Azure.SDP.Standard",
        selectionScope=["regions(centralus)"],  // Start with one region
        endpoint="Prod",
        artifactVersionNumber="1.0.0"
      )
      ```

   d. **Monitoring**:
      - Use `/Octane.Ev2Deployer.Monitor` prompt to track deployment progress
      - Monitor EV2 rollout status
      - Watch Azure DevOps pipeline execution

   e. **Iteration**:
      - When making changes, increment version.txt
      - Re-validate and re-register artifacts
      - Start new rollout with updated version

7. **Important Reminders**:
   - ✅ Bicep templates MUST be compiled before registration
   - ✅ Each registration requires a unique version number
   - ✅ Validation rollouts don't deploy resources (safe to run)
   - ✅ Production rollouts require additional approval controls
   - ✅ Use `forceRegistration=false` to prevent accidental overwrites

8. **Useful Commands for Future Updates**:
   ```bash
   # Compile Bicep templates
   bicep build templates/infrastructure.bicep --outfile templates/infrastructure.template.json

   # Validate artifacts (iterate until clean)
   # Use mcp_ev2-mcp_validate_region_agnostic_artifacts

   # Increment version (after validation passes)
   # Update version.txt to unique value

   # Register updated artifacts
   # Use mcp_ev2-mcp_register_region_agnostic_artifacts

   # Start validation rollout
   # Use mcp_ev2-mcp_start_validation_rollout
   ```

---

## COMMUNICATION PRINCIPLES

Throughout this workflow:

1. **Be Explanatory**: This may be the user's first EV2 service. Explain concepts clearly.

2. **Validate Continuously**: Don't skip validation steps. They catch issues early.

3. **Request Approval**: ALWAYS get approval before:
   - Service registration
   - Artifact registration
   - Validation rollout execution

4. **Use Knowledge Search**: When encountering errors, query `mcp_ev2-mcp_ev2_knowledge_search` for authoritative guidance.

5. **Iterate Patiently**: Validation failures are normal. Fix issues systematically.

6. **Document Decisions**: Explain why certain patterns are chosen (e.g., standard stage map vs. custom).

7. **Provide Examples**: Use concrete examples with placeholder values clearly marked.

8. **Emphasize Safety**: Highlight validation-first approach and non-destructive operations.

---

## TROUBLESHOOTING COMMON ISSUES

### Issue: "Service registration failed - permission denied"

**Cause**: User is not a member of the owner security group specified in Service Tree.

**Resolution**:
1. Verify user's security group membership
2. Confirm security group ObjectId matches Service Tree configuration
3. Ensure group is in the same tenant as EV2 endpoint

---

### Issue: "Validation failed - template file not found"

**Cause**: Bicep template not compiled to JSON, or path mismatch.

**Resolution**:
1. Verify Bicep compilation: `bicep build templates/infrastructure.bicep --outfile templates/infrastructure.template.json`
2. Check `serviceModel.json` references correct `.template.json` path (NOT `.bicep`)
3. Verify file exists at referenced path

---

### Issue: "Validation failed - scope tag not found"

**Cause**: Mismatch between `scopeTags` in serviceModel and `scopeTagName` in scopeBindings.

**Resolution**:
1. Review serviceModel: find `scopeTags` array in service resource groups
2. Review scopeBindings: find `scopeTagName` fields
3. Ensure exact string match (case-sensitive)

---

### Issue: "Registration failed - version already exists"

**Cause**: Attempting to register with a version number that was previously registered.

**Resolution**:
1. Increment `version.txt` to a unique value
2. Re-register artifacts
3. Version numbers cannot be reused, even after failed registrations

---

### Issue: "Dynamic validation failed - insufficient permissions"

**Cause**: Service principal lacks permissions on target subscription.

**Resolution**:
1. Verify subscription registration with EV2
2. Confirm admin SPN has necessary permissions
3. Check Service Tree subscription configuration

---

## SUCCESS CRITERIA

This workflow succeeds when:

- ✅ All artifacts created and follow EV2 best practices
- ✅ Service registered with EV2 (one-time)
- ✅ Artifacts registered with unique version
- ✅ Static validation passes without errors
- ✅ Dynamic validation rollout completes successfully
- ✅ User understands next steps for production deployment
- ✅ Documentation provided for future maintenance

---

## AGENT RESPONSIBILITIES RECAP

As Ev2Author, you must:

1. ✅ Call `mcp_ev2-mcp_get_ev2_best_practices` FIRST
2. ✅ Call `mcp_bicep_experim_get_bicep_best_practices` if authoring Bicep
3. ✅ Gather all required information before proceeding
4. ✅ Create well-structured, schema-compliant artifacts
5. ✅ Validate early and often (static before registration)
6. ✅ Request user approval for registration operations
7. ✅ Iterate patiently on validation failures
8. ✅ Query knowledge base for unfamiliar errors
9. ✅ Provide comprehensive summary and next steps
10. ✅ Emphasize safety and best practices throughout

---

## FINAL NOTES

- **Trust EV2 Validation**: Validation errors are authoritative. Fix issues, don't bypass.
- **Version Uniqueness**: Every registration needs a unique version. Never reuse.
- **Compilation Reminder**: Bicep files must be compiled. Check before each validation.
- **Approval Gates**: Service registration and artifact registration modify EV2 state. Always require explicit user consent.
- **Knowledge Search**: When in doubt, search EV2 knowledge base. Don't guess.

---

# Task

Create a new EV2 service from scratch, including all required artifacts, infrastructure templates, validation, and registration. Follow the validation-driven workflow systematically. Request approval before registration operations. Provide comprehensive guidance and troubleshooting support throughout the process.
