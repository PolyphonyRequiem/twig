"""Run named, selector-verified CI checks from the repository root."""

import argparse
import os
from pathlib import Path
import subprocess
import sys


def native_authority():
    for project, selector in [
        ("Twig.Providers.Native.Tests", "FullyQualifiedName~NativeProviderTests"),
        ("Twig.Daemon.Tests", "FullyQualifiedName~CanvasPackageTests|FullyQualifiedName~DaemonLauncherTests|FullyQualifiedName~NativeMetadata"),
        ("Twig.Cli.Tests", "FullyQualifiedName~NativeTwigSpineTests"),
    ]:
        if os.name == "nt":
            if project == "Twig.Daemon.Tests":
                selector += "|FullyQualifiedName~CommandPipeSecurityTests"
            elif project == "Twig.Cli.Tests":
                selector += "|FullyQualifiedName~CommandPipeClientTests"
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        f"tests/{project}/{project}.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName={project}.trx",
                        "--results-directory", "artifacts/test-logs/ci/native-authority"], check=True)


def native_setup():
    groups = [
        ("setup-infrastructure", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeConnectionSetupTests|FullyQualifiedName~NativeSetupBinding|FullyQualifiedName~NativeSetupPkce|FullyQualifiedName~NativeSetupCallback"),
        ("setup-discovery", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeAdoSetupDiscoveryTests"),
        ("setup-http", "Twig.Daemon.Tests", "FullyQualifiedName~NativeAdoSetupDiscoveryHttpTests|FullyQualifiedName~ManagedAdoSetupTests"),
        ("setup-cli", "Twig.Cli.Tests", "FullyQualifiedName~NativeConnectionSetupCommandTests|FullyQualifiedName~NativeSetupFullInitPreflight|FullyQualifiedName~NativeSetupAdo|FullyQualifiedName~NativeSetupGitHub"),
        ("setup-managed-ado", "Twig.Cli.Tests", "FullyQualifiedName~NativeManagedAdo"),
        ("setup-native-databases", "Twig.Cli.Tests", "FullyQualifiedName~NativeDatabaseTests"),
    ]
    for label, project, selector in groups:
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        f"tests/{project}/{project}.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName={label}.trx",
                        "--results-directory", "artifacts/test-logs/ci/native-setup"], check=True)


def registry_compat():
    for version, revision in [(10, "cbbf800dab1a8e502f568642d81e5c3c2bf3fa3c"),
                              (11, "710056002c73c6409eb05318bdba422e84eaba47")]:
        legacy = Path("artifacts") / f"legacy-core-v{version}"
        subprocess.run(["git", "worktree", "add", "--detach", str(legacy), revision], check=True)
        subprocess.run([sys.executable, "eng/run-bounded.py", "--timeout", "120", "--",
                        "dotnet", "build", str(legacy / "src" / "Twig.Infrastructure" / "Twig.Infrastructure.csproj"),
                        "-f", "net11.0", "-m:1", "--verbosity", "minimal"], check=True)
        subprocess.run([sys.executable, "eng/verify-core-registry-compat.py", "--legacy-version", str(version),
                        "--legacy-assembly", str(legacy / "src" / "Twig.Infrastructure" / "bin" / "Debug" / "net11.0" / "Twig.Infrastructure.dll"),
                        "--current-assembly", str(Path("src") / "Twig.Infrastructure" / "bin" / "Debug" / "net11.0" / "Twig.Infrastructure.dll")], check=True)


def catalog():
    groups = [
        ("catalog-help", ["ClosedCafDeclarationsDoNotAdvertiseRetiredCommands"]),
        ("no-marker", ["NoMarkerCoreDataReadsRefuseWithoutLegacyGraphOrInputIo"]),
        ("catalog-owners", ["ClosedProductionCatalogHasOnlyCoreDataAndNativeHostOwners"]),
        ("retired-caf", ["ClosedCatalogRefusesRetiredCafRoutesBeforeComposition", "RestoredPlanningRoutesStillRejectRetiredUiEnvelopesBeforeComposition", "RetiredRootLeavesNeverResolveLegacyCommandsOrLaunchers"]),
        ("native-host", ["NativeHostUtilitiesUseRealNamedCredentialAndBindingWithoutLegacyWorkspace"]),
        ("ado-init", ["CoreInitCliCreatesAnAdmittedWorkspaceAndRefusesReinitializationWithoutChangingIt"]),
        ("ado-init-identity", ["CoreInitializationDerivesNativeIdentityAndNeverReusesAnExistingDatabase"]),
        ("ado-init-refusals", ["CoreInitNativeAuthorityFailureNeverCreatesAStoreOrReusesPartialEvidence", "LegacyWorkspaceMutationIsRefusedBeforeProductionWriterRegistration"]),
    ]
    for label, methods in groups:
        selector = "|".join(f"FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.{method}" for method in methods)
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName={label}.trx",
                        "--results-directory", "artifacts/test-logs/ci/catalog"], check=True)
    subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", "FullyQualifiedName=Twig.Cli.Tests.Architecture.HostContractOwnershipTests.Compiled_native_assembly_has_no_legacy_domain_dependency",
                        "--logger", "trx;LogFileName=compiled-domain-dependency.trx",
                        "--results-directory", "artifacts/test-logs/ci/catalog"], check=True)
    subprocess.run([sys.executable, "eng/verify-domain-cut.py"], check=True)
    subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                    "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                    "--filter", "FullyQualifiedName=Twig.Cli.Tests.Architecture.HostContractOwnershipTests.Compiled_implementation_uses_host_owned_contract",
                    "--logger", "trx;LogFileName=host-contract-ownership.trx",
                    "--results-directory", "artifacts/test-logs/ci/catalog"], check=True)


def user_benches():
    os.environ["PYTHON"] = sys.executable
    for label, project, selector in [
        ("user-bench-store", "Twig.Infrastructure.Tests", "FullyQualifiedName~UserBenchStoreTests"),
        ("native-plan-scopes", "Twig.Infrastructure.Tests", "FullyQualifiedName~Twig.Infrastructure.Tests.Services.NativePlanStoreTests|FullyQualifiedName~Twig.Infrastructure.Tests.Services.NativePlanScopeContractTests"),
        ("native-core-execution", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeCoreExecutionTests|FullyQualifiedName~NativeCoreRegistryMigrationTests"),
        ("native-publishing-gate", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublishingBeforeActivationIsRefusedByTheActualCliComposition|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeUnknownWriteBlocksAnotherInitializedRootNormalSaveWithoutMutationHttp|FullyQualifiedName=Twig.Cli.Tests.Commands.GitHubCoreSpineTests.NativeUnknownGitHubWriteFencesAnotherRootNormalSaveThroughRealComposition"),
        ("native-no-effect", "Twig.Cli.Tests", "FullyQualifiedName~NativeHttpRejectionPermitsOnlyFreshReviewedNoEffectEdits|FullyQualifiedName~NativeGitHubHttpRejectionPermitsOnlyFreshReviewedNoEffectEdits"),
        ("native-compound-editor", "Twig.Cli.Tests", "FullyQualifiedName~NativeCompoundEditorFieldsAndRelatedDelta"),
        ("native-authoring-contract", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeAuthoringContractTests"),
        ("native-scope-birth", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeScopeBirthTests"),
        ("native-authoring-leases", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeEditorStoreGuardsTests"),
        ("native-ado-fresh-acquisition", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoAcquisitionResolvesFreshSourceOnceAtEachEntry|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoAcquisitionWithinLiveAncestorStillRefusesChangedSource|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoFreshChildRetainsPhysicalLeaseAfterAncestorDisposal|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoAcquisitionWrongResolverCannotBorrowAuthority|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoAcquisitionCancellationReleasesItsLease|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoAcquisitionDifferentPreferenceContextKeepsFullValidation|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeAdoApplyingAdmissionStillRevalidatesAfterDurableAttempt"),
        ("native-ado-fresh-acquisition-workflow", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeFreshAcquisitionPreservesFullOutgoingTitleDebtAndRemoval"),
        ("native-editor-roundtrip", "Twig.Cli.Tests", "FullyQualifiedName~NativeNormalRelationshipCaptureSurvivesReopen|FullyQualifiedName~NativePlanCaptureIsIsolated"),
        ("native-editor-authority", "Twig.Cli.Tests", "FullyQualifiedName~NativeEditorCapturedAuthorityAndVersions|FullyQualifiedName~NativeEditorExactSourceStoreAndUserInstance|FullyQualifiedName~NativeEditorBaselineFingerprint"),
        ("native-editor-recovery", "Twig.Cli.Tests", "FullyQualifiedName~NativeEditorInterruptedCapture|FullyQualifiedName~NativeEditorMissingOrCorruptCapture|FullyQualifiedName~NativeEditorCachedGuidance|FullyQualifiedName~NativeEditorPendingSafetyCheck"),
        ("native-editor-persistence", "Twig.Cli.Tests", "FullyQualifiedName~NativeEditorJointlyMissingCaptureTables|FullyQualifiedName~NativeEditorStoredDocumentBound"),
        ("native-capture-schema", "Twig.Infrastructure.Tests", "FullyQualifiedName~NativeCaptureRegistryMigrationTests"),
        ("native-human-editor", "Twig.Cli.Tests", "FullyQualifiedName~NativeHumanEditorPublishes|FullyQualifiedName~NativeHumanEditorBridge|FullyQualifiedName~NativeHumanEditorReuses"),
        ("native-human-editor-guards", "Twig.Cli.Tests", "FullyQualifiedName~NativeHumanEditorFreshGuards|FullyQualifiedName~NativeHumanEditorNormalSaveNeverFlushes"),
        ("native-public-relationships-reopen", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipIntentReopensWithoutScalarPublication"),
        ("native-public-relationships-association", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsStageReopenAndSaveOneAtomicEffect&DisplayName~Native.Association"),
        ("native-public-relationships-dependency", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsStageReopenAndSaveOneAtomicEffect&DisplayName~Native.DependencyForward"),
        ("native-public-relationships-hierarchy", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsStageReopenAndSaveOneAtomicEffect&DisplayName~Native.ParentForward"),
        ("native-public-relationships-revisions", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~endpoint|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~target|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~schema"),
        ("native-public-relationships-authority", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~actor|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~mode|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~bench"),
        ("native-public-relationships-debt", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~draft|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~incident-debt|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~ticket"),
        ("native-public-relationships-capture", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~store|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~source|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture&DisplayName~witness"),
        ("native-public-relationships-late-proof", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsLateEndpointReadCannotRecaptureDisplayedEvidence"),
        ("native-public-relationships-evaluation", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicQualifiedRelationshipEvaluationShowsCloseGatesWithoutStaging"),
        ("native-public-relationships-evaluation-guards", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicQualifiedRelationshipEvaluationRefusesInvalidatedEvidence"),
        ("native-public-relationships-direction", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsRejectWrongDirectionPresenceAndEndpointBeforeStaging"),
        ("native-public-relationships-policy", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsRefuseParentReplacementAndDependencyCycleThroughCore"),
        ("native-public-relationships-unavailable", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsUnavailableMetadataAndEndpointNeverBecomeEmptySuccess"),
        ("native-public-relationships-uncertain", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsOriginalUncertainReceiptIsProtectedWithoutReplay"),
        ("native-public-relationships-isolation", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativePublicRelationshipsDoNotAdoptCliDebtPlanSeedsOrAnotherBench"),
        ("native-normal-seed-delivery", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeNormalSeedLifecycleUsesOneOwnedUuidAndVerifiedRemoteBenchPin|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeNormalSeedVerifiedDeliveryReopensOriginalReferenceWithoutAnotherCreate"),
        ("native-normal-seed-protection", "Twig.Cli.Tests", "FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeNormalSeedLostResponseProtectsOriginalUuidAndNeverGuessesOrRecreates|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeNormalOwnedSeedEnumerationDoesNotAdoptCliPlanOrAnotherBenchDebt|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeNormalSeedFreshAuthoringGuardsRefuseBeforeCreate|FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.NativeNormalSeedCompletionRequirednessUsesCreationNotRemoteTransition"),
        ("native-human-plan-open", "Twig.Cli.Tests", "FullyQualifiedName~NativeHumanPlanEditorOpensOwnedScopeWithoutPublishingOrTouchingNormalDraft"),
        ("native-human-plan-stage-seed", "Twig.Cli.Tests", "FullyQualifiedName~NativeHumanPlanStagesAcrossItemsAndSeedsAndReopensWithoutCopyingNormalIntent"),
        ("native-plan-digest-delivery", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanRetainsExactCoreDigestsAndDeliversWithoutFlushingNormalIntent"),
        ("native-plan-stale-review", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanStaleReviewRefusesBeforeFirstDispatch"),
        ("native-plan-receipt-delivery", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanOriginalReceiptsFenceReplayAndAllowDeliveryOnly"),
        ("native-plan-seed-pin", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanSeedDeliveryKeepsItsOwnedUuidAndAddsOnlyVerifiedRemotePin"),
        ("native-plan-last-scope", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanLastScopeDriftStopsBothScopesBeforeDispatch"),
        ("native-plan-two-scope-prefix", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanTwoScopesProtectVerifiedPrefixAndNeverReplayAfterRestart"),
        ("native-plan-untouched-remainder", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanVerifiedPrefixDoesNotBrickProvenUntouchedLaterScope"),
        ("native-plan-admission-window", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanAdmissionWithoutAnyCoreAttemptCanBeFreshlyReviewed"),
        ("native-plan-repeated-target", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanCompletedScopeCanPublishNewIntentToSameItemWithoutReplayingOldEffect"),
        ("native-plan-converged-settlement", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanConvergedNoEffectSettlesAfterRestartWithoutInventingRemoteReceipt"),
        ("native-plan-review-metadata", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanReviewDiscoversMetadataOncePerExactScope"),
        ("native-plan-local-cleanup", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanVerifiedEffectsWithFailedLocalCleanupSettleExactlyWithoutDispatch"),
        ("native-plan-definitive-edit", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanDefinitiveNoEffectEditRequiresNewReviewAndFreshRestoredAccess"),
        ("native-plan-refused-seed", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanDefinitivelyRefusedCreationUuidIsNeverRecreated"),
        ("native-plan-same-scope-prefix", "Twig.Cli.Tests", "FullyQualifiedName~NativeWholePlanSameScopePartialPrefixDeliversOnlyVerifiedCaptureAndNeverReplaysIt"),
        ("native-capture-rewind", "Twig.Cli.Tests", "FullyQualifiedName~NativeCaptureWholeDatabaseRewind"),
        ("native-capture-lifecycle", "Twig.Cli.Tests", "FullyQualifiedName~NativeCaptureInterruptedBeforeCoreStage|FullyQualifiedName~NativeCaptureUserCommitBeforeRegistryConfirmation|FullyQualifiedName~NativeCaptureReserveAndPostCoreCrash"),
        ("native-capture-bootstrap", "Twig.Cli.Tests", "FullyQualifiedName~NativeCaptureWithoutCreationWitnessRefusesHistoricalAdoption|FullyQualifiedName~NativeCaptureMissingBidirectionalHistory"),
        ("native-gh-editor", "Twig.Cli.Tests", "FullyQualifiedName~NativeGitHubEditorCaptures"),
        ("native-gh-human-scalars", "Twig.Cli.Tests", "FullyQualifiedName~NativeGitHubHumanEditorSavesScalarsAndExplicitMarkdownClearThroughCore|FullyQualifiedName~NativeGitHubHumanEditorOnlyOffersCapturedScalarInputs"),
        ("native-gh-human-plan", "Twig.Cli.Tests", "FullyQualifiedName~NativeGitHubHumanPlanStagesScalarsAndNormalSaveNeverFlushesPlanDebt"),
        ("native-gh-human-guards", "Twig.Cli.Tests", "FullyQualifiedName~NativeGitHubHumanEditorFreshGuardsRefuseBeforeMutation"),
        ("native-gh-human-protected", "Twig.Cli.Tests", "FullyQualifiedName~NativeGitHubHumanEditorRefusesCollectionsAndRetainsProtectedIntent"),
        ("native-gh-human-uncertain", "Twig.Cli.Tests", "FullyQualifiedName~NativeGitHubHumanEditorUncertainSaveKeepsOriginalReceiptAndNeverRetries"),
        ("planning-consumers", "Twig.Infrastructure.Tests", "FullyQualifiedName~PlanningConsumerTests"),
        ("native-team-planning", "Twig.Cli.Tests", "FullyQualifiedName~NativeExplicitTeamPlanningRefreshBrowseAndPull|FullyQualifiedName~NativeExplicitProjectPlanning|FullyQualifiedName~NativeExplicitTeamPlanningRead|FullyQualifiedName~NativeExplicitTeamPlanningBacklog"),
        ("native-team-planning-guards", "Twig.Cli.Tests", "FullyQualifiedName~NativeExplicitTeamPlanningRefuses|FullyQualifiedName~NativeRetainedTeamCalendars|FullyQualifiedName~NativeSourcePlanningBrowseRevalidatesAuthority"),
        ("native-source-catalogue-cache", "Twig.Cli.Tests", "FullyQualifiedName~NativeSourceCatalogueRefreshDoesNotAcquireProcessSchemaOrExecuteItems|FullyQualifiedName~NativeSourceCatalogueCachePersists"),
        ("native-source-catalogue-refresh-guards", "Twig.Cli.Tests", "FullyQualifiedName~NativeSourceCatalogueFailedRefresh"),
        ("native-source-catalogue-authority", "Twig.Cli.Tests", "FullyQualifiedName~NativeSourceCatalogueAuthorityReplacement"),
        ("native-source-lightweight-execution", "Twig.Cli.Tests", "FullyQualifiedName~NativeSourceCatalogueLightweightExecution"),
        ("user-bench-cli", "Twig.Cli.Tests", "FullyQualifiedName~UserBenchCommandTests|FullyQualifiedName~UserWideBenchEntrypointTests"),
    ]:
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        f"tests/{project}/{project}.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName={label}.trx",
                        "--results-directory", "artifacts/test-logs/ci/user-benches"], check=True)


def ado_root():
    methods = [
        "ExplicitCoreProductionRootReadsAndUpdatesThroughDraftDigestJournalWithoutLegacyWriters",
        "CoreProcessInspectionUsesDiscoveredRulesWithoutLegacyCommandsOrWorkReads",
        "CoreProposalInspectionPreservesTypedDraftsAndExactJournalWithoutLegacyReaders",
        "CoreSaveUsesDraftJournalInsteadOfResolvingALegacyPendingFlusher",
        "CoreSeedPublicationUsesTheRetainedUuidAndPermanentVerifiedMapping",
        "CoreParentedSeedPublishesItsHierarchyInTheCreationCallWithoutAPostCreateWriter",
        "CoreChainAuthoringValidationAndReconciliationRetainOriginalCreationReferences",
        "CoreLinkRouteUsesNativeJournalWithoutLegacyGraph",
        "CoreLinkHumanActivePublishedUuidUsesPermanentMappingWithoutRewritingHistory",
        "CoreLinkMachineChildOptionRequiresIdBeforeAnyHttp",
        "CoreLinkRefusalsAndAmbientSelectionNeverUseLegacyOrDispatchUnsupportedIntent",
        "CoreLocalRelatedCannotFallbackFromNativeAdoAssociation",
        "CoreLinkUnresolvedReadbackFencesBothEndpointsWithoutReplay",
        "CoreLinkTelemetryUsesStaticMetadataAndCannotChangeTheOutcome",
        "CoreBenchCrudPreservesSelectorKindsAndNeverResolvesLegacyWriters",
        "CoreBenchPullKeepsDraftDebtAndExcludesImplicitMachineActiveContext",
        "CoreSyncUsesOneProposalAndOneJsonResultWithSafeOptionalTelemetry",
        "CoreActiveContextAndNativeEditorPreserveTargetingAndRealConcurrentFields",
        "CoreQueryFindsLaterPageMatchesAndPreservesUnpublishedDraftsWithoutWriters",
        "CoreWorkspaceReadRoutesThroughProductionComposition",
        "CoreWorkspaceReadResolvesSharedPlanningRulesFromNativeCaches",
        "CorePlanningCommandsPreserveAndOrPathTeamOwnershipAndWorkspaceExclusionSemantics",
        "CorePlanningCommandsPinNativeReferencesAndBrowseWithoutChangingSelectionOrPublishing",
        "CorePlanningSourceListingRevalidatesAuthorityAfterDiscoveryBeforeRendering",
        "CoreWorkspaceReadKeepsExactMembershipTypedDebtAndOneOutputAcrossFormats",
        "CoreWorkspaceReadRefusesUnsupportedShapesAndUntrustworthyScopeWithoutBroadening",
        "CoreWorkspaceReadRefreshUsesOnlySharedPullAndPreservesDebtAndMappedSelectors",
        "CleanCoreAdmissionRetainsRealNativeLeaseAndExactDurableAttemptAttribution",
        "CleanCoreAdmissionRechecksActualPolicyPresenceAndBytesAfterPreview",
        "CleanCoreAccessRefusesANewSelectedActorBeforeWorkTransport",
        "NativeOnlineReadUsesFreshRemoteAccessWithoutSchemaCrawlAndExplicitSchemaReadRemainsFull",
        "NativeOnlineReadRefusesPermissionFailureAndChangedRemotePrincipal",
        "NativeOnlineReadRequiresCapturedStampAndRevalidatesAuthorityAfterRemoteProbe",
        "NativeOnlineReadCancellationCannotReleaseContentOrRetainTheOperationLease",
    ]
    for method in methods:
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", f"FullyQualifiedName=Twig.Cli.Tests.Commands.NativePublicationConsumerTests.{method}",
                        "--logger", f"trx;LogFileName={method}.trx",
                        "--results-directory", "artifacts/test-logs/ci/ado-root"], check=True)


def github_root():
    groups = [
        ("online-read-access", ["NativeOnlineReadChecksActualRepositoryAccessWithoutSchemaOrItemAcquisition"]),
        ("init", ["FreshInitUsesOnlyNativeCoreBootstrap", "FreshInitTelemetryFailureCannotChangeNativeResult", "FreshCoreInitRequiresExplicitGhSelectionBeforeAnyHttp"]),
        ("creation-cache", ["FreshNativeRootCreatesOriginalUuidAndNumericCommandsNeverUseNumberAsNodeIdentity"]),
        ("markdown-close", ["NativeMarkdownAndActualCloseUseVerifiedObservedNonAtomicEffects"]),
        ("patch-save-bench-query", ["TypedPatchBenchAndBoundedQueryShareNativeNumbersAndRefuseUnqualifiedFamilies"]),
        ("offline-host", ["WorkspaceAliasUsesTheQualifiedOwnerAndVersionAndHelpAreOffline", "GitHubNativeHostStatusAndEvidenceNeverFallbackToAdoOrAcquireCredentials"]),
        ("retired", ["UnqualifiedMachineFamilyRefusesWithOneJsonErrorBeforeAnyHttp"]),
        ("number-refresh", ["CachedNumberMustBeRequalifiedBeforeExplicitRefreshOrMutation"]),
        ("number-save", ["CachedNumberMustBeRequalifiedBeforeSavingAnExplicitDraft", "CachedNumberMetadataMustBePresentAndUniqueWithoutLookupFallback"]),
        ("actor-repository-number", ["NativeBoundaryMismatchRefusesBeforeAnyMutation"]),
        ("unknown-restart", ["UnknownCreateSurvivesFreshRootReopenWithoutSecondCreateOrUuidReplacement", "GitHubTransportInspectionCorrelatesOriginalAttemptWithoutSettlingUnknownCreate"]),
        ("local-cas-aba", ["LocalRelatedUsesNativeNumbersAndVerifiedTombstoneCasWithoutNativeWrites"]),
        ("published-original-uuid", ["LocalRelatedKeepsOriginalEdgeKeysAcrossVerifiedSeedPublication"]),
        ("local-fences", ["LocalRelatedCannotBypassAnUnknownCreationWithANewDigest", "LocalRelatedRefusesUnknownTargetsAndAmbiguousCliWithoutEffects"]),
        ("native-local-selection", ["NativeGitHubPublishedPairQualifiesLocalOnlyAssociationWithoutScalarAuthority"]),
        ("native-local-selection-admission", ["NativeGitHubLocalAndScalarSelectionStillRefuseStaleAdmission"]),
        ("native-local-selection-debt", ["NativeGitHubLocalSelectionRefusesDebtIntroducedWhileReadingOtherEndpoint"]),
        ("native-local-selection-late-state", ["NativeGitHubLocalSelectionRechecksAfterCoherentProviderRead"]),
        ("native-local-provider-design", ["NativeGitHubProviderDesignDeclaresExactlyOneLocalOnlySymmetricAssociation"]),
        ("native-local-digest", ["NativeGitHubLocalReviewUsesExactDigestAndVerifiedCasWithoutScalarPublication"]),
        ("native-local-uuid-cas", ["NativeGitHubLocalReverseOrderRetainsOriginalUuidAndTombstoneAbaCas"]),
        ("native-local-applied-recovery", ["NativeGitHubLocalAppliedReceiptReopensAndVerifiesWithoutReplay"]),
        ("native-local-capture-witness", ["NativeGitHubLocalCaptureWitnessCannotBecomeAbsentIntent"]),
        ("native-local-explicit-scope", ["NativeGitHubLocalQualificationRejectsImplicitScopeAndPreservesMissingRemove"]),
        ("native-local-late-acquisition", ["NativeGitHubLocalLateAcquisitionCannotReplaceCapturedEndpointProof"]),
        ("native-local-read-rediscovery", ["NativeGitHubLocalReaderRediscoversOriginalPlannedAndSettledReviewWithoutCachedReference"]),
        ("native-local-read-preparing", ["NativeGitHubLocalReaderIncludesOriginalPreparingCaptureWithoutCreatingJournal"]),
        ("native-local-read-protected", ["NativeGitHubLocalReaderIncludesProtectedOriginalAttemptWithoutReplay"]),
        ("native-local-read-ownership", ["NativeGitHubLocalReaderDoesNotExposeAnotherBenchOrAdoptPlanMode"]),
        ("native-local-read-missing-proof", ["NativeGitHubLocalReaderMissingOriginalProofRefusesInsteadOfEmptySuccess"]),
        ("native-local-read-exact-target", ["NativeGitHubLocalExactReaderRefusesInvalidTargetsAndReleasesCancelledOrContendedLeases"]),
        ("native-local-read-exact-isolation", ["NativeGitHubLocalExactReaderEvaluatesOnlyNamedOriginalWhileAllRetainsOrderedRefusals"]),
        ("native-local-read-admission", ["NativeGitHubLocalOriginalReadersShareEntryAdmission"]),
        ("native-local-read-late-source", ["NativeGitHubLocalOriginalReadersStillRefuseSourceChangedAfterEntryAdmission"]),
    ]
    for label, methods in groups:
        selector = "|".join(f"FullyQualifiedName=Twig.Cli.Tests.Commands.GitHubCoreSpineTests.{method}" for method in methods)
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName={label}.trx",
                        "--results-directory", "artifacts/test-logs/ci/github-root"], check=True)
    for damage in ["actor", "repository", "schema", "endpoint", "mode", "store",
                   "draft", "policy", "denied", "bench", "edge"]:
        selector = ("FullyQualifiedName=Twig.Cli.Tests.Commands.GitHubCoreSpineTests."
                    "NativeGitHubLocalOriginalReviewRefusesInvalidatedProofWithoutReplacement"
                    f"&DisplayName~{damage}")
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName=native-local-proof-{damage}.trx",
                        "--results-directory", "artifacts/test-logs/ci/github-root"], check=True)
    for damage in ["actor", "repository", "bench", "mode", "store", "execution-path",
                   "cancelled-provenance", "endpoint-provenance", "revision-provenance", "history"]:
        selector = ("FullyQualifiedName=Twig.Cli.Tests.Commands.GitHubCoreSpineTests."
                    "NativeGitHubLocalExactReaderRevalidatesPersistedOriginalAfterVerifiedOperation"
                    f"&DisplayName~{damage}")
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName=native-local-read-exact-proof-{damage}.trx",
                        "--results-directory", "artifacts/test-logs/ci/github-root"], check=True)


def native_auth():
    groups = [
        ("github-core-host", "FullyQualifiedName~CleanCoreGitHubAccessTests"),
        ("creation-link-files", "FullyQualifiedName~CoreCreationLinkFileTests"),
        ("gh-credential", "FullyQualifiedName~GhCliTokenSourceTests"),
        ("aad-account", "FullyQualifiedName~AadAccountClaimTests|FullyQualifiedName~IdTokenDecoderTests|FullyQualifiedName~JwtAccessTokenInspectorTests|FullyQualifiedName~DeviceCodeFlowTests|FullyQualifiedName~LoopbackPkceFlowTests"),
        ("github-app", "FullyQualifiedName~GitHubAppAuthenticationTests|FullyQualifiedName~GitHubDeviceFlowClientTests"),
    ]
    for label, selector in groups:
        subprocess.run([sys.executable, "eng/verify-trx.py", "--", "dotnet", "test",
                        "tests/Twig.Infrastructure.Tests/Twig.Infrastructure.Tests.csproj", "--no-build", "--no-restore", "-f", "net11.0",
                        "--filter", selector, "--logger", f"trx;LogFileName={label}.trx",
                        "--results-directory", "artifacts/test-logs/ci/native-auth"], check=True)


CHECKS = {
    'native-authority': native_authority,
    'native-setup': native_setup,
    'registry-compat': registry_compat,
    'catalog': catalog,
    'user-benches': user_benches,
    'ado-root': ado_root,
    'github-root': github_root,
    'native-auth': native_auth,
}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('check', choices=CHECKS)
    CHECKS[parser.parse_args().check]()
