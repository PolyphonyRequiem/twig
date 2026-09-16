using System.Text.Json;
using Shouldly;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Process;

/// <summary>
/// Covers <see cref="ProcessDescriptionCompactProjection"/>: the opt-in compact envelope
/// shared by <c>twig process description</c> and its agent surface.
/// </summary>
/// <remarks>
/// <para>
/// A non-Hyperbright fixture (deliberately). The rules that gate the projection —
/// requiredness kinds, value-constraint kinds, rule customization tokens, supplier
/// semantics — are process-agnostic per repo policy; using Hyperbright wording here would
/// couple these tests to that one process by convention rather than testing the general
/// contract.
/// </para>
/// <para>
/// The fixture describes two types on a fictional <c>Twelvefold.*</c> process, chosen so
/// each behavioural axis has at least one witness: an always-required field, a
/// conditionally-required field with rule conditions, a picklist-constrained field, an
/// unresolved picklist source, a rule with a server default action that supplies a field,
/// and an unfetched sub-route.
/// </para>
/// </remarks>
public sealed class ProcessDescriptionCompactProjectionTests
{
    // ── Fixture ─────────────────────────────────────────────────────────────────────
    // A single fixture, built once, reused by tests. Keeping the assertions each about
    // one axis makes a failing test point at the actual regression instead of masking
    // several.

    private const string TypeAlpha = "Twelvefold.Alpha";
    private const string TypeBeta = "Twelvefold.Beta";

    private const string FieldTitle = "System.Title";       // always required, no default
    private const string FieldOutcome = "Custom.Outcome";   // conditionally required
    private const string FieldPriority = "Custom.Priority"; // picklist-constrained
    private const string FieldSlug = "Custom.Slug";         // picklist source unreadable
    private const string FieldReporter = "Custom.Reporter"; // rule-supplied via setDefault
    private const string FieldNotes = "Custom.Notes";       // never required, no default

    private static ProcessDescription BuildFixture(bool includeBetaUnfetched = true)
    {
        var titleField = new ProcessDescriptionField(
            FieldTitle,
            "Title",
            "string",
            DefaultValue: null,
            FieldRequiredness.Always,
            new FieldValueConstraint(FieldValueConstraintKind.Unconstrained, null, []),
            "system",
            IsLocked: false,
            Description: "The title.");

        var outcomeConditions = new List<FieldRequirednessCondition>
        {
            new(
                [
                    new FieldRequirednessClause("when", "System.State", "Done"),
                ]),
        };
        var outcomeField = new ProcessDescriptionField(
            FieldOutcome,
            "Outcome",
            "string",
            DefaultValue: null,
            new FieldRequiredness(FieldRequirednessKind.Conditional, outcomeConditions),
            new FieldValueConstraint(FieldValueConstraintKind.Unconstrained, null, []),
            "custom",
            IsLocked: false,
            Description: "The outcome.");

        var priorityField = new ProcessDescriptionField(
            FieldPriority,
            "Priority",
            "string",
            DefaultValue: "P3",
            FieldRequiredness.Always,
            new FieldValueConstraint(
                FieldValueConstraintKind.ListConstrained,
                "PriorityList",
                ["P1", "P2", "P3"]),
            "custom",
            IsLocked: false,
            Description: "The priority.");

        var slugField = new ProcessDescriptionField(
            FieldSlug,
            "Slug",
            "string",
            DefaultValue: null,
            FieldRequiredness.Always,
            new FieldValueConstraint(FieldValueConstraintKind.Unknown, "SlugList", []),
            "custom",
            IsLocked: false,
            Description: "A slug.");

        var reporterField = new ProcessDescriptionField(
            FieldReporter,
            "Reporter",
            "string",
            DefaultValue: null,
            FieldRequiredness.Always,
            new FieldValueConstraint(FieldValueConstraintKind.Unconstrained, null, []),
            "custom",
            IsLocked: false,
            Description: "The reporter.");

        var notesField = new ProcessDescriptionField(
            FieldNotes,
            "Notes",
            "html",
            DefaultValue: null,
            FieldRequiredness.Never,
            new FieldValueConstraint(FieldValueConstraintKind.Unconstrained, null, []),
            "custom",
            IsLocked: false,
            Description: "Optional notes.");

        var gateRule = new ProcessDescriptionRule(
            "Require outcome when done",
            RuleCustomization.From("custom"),
            IsDisabled: false,
            Conditions:
            [
                new RuleCondition("whenStateIs", "System.State", "Done"),
            ],
            Actions:
            [
                new RuleAction("makeRequired", FieldOutcome, Value: null),
            ]);

        var supplierRule = new ProcessDescriptionRule(
            "Supply reporter default",
            RuleCustomization.From("custom"),
            IsDisabled: false,
            Conditions: [],
            Actions:
            [
                new RuleAction("setDefault", FieldReporter, "unassigned"),
            ]);

        var states = new List<ProcessTypeState>
        {
            new("Active", "InProgress", 1, string.Empty, "system", IsHidden: false),
            new("Done", "Completed", 2, string.Empty, "system", IsHidden: false),
        };

        var alpha = new ProcessDescriptionType(
            TypeAlpha,
            "Alpha",
            "Alpha type.",
            "custom",
            Inherits: null,
            IsDisabled: false,
            Fields:
            [
                titleField,
                outcomeField,
                priorityField,
                slugField,
                reporterField,
                notesField,
            ],
            States: states,
            Transitions: [],
            Unfetched: [],
            Rules: [gateRule, supplierRule],
            Behaviours: [],
            Layout: null);

        var beta = new ProcessDescriptionType(
            TypeBeta,
            "Beta",
            "Beta type.",
            "inherited",
            Inherits: "System.Base",
            IsDisabled: false,
            Fields: [titleField],
            States: states,
            Transitions: [],
            Unfetched: includeBetaUnfetched ? ["rules"] : [],
            Rules: [],
            Behaviours: [],
            Layout: null);

        var header = new ProcessDescriptionHeader(
            Organization: "https://example.visualstudio.com",
            ProjectName: "TwelvefoldProject",
            ProcessId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ProcessName: "Twelvefold",
            DescriptorVersion: ProcessDescriptionHeader.CurrentDescriptorVersion,
            CapturedAtUtc: new DateTimeOffset(2026, 09, 16, 12, 34, 56, TimeSpan.Zero),
            RouteApiVersions:
            [
                new ProcessDescriptionRouteVersion("processes/{id}", "7.1"),
            ],
            KnownGaps: []);

        return new ProcessDescription(header, [alpha, beta]);
    }

    private static ProcessDescriptionCompactProjection.CompactRequest Request(
        string sections,
        string? fields = null)
    {
        var outcome = ProcessDescriptionCompactProjection.Parse(sections, fields);
        var parsed = outcome.ShouldBeOfType<
            ProcessDescriptionCompactProjection.CompactParseResult.Parsed>();
        return parsed.Request;
    }

    // ── Parser ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_UnknownSection_ReportsOffendingToken()
    {
        var outcome = ProcessDescriptionCompactProjection.Parse("fields,rulez", null);
        var unknown = outcome.ShouldBeOfType<
            ProcessDescriptionCompactProjection.CompactParseResult.UnknownSection>();
        unknown.Token.ShouldBe("rulez");
    }

    [Fact]
    public void Parse_EmptySections_IsNoSectionsSentinel()
    {
        var outcome = ProcessDescriptionCompactProjection.Parse(null, "System.Title");
        outcome.ShouldBeOfType<
            ProcessDescriptionCompactProjection.CompactParseResult.NoSections>();
    }

    [Fact]
    public void Parse_SectionsAreCaseInsensitive()
    {
        var request = Request("Fields, REQUIREMENTS");
        request.Sections.HasFlag(ProcessDescriptionCompactProjection.CompactSections.Fields).ShouldBeTrue();
        request.Sections.HasFlag(ProcessDescriptionCompactProjection.CompactSections.Requirements).ShouldBeTrue();
    }

    [Fact]
    public void Parse_FieldFilterKeepsOriginalCasingAndDedupesCaseInsensitively()
    {
        var request = Request("fields", "System.Title, system.title, Custom.Outcome");
        request.FieldFilter.Count.ShouldBe(2);
        request.FieldFilter.ShouldContain("System.Title");
        request.FieldFilter.ShouldContain("Custom.Outcome");
    }

    // ── Envelope invariants ─────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_EmitsSingleLineJsonWithContractIdentity()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields"));

        json.ShouldNotContain('\n');
        json.ShouldNotContain('\r');

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("contractVersion").GetString()
            .ShouldBe(ProcessDescriptionCompactProjection.CompactContractVersion);
        root.GetProperty("descriptorVersion").GetString()
            .ShouldBe(ProcessDescriptionHeader.CurrentDescriptorVersion);

        var connection = root.GetProperty("connection");
        connection.GetProperty("organization").GetString().ShouldBe("https://example.visualstudio.com");
        connection.GetProperty("project").GetString().ShouldBe("TwelvefoldProject");

        var process = root.GetProperty("process");
        process.GetProperty("id").GetString().ShouldBe("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        process.GetProperty("name").GetString().ShouldBe("Twelvefold");
    }

    [Fact]
    public void Serialize_RouteFullNamesTheActualNarrowedType()
    {
        // Simulate the CLI narrowing to a single type: the envelope route.full names it.
        var alpha = BuildFixture().Types[0];
        var header = BuildFixture().Header;
        var narrowed = new ProcessDescription(header, [alpha]);

        var json = ProcessDescriptionCompactProjection.Serialize(
            narrowed,
            Request("requirements"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("route").GetProperty("full").GetString()
            .ShouldBe($"twig process description {TypeAlpha} -o json");
    }

    [Fact]
    public void Serialize_CapturedAtCarriesTheHeaderTimestamp()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields"));

        using var doc = JsonDocument.Parse(json);
        var capturedAt = doc.RootElement.GetProperty("capturedAt").GetString();
        capturedAt.ShouldNotBeNull();
        DateTimeOffset.Parse(capturedAt!).ToUniversalTime()
            .ShouldBe(new DateTimeOffset(2026, 09, 16, 12, 34, 56, TimeSpan.Zero));
    }

    [Fact]
    public void Serialize_NeverAssertsTransitionAllowed()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields,requirements"));

        // Every "no transitionAllowed" fact this whole feature exists to preserve:
        // even with requirements requested, the envelope never claims a transition is
        // allowed, because it has no item revision and no evaluator.
        json.ShouldNotContain("transitionAllowed");
    }

    // ── Fields section ──────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_FieldsSection_ProjectsRequirednessKindVerbatim()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields", $"{FieldTitle},{FieldOutcome}"));

        using var doc = JsonDocument.Parse(json);
        var fields = FieldRowsFor(doc, TypeAlpha);

        fields[FieldTitle].GetProperty("required").GetString().ShouldBe("always");
        fields[FieldTitle].GetProperty("requiredWhen").ValueKind.ShouldBe(JsonValueKind.Null);

        fields[FieldOutcome].GetProperty("required").GetString().ShouldBe("conditional");
        fields[FieldOutcome].GetProperty("requiredWhen").GetString()!
            .ShouldContain("System.State");
    }

    [Fact]
    public void Serialize_FieldsSection_CarriesPicklistValuesAndUnknownConstraint()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields", $"{FieldPriority},{FieldSlug}"));

        using var doc = JsonDocument.Parse(json);
        var fields = FieldRowsFor(doc, TypeAlpha);

        fields[FieldPriority].GetProperty("valueConstraint").GetString().ShouldBe("list");
        var allowed = fields[FieldPriority].GetProperty("allowedValues");
        allowed.GetArrayLength().ShouldBe(3);
        allowed[0].GetString().ShouldBe("P1");
        allowed[2].GetString().ShouldBe("P3");

        fields[FieldSlug].GetProperty("valueConstraint").GetString().ShouldBe("unknown");
        fields[FieldSlug].GetProperty("allowedValues").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Serialize_FieldsSection_DefaultIsServerValueOrNull_NeverAssertsCallerSupplied()
    {
        // The fields-section 'default' is a mirror of the fields route only. A null here
        // NEVER licenses a caller-supplied claim: server rule actions (AB#803) or
        // item-level defaults can supply. That answer lives in the requirements section
        // where the rule set is in front of the reader.
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields", $"{FieldTitle},{FieldPriority}"));

        using var doc = JsonDocument.Parse(json);
        var fields = FieldRowsFor(doc, TypeAlpha);

        fields[FieldTitle].GetProperty("default").ValueKind.ShouldBe(JsonValueKind.Null);
        fields[FieldPriority].GetProperty("default").GetString().ShouldBe("P3");

        // Structural guard: the fields row has NO defaultSource / supplier property. That
        // claim only belongs on the requirements row, where the rules are visible.
        fields[FieldTitle].TryGetProperty("defaultSource", out _).ShouldBeFalse();
        fields[FieldTitle].TryGetProperty("supplier", out _).ShouldBeFalse();
    }

    [Fact]
    public void Serialize_FieldsSection_FilterMarksScopePartialAndCountsCandidates()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields", FieldTitle));

        using var doc = JsonDocument.Parse(json);
        var alpha = FindType(doc, TypeAlpha);
        var scope = alpha.GetProperty("fieldsScope");

        scope.GetProperty("coverage").GetString().ShouldBe("partial");
        scope.GetProperty("emitted").GetInt32().ShouldBe(1);
        // Alpha's field count in the fixture is six.
        scope.GetProperty("candidateTotal").GetInt32().ShouldBe(6);
    }

    // ── Requirements section ────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_RequirementsSection_SupplierUnknownWhenNoServerDefault()
    {
        // Custom.Outcome is Always-required in the fixture, has no server default, and
        // has no rule action targeting it (the rule action is makeRequired, which does not
        // supply a value). The correct answer is supplier=unknown — NOT caller.
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements", FieldOutcome));

        using var doc = JsonDocument.Parse(json);
        var row = RequirementRowFor(doc, TypeAlpha, FieldOutcome);
        row.GetProperty("supplier").GetString().ShouldBe("unknown");
        row.GetProperty("serverDefault").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Serialize_RequirementsSection_SupplierServerWhenServerDefaultKnown()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements", FieldPriority));

        using var doc = JsonDocument.Parse(json);
        var row = RequirementRowFor(doc, TypeAlpha, FieldPriority);
        row.GetProperty("supplier").GetString().ShouldBe("server");
        row.GetProperty("serverDefault").GetString().ShouldBe("P3");
    }

    [Fact]
    public void Serialize_RequirementsSection_CarriesRawSupplierActionsVerbatim()
    {
        // Custom.Reporter has no server default AND a rule action targeting it. The
        // supplier answer stays "unknown" (Twig does not evaluate rules here) but the
        // action rides verbatim so a consumer can evaluate it against a specific item.
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements", FieldReporter));

        using var doc = JsonDocument.Parse(json);
        var row = RequirementRowFor(doc, TypeAlpha, FieldReporter);

        row.GetProperty("supplier").GetString().ShouldBe("unknown");
        var actions = row.GetProperty("supplierActions");
        actions.GetArrayLength().ShouldBe(1);
        var action = actions[0];
        action.GetProperty("actionType").GetString().ShouldBe("setDefault");
        action.GetProperty("value").GetString().ShouldBe("unassigned");
        action.GetProperty("rule").GetString().ShouldBe("Supply reporter default");
    }

    [Fact]
    public void Serialize_RequirementsSection_ExcludesNeverRequiredFields()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements"));

        using var doc = JsonDocument.Parse(json);
        var alpha = FindType(doc, TypeAlpha);
        var rows = alpha.GetProperty("requirements");

        // Notes is FieldRequirednessKind.Never in the fixture; the requirements section
        // is the "what does the process demand" view, so a Never-row would be noise.
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            if (row.GetProperty("kind").GetString() != "field")
                continue;
            row.GetProperty("ref").GetString().ShouldNotBe(FieldNotes);
        }
    }

    [Fact]
    public void Serialize_RequirementsSection_CarriesStatesWithCategory()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements"));

        using var doc = JsonDocument.Parse(json);
        var alpha = FindType(doc, TypeAlpha);
        var states = alpha.GetProperty("states");
        states.GetArrayLength().ShouldBe(2);
        states[0].GetProperty("name").GetString().ShouldBe("Active");
        states[0].GetProperty("category").GetString().ShouldBe("InProgress");
        states[1].GetProperty("name").GetString().ShouldBe("Done");
        states[1].GetProperty("category").GetString().ShouldBe("Completed");
    }

    [Fact]
    public void Serialize_RequirementsSection_RulesTravelVerbatimEvenWhenFieldFilterDropsTheirTarget()
    {
        // The filter drops every field row from Alpha's requirements. Rules still ride:
        // a caller looking at requirements needs the whole rule set to reason about which
        // conditions gate which fields.
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements", "Custom.NotThere"));

        using var doc = JsonDocument.Parse(json);
        var alpha = FindType(doc, TypeAlpha);
        var rows = alpha.GetProperty("requirements");

        var ruleCount = 0;
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            if (rows[i].GetProperty("kind").GetString() == "rule")
                ruleCount++;
        }
        ruleCount.ShouldBe(2);
    }

    [Fact]
    public void Serialize_Rules_AreNeverEvaluatedAndCarryConditionsAndActionsVerbatim()
    {
        // The load-bearing "we do not evaluate" contract:
        //   - each rule row is tagged evaluated: false
        //   - conditions/actions ride verbatim as objects (no joined-string collapse)
        //   - conditionType/actionType are whatever the server said (no known-verb list)
        // No supported/unsupported classification of the verb — Twig does not know what
        // the proposal gate can evaluate from THIS surface, and inventing a list would
        // create the same defect Main flagged in the initial pass.
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements"));

        using var doc = JsonDocument.Parse(json);
        var alpha = FindType(doc, TypeAlpha);
        var rules = RuleRowsFor(alpha);

        rules.Count.ShouldBe(2);
        foreach (var rule in rules.Values)
        {
            rule.GetProperty("evaluated").GetBoolean().ShouldBeFalse();
        }

        var gate = rules["Require outcome when done"];
        var condition = gate.GetProperty("conditions")[0];
        condition.GetProperty("conditionType").GetString().ShouldBe("whenStateIs");
        condition.GetProperty("field").GetString().ShouldBe("System.State");
        condition.GetProperty("value").GetString().ShouldBe("Done");

        var action = gate.GetProperty("actions")[0];
        action.GetProperty("actionType").GetString().ShouldBe("makeRequired");
        action.GetProperty("targetField").GetString().ShouldBe(FieldOutcome);
        action.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Warnings & completeness ─────────────────────────────────────────────────────

    [Fact]
    public void Serialize_EmitsConditionsNotEvaluatedPerTypeWithRules()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("requirements"));

        using var doc = JsonDocument.Parse(json);
        var warnings = doc.RootElement.GetProperty("warnings");

        var found = false;
        for (var i = 0; i < warnings.GetArrayLength(); i++)
        {
            var w = warnings[i];
            if (w.GetProperty("code").GetString() != "conditionsNotEvaluated") continue;
            if (w.GetProperty("subject").GetString() != TypeAlpha) continue;
            w.GetProperty("detail").GetString()!
                .ShouldContain("NOT evaluated");
            found = true;
        }

        found.ShouldBeTrue(
            "every type carrying rules must be flagged conditionsNotEvaluated — Twig does "
            + "not evaluate rule conditions on this route, and staying quiet about it would "
            + "let a caller misread rules as applying.");
    }

    [Fact]
    public void Serialize_EmitsUnresolvedValueConstraintWhenPicklistUnreadable()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields", FieldSlug));

        using var doc = JsonDocument.Parse(json);
        var warnings = doc.RootElement.GetProperty("warnings");

        var seenSlug = false;
        for (var i = 0; i < warnings.GetArrayLength(); i++)
        {
            var w = warnings[i];
            if (w.GetProperty("code").GetString() == "unresolvedValueConstraint"
                && w.GetProperty("subject").GetString() == $"{TypeAlpha}#{FieldSlug}")
                seenSlug = true;
        }
        seenSlug.ShouldBeTrue();
    }

    [Fact]
    public void Serialize_EmitsUnknownFieldRefWhenFilterMatchesNoType()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(),
            Request("fields", "Custom.Absent"));

        using var doc = JsonDocument.Parse(json);
        var warnings = doc.RootElement.GetProperty("warnings");

        var seen = false;
        for (var i = 0; i < warnings.GetArrayLength(); i++)
        {
            var w = warnings[i];
            if (w.GetProperty("code").GetString() == "unknownFieldRef"
                && w.GetProperty("subject").GetString() == "Custom.Absent")
                seen = true;
        }
        seen.ShouldBeTrue(
            "a filter typo that matched nothing must not be silent — the whole compact "
            + "response's compactness is what invites the failure.");
    }

    [Fact]
    public void Serialize_EmitsUnfetchedWarningAndFullyReadFlagFalse()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(includeBetaUnfetched: true),
            Request("fields,requirements"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("completeness").GetProperty("fullyRead").GetBoolean().ShouldBeFalse();

        var warnings = root.GetProperty("warnings");
        var seen = false;
        for (var i = 0; i < warnings.GetArrayLength(); i++)
        {
            var w = warnings[i];
            if (w.GetProperty("code").GetString() == "unfetched"
                && w.GetProperty("subject").GetString() == $"{TypeBeta}#rules")
                seen = true;
        }
        seen.ShouldBeTrue();
    }

    [Fact]
    public void Serialize_FullyReadTrue_WhenNoTypeHasUnfetched()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(includeBetaUnfetched: false),
            Request("fields"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("completeness").GetProperty("fullyRead").GetBoolean()
            .ShouldBeTrue();
    }

    [Fact]
    public void Serialize_SectionCoveragePartial_WhenAnyFieldFilterDropsSomething()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(includeBetaUnfetched: false),
            Request("fields", FieldTitle));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("completeness").GetProperty("sectionCoverage")
            .GetString().ShouldBe("partial");
    }

    [Fact]
    public void Serialize_SectionCoverageComplete_WhenNoFilterAndNoUnfetched()
    {
        var json = ProcessDescriptionCompactProjection.Serialize(
            BuildFixture(includeBetaUnfetched: false),
            Request("fields,requirements"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("completeness").GetProperty("sectionCoverage")
            .GetString().ShouldBe("complete");
    }


    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static JsonElement FindType(JsonDocument doc, string referenceName)
    {
        var types = doc.RootElement.GetProperty("types");
        for (var i = 0; i < types.GetArrayLength(); i++)
        {
            if (types[i].GetProperty("referenceName").GetString() == referenceName)
                return types[i];
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected type '{referenceName}' in the compact envelope; not found.");
    }

    private static IReadOnlyDictionary<string, JsonElement> FieldRowsFor(
        JsonDocument doc,
        string typeReferenceName)
    {
        var type = FindType(doc, typeReferenceName);
        var rows = type.GetProperty("fields");
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            map[row.GetProperty("ref").GetString()!] = row;
        }
        return map;
    }

    private static JsonElement RequirementRowFor(
        JsonDocument doc,
        string typeReferenceName,
        string fieldReferenceName)
    {
        var type = FindType(doc, typeReferenceName);
        var rows = type.GetProperty("requirements");
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            if (row.GetProperty("kind").GetString() != "field") continue;
            if (row.GetProperty("ref").GetString() == fieldReferenceName)
                return row;
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected requirement row for '{fieldReferenceName}' under '{typeReferenceName}'.");
    }

    private static IReadOnlyDictionary<string, JsonElement> RuleRowsFor(JsonElement type)
    {
        var rows = type.GetProperty("requirements");
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            if (row.GetProperty("kind").GetString() != "rule") continue;
            map[row.GetProperty("name").GetString()!] = row;
        }
        return map;
    }
}
