using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed partial class AdoProviderTests
{
    [Theory]
    [InlineData("copyFromCurrentUser", "Custom.Owner")]
    [InlineData("copyFromServerCurrentUser", "Custom.Owner")]
    [InlineData("copyFromServerClock", "Custom.Date")]
    [InlineData("setDefaultFromCurrentUser", "Custom.Owner")]
    [InlineData("setDefaultValue", "Custom.Unset")]
    [InlineData("setValueToEmpty", "Custom.Closure")]
    public async Task NativeEditorUnderstandsObservedDefaultCopyAndClearActions(string action, string target)
    {
        using var f = await Fixture.Create();
        f.Http.IncludeDateField = true;
        f.Http.NativeRulesJson = $$"""
            {"isDisabled":false,"conditions":[{"conditionType":"whenWas","field":"System.State","value":"{{f.Http.Initial}}"}],
             "actions":[{"actionType":"{{action}}","targetField":"{{target}}"{{(action == "setDefaultValue" ? ",\"value\":\"native default\"" : "")}}}]}
            """;
        f.Http.NativeRulesJson.ShouldContain(action);
        var metadata = await f.Provider.CaptureEditorMetadataAsync(f.Connection, default);
        metadata.IsSuccess.ShouldBeTrue(metadata.IsSuccess ? "" : metadata.Error.Message);
        var observed = await f.Provider.ReadEditorAsync(metadata.Value, [f.Key(1)], default);
        observed.IsSuccess.ShouldBeTrue();
        var source = observed.Value.Items.Single();
        source.Type.ShouldBe(f.Http.Type);
        source.Fields["System.State"].ShouldBe(FieldValue.String(f.Http.Initial));
        var opened = Core.Core.EvaluateEditorFields(metadata.Value.Schema, source, FieldValues.Empty, true);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        opened.Value.Errors.ShouldBeEmpty();
        opened.Value.States.ShouldContain(f.Http.Completed);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }

    [Fact]
    public async Task NativeEditorExactTypeIsNotBlockedByAnotherTypesMissingTransitionsButMutationRemainsBlocked()
    {
        using var f = await Fixture.Create();
        f.Http.AdditionalType = "Contoso.Other";
        f.Http.MissingTransitionsType = f.Http.AdditionalType;
        var metadata = (await f.Provider.CaptureEditorMetadataAsync(f.Connection, default)).Value;
        metadata.Schema.Types.Length.ShouldBe(2);
        metadata.Schema.Types.Single(type => type.Reference == f.Http.AdditionalType).Transitions.ShouldBeEmpty();
        var source = (await f.Provider.ReadEditorAsync(metadata, [f.Key(1)], default)).Value.Items.Single();
        var opened = Core.Core.EvaluateEditorFields(metadata.Schema, source, FieldValues.Empty, true);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        opened.Value.Errors.ShouldBeEmpty();
        opened.Value.States.ShouldBe([f.Http.Initial, f.Http.Completed]);
        var incomplete = Core.Core.EvaluateEditorFields(metadata.Schema,
            new(source.Key, f.Http.AdditionalType, source.Fields, source.Revision, source.RelationshipsComplete),
            FieldValues.Empty, true);
        incomplete.IsSuccess.ShouldBeFalse();
        incomplete.Error.Evidence.ShouldContain(value => value.Contains("exact type transitions unavailable"));
        var proposal = new Proposal(f.Connection, metadata.Schema.Fingerprint, 0,
            [new("edit", OpKind.Batch, source.Key, source.Revision,
                FieldValues.Empty.Set("Custom.Empty", FieldValue.String("edit")))]);
        Core.Core.Validate(proposal, proposal.Digest, metadata.Schema, await f.Stored(), f.Provider.Capabilities)
            .ShouldContain(error => error.Code == ErrorCode.Unavailable);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeStaticDefaultDoesNotOverwriteAuthoredValuesAndClearDoesNotSatisfyRequiredness()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "default-value";
        f.Http.NativeRulesJson = """
            {"isDisabled":false,"conditions":[],"actions":[
              {"actionType":"makeRequired","targetField":"Custom.Empty"},
              {"actionType":"makeRequired","targetField":"Custom.Closure"},
              {"actionType":"setValueToEmpty","targetField":"Custom.Closure"}]}
            """;
        var metadata = (await f.Provider.CaptureEditorMetadataAsync(f.Connection, default)).Value;
        var source = (await f.Provider.ReadEditorAsync(metadata, [f.Key(1)], default)).Value.Items.Single();
        source.Fields["Custom.Closure"].IsEmpty.ShouldBeFalse();
        source.Fields["Custom.Empty"].IsEmpty.ShouldBeTrue();
        var result = Core.Core.EvaluateEditorFields(metadata.Schema, source, FieldValues.Empty, true);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Fields.Single(field => field.Definition.Reference == "Custom.Empty").Required.ShouldBeFalse();
        result.Value.Fields.Single(field => field.Definition.Reference == "Custom.Closure").Required.ShouldBeTrue();
        result.Value.Errors.ShouldContain(error => error.Message.Contains("Required field 'Custom.Closure'"));
        var authored = Core.Core.EvaluateEditorFields(metadata.Schema, source,
            FieldValues.Empty.Set("Custom.Empty", FieldValue.String("authored")), true);
        authored.IsSuccess.ShouldBeTrue();
        authored.Value.Fields.Single(field => field.Definition.Reference == "Custom.Empty").Required.ShouldBeTrue();
        var whitespace = Core.Core.EvaluateEditorFields(metadata.Schema, source,
            FieldValues.Empty.Set("Custom.Empty", FieldValue.String(" ")), true);
        whitespace.IsSuccess.ShouldBeTrue();
        whitespace.Value.Errors.ShouldContain(error => error.Message.Contains("Required field 'Custom.Empty'"));
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("unknown-rule")]
    [InlineData("missing-transitions")]
    [InlineData("opaque-default-value")]
    public async Task NativeEditorStillRefusesIncompleteExactTypeWithDiagnosticEvidence(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = defect;
        var metadata = (await f.Provider.CaptureEditorMetadataAsync(f.Connection, default)).Value;
        var source = (await f.Provider.ReadEditorAsync(metadata, [f.Key(1)], default)).Value.Items.Single();
        var opened = Core.Core.EvaluateEditorFields(metadata.Schema, source, FieldValues.Empty, true);
        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Code.ShouldBe(ErrorCode.Unavailable);
        opened.Error.Evidence.ShouldNotBeEmpty();
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("wrong-kind")]
    [InlineData("unknown-condition")]
    [InlineData("conflicting-suppliers")]
    [InlineData("chained-predicate")]
    public async Task NativeEditorDoesNotCertifyInvalidOrUnresolvedNativeRuleEffects(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.NativeRulesJson = defect switch
        {
            "wrong-kind" => """
                {"isDisabled":false,"conditions":[],"actions":[{"actionType":"copyFromServerClock","targetField":"Custom.Owner"}]}
                """,
            "unknown-condition" => """
                {"isDisabled":false,"conditions":[{"conditionType":"unsupported","field":"System.State"}],
                 "actions":[{"actionType":"setValueToEmpty","targetField":"Custom.Closure"}]}
                """,
            "conflicting-suppliers" => """
                {"isDisabled":false,"conditions":[],"actions":[
                 {"actionType":"copyValue","targetField":"Custom.Closure","value":"one"},
                 {"actionType":"copyValue","targetField":"Custom.Closure","value":"two"}]}
                """,
            _ => """
                {"isDisabled":false,"conditions":[],"actions":[{"actionType":"copyValue","targetField":"Custom.Empty","value":"supplied"}]},
                {"isDisabled":false,"conditions":[{"conditionType":"when","field":"Custom.Empty","value":"supplied"}],
                 "actions":[{"actionType":"makeRequired","targetField":"Custom.Closure"}]}
                """,
        };
        var metadata = (await f.Provider.CaptureEditorMetadataAsync(f.Connection, default)).Value;
        var source = (await f.Provider.ReadEditorAsync(metadata, [f.Key(1)], default)).Value.Items.Single();
        source.Fields["Custom.Empty"].IsEmpty.ShouldBeTrue();
        metadata.Schema.Types.Single().Rules.Count(rule => !rule.Disabled).ShouldBeGreaterThan(0);
        var opened = Core.Core.EvaluateEditorFields(metadata.Schema, source, FieldValues.Empty, true);
        opened.IsSuccess.ShouldBeFalse();
        opened.Error.Code.ShouldBe(ErrorCode.Unavailable);
        opened.Error.Message.ShouldNotBeNullOrWhiteSpace();
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task TypeLocalGapIsFingerprintBoundAndStillBlocksPolicyCertification()
    {
        using var f = await Fixture.Create();
        var complete = await f.Schema();
        var type = complete.Types.Single();
        var incomplete = new ProcessSchema(complete.Connection, [type with { Gaps = ["native diagnostic"] }], complete.Relations);
        incomplete.Fingerprint.ShouldNotBe(complete.Fingerprint);
        incomplete.Gaps.ShouldBeEmpty("the diagnostic is structurally type-local, not reconstructed from prose");
        var findings = Core.Core.Evaluate(new([new("parent", RuleKind.ParentType, type.Reference, [type.Reference])]),
            new(0), new(0), incomplete);
        findings.ShouldContain(finding => finding.Kind == FindingKind.EvidenceUnavailable && finding.Blocks);
    }
}
