namespace Twig.Domain.Services.Plan;

/// <summary>
/// One structured problem the parser found. Codes are stable so callers can key on them;
/// paths are JSON-pointer style (e.g. <c>/operations/2/relation</c>) and messages are
/// human-readable but not the identity — never key on the message.
/// </summary>
public sealed record PlanValidationIssue
{
    /// <summary>Stable machine-readable code, e.g. <c>plan.unknown_property</c>.</summary>
    public required string Code { get; init; }

    /// <summary>JSON-pointer path to the offending node; <c>""</c> for whole-document issues.</summary>
    public required string Path { get; init; }

    /// <summary>Human-readable description. Never keyed on.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Well-known validation issue codes emitted by <see cref="Twig.Infrastructure.Plan.PlanDocumentParser"/>.
/// </summary>
public static class PlanValidationCodes
{
    /// <summary>Document bytes were not valid JSON.</summary>
    public const string JsonInvalid = "plan.json_invalid";

    /// <summary>Root value was not a JSON object.</summary>
    public const string NotAnObject = "plan.not_an_object";

    /// <summary>A property required by the schema was missing.</summary>
    public const string MissingProperty = "plan.missing_property";

    /// <summary>A property present in the JSON is not part of the schema.</summary>
    public const string UnknownProperty = "plan.unknown_property";

    /// <summary>A JSON value was of the wrong type (e.g. string where int expected).</summary>
    public const string WrongType = "plan.wrong_type";

    /// <summary>Plan version was not <c>1</c>.</summary>
    public const string UnsupportedVersion = "plan.unsupported_version";

    /// <summary>Operation kind is not one of the five supported values.</summary>
    public const string UnknownKind = "plan.unknown_kind";

    /// <summary>Two operations share an id.</summary>
    public const string DuplicateOperationId = "plan.duplicate_operation_id";

    /// <summary>Operations array was empty.</summary>
    public const string EmptyOperations = "plan.empty_operations";

    /// <summary>A batch declared no fields, or a fields map was otherwise empty.</summary>
    public const string EmptyFields = "plan.empty_fields";

    /// <summary>A relation string was not one of parent|predecessor|successor|related.</summary>
    public const string InvalidRelation = "plan.invalid_relation";

    /// <summary>A staged identity string was not a valid GUID.</summary>
    public const string InvalidStagedIdentity = "plan.invalid_staged_identity";

    /// <summary>A string was empty or whitespace when a non-empty value was required.</summary>
    public const string EmptyString = "plan.empty_string";

    /// <summary>The same property name appeared more than once in a single JSON object.</summary>
    public const string DuplicateProperty = "plan.duplicate_property";

    /// <summary>Two publish-seed operations targeted the same stagedIdentity.</summary>
    public const string DuplicateStagedIdentityTarget = "plan.duplicate_staged_identity_target";

    /// <summary>A numeric value was outside the allowed range (e.g. workItemId must be &gt; 0).</summary>
    public const string IntegerOutOfRange = "plan.integer_out_of_range";

    /// <summary>
    /// AB#832: the plan file's path already carries a journal under a different digest, so this
    /// file replaced a document that was previously previewed at the same path. Plan files are
    /// single-use; consuming a replaced one would bind a transaction to bytes that are not the
    /// ones its journal describes.
    /// </summary>
    public const string SourceReplaced = "plan.source_replaced";
}
