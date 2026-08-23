using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Parses and validates a plan v1 JSON document into a <see cref="PlanDefinition"/>.
/// <para>
/// User-facing input is never allowed to throw: every structural, type or vocabulary
/// error surfaces as a <see cref="PlanValidationIssue"/> with a stable code and JSON
/// pointer path. Only programmer errors on already-validated data throw.
/// </para>
/// <para>
/// The parser is strict by intent — unknown top-level properties, unknown operation
/// properties and unknown operation kinds are all rejected. Silent tolerance would let
/// notes, artifacts, branches and reparent slip into a "declarative" plan file through
/// the seam that was drawn precisely to keep them out.
/// </para>
/// <para>
/// AOT-safe: only <see cref="JsonDocument"/> is used, never
/// <see cref="JsonSerializer"/>.Deserialize with unbound generics.
/// </para>
/// </summary>
public sealed class PlanDocumentParser
{
    private const int SupportedVersion = 1;

    // JSON property names — camelCase per the shared contract sample.
    private const string PropVersion = "version";
    private const string PropWorkspace = "workspace";
    private const string PropOperations = "operations";
    private const string PropOrganization = "organization";
    private const string PropProject = "project";
    private const string PropId = "id";
    private const string PropKind = "kind";
    private const string PropWorkItemId = "workItemId";
    private const string PropExpectedRevision = "expectedRevision";
    private const string PropFields = "fields";
    private const string PropRelation = "relation";
    private const string PropOtherId = "otherId";
    private const string PropStagedIdentity = "stagedIdentity";
    private const string PropExpectedFingerprint = "expectedFingerprint";

    private const string KindBatch = "batch";
    private const string KindAddLink = "add-link";
    private const string KindRemoveLink = "remove-link";
    private const string KindPublishSeed = "publish-seed";
    private const string KindDelete = "delete";

    private static readonly FrozenSet<string> RootProperties = new[]
    {
        PropVersion, PropWorkspace, PropOperations,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> WorkspaceProperties = new[]
    {
        PropOrganization, PropProject,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> BatchProperties = new[]
    {
        PropId, PropKind, PropWorkItemId, PropExpectedRevision, PropFields,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> LinkProperties = new[]
    {
        PropId, PropKind, PropWorkItemId, PropExpectedRevision, PropRelation, PropOtherId,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> PublishSeedProperties = new[]
    {
        PropId, PropKind, PropStagedIdentity, PropExpectedFingerprint,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> DeleteProperties = new[]
    {
        PropId, PropKind, PropWorkItemId, PropExpectedRevision,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ValidRelations = new[]
    {
        "parent", "predecessor", "successor", "related",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Parses <paramref name="json"/> as a plan v1 document.</summary>
    public PlanValidationResult Parse(string json) => Parse(json.AsSpan());

    /// <summary>Parses <paramref name="json"/> as a plan v1 document.</summary>
    public PlanValidationResult Parse(ReadOnlySpan<char> json)
    {
        var issues = new List<PlanValidationIssue>();
        var utf8 = Encoding.UTF8.GetBytes(json.ToString());
        JsonDocument? document = null;
        try
        {
            try
            {
                document = JsonDocument.Parse(utf8);
            }
            catch (JsonException ex)
            {
                issues.Add(new PlanValidationIssue
                {
                    Code = PlanValidationCodes.JsonInvalid,
                    Path = "",
                    Message = ex.Message,
                });
                return new PlanValidationResult { Issues = issues };
            }

            // Duplicate property names anywhere in the document must be caught before
            // any semantic read or digest — JsonDocument silently keeps the last value,
            // which would make two files with different intent collapse to the same digest.
            CheckDuplicateProperties(utf8, issues);
            if (issues.Count > 0)
            {
                return new PlanValidationResult { Issues = issues };
            }

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new PlanValidationIssue
                {
                    Code = PlanValidationCodes.NotAnObject,
                    Path = "",
                    Message = "Plan root must be a JSON object.",
                });
                return new PlanValidationResult { Issues = issues };
            }

            RejectUnknown(root, RootProperties, "", issues);

            var version = ReadRequiredInt(root, PropVersion, "/" + PropVersion, issues);
            if (version is not null && version.Value != SupportedVersion)
            {
                issues.Add(new PlanValidationIssue
                {
                    Code = PlanValidationCodes.UnsupportedVersion,
                    Path = "/" + PropVersion,
                    Message = $"Plan version {version.Value} is not supported. Only version {SupportedVersion} is understood.",
                });
            }

            var workspace = ReadWorkspace(root, issues);
            var operations = ReadOperations(root, issues);

            if (issues.Count > 0 || version is null || workspace is null || operations is null)
            {
                return new PlanValidationResult { Issues = issues };
            }

            var plan = new PlanDefinition
            {
                Version = version.Value,
                Workspace = workspace,
                Operations = operations,
            };
            var (canonicalJson, digest) = PlanCanonicalizer.Canonicalize(root);
            return new PlanValidationResult
            {
                Issues = issues,
                Plan = plan,
                CanonicalJson = canonicalJson,
                Digest = digest,
            };
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static PlanWorkspace? ReadWorkspace(JsonElement root, List<PlanValidationIssue> issues)
    {
        if (!root.TryGetProperty(PropWorkspace, out var element))
        {
            issues.Add(Missing("/" + PropWorkspace, PropWorkspace));
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(WrongType("/" + PropWorkspace, "object", element.ValueKind));
            return null;
        }

        RejectUnknown(element, WorkspaceProperties, "/" + PropWorkspace, issues);

        var org = ReadRequiredNonEmptyString(element, PropOrganization, $"/{PropWorkspace}/{PropOrganization}", issues);
        var proj = ReadRequiredNonEmptyString(element, PropProject, $"/{PropWorkspace}/{PropProject}", issues);
        if (org is null || proj is null) return null;
        return new PlanWorkspace { Organization = org, Project = proj };
    }

    private static IReadOnlyList<PlanOperationDefinition>? ReadOperations(JsonElement root, List<PlanValidationIssue> issues)
    {
        if (!root.TryGetProperty(PropOperations, out var element))
        {
            issues.Add(Missing("/" + PropOperations, PropOperations));
            return null;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            issues.Add(WrongType("/" + PropOperations, "array", element.ValueKind));
            return null;
        }
        if (element.GetArrayLength() == 0)
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.EmptyOperations,
                Path = "/" + PropOperations,
                Message = "Plan must declare at least one operation.",
            });
            return null;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPublishTargets = new HashSet<Guid>();
        var results = new List<PlanOperationDefinition>(element.GetArrayLength());
        var index = 0;
        var anyFailed = false;
        foreach (var op in element.EnumerateArray())
        {
            var path = $"/{PropOperations}/{index}";
            var parsed = ReadOperation(op, path, issues, seenIds, seenPublishTargets);
            if (parsed is null)
            {
                anyFailed = true;
            }
            else
            {
                results.Add(parsed);
            }
            index++;
        }
        return anyFailed ? null : results;
    }

    private static PlanOperationDefinition? ReadOperation(
        JsonElement op,
        string path,
        List<PlanValidationIssue> issues,
        HashSet<string> seenIds,
        HashSet<Guid> seenPublishTargets)
    {
        if (op.ValueKind != JsonValueKind.Object)
        {
            issues.Add(WrongType(path, "object", op.ValueKind));
            return null;
        }

        var id = ReadRequiredNonEmptyString(op, PropId, $"{path}/{PropId}", issues);
        var kind = ReadRequiredNonEmptyString(op, PropKind, $"{path}/{PropKind}", issues);

        if (id is not null && !seenIds.Add(id))
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.DuplicateOperationId,
                Path = $"{path}/{PropId}",
                Message = $"Operation id '{id}' is used more than once.",
            });
            return null;
        }

        if (id is null || kind is null) return null;

        return kind switch
        {
            KindBatch => ReadBatch(op, path, id, issues),
            KindAddLink => ReadLink(op, path, id, PlanOperationKind.AddLink, issues),
            KindRemoveLink => ReadLink(op, path, id, PlanOperationKind.RemoveLink, issues),
            KindPublishSeed => ReadPublishSeed(op, path, id, issues, seenPublishTargets),
            KindDelete => ReadDelete(op, path, id, issues),
            _ => AddUnknownKind(op, path, kind, issues),
        };
    }

    private static PlanOperationDefinition? AddUnknownKind(
        JsonElement op, string path, string kind, List<PlanValidationIssue> issues)
    {
        _ = op;
        issues.Add(new PlanValidationIssue
        {
            Code = PlanValidationCodes.UnknownKind,
            Path = $"{path}/{PropKind}",
            Message = $"Unknown operation kind '{kind}'. Expected batch | add-link | remove-link | publish-seed | delete.",
        });
        return null;
    }

    private static PlanOperationDefinition? ReadBatch(
        JsonElement op, string path, string id, List<PlanValidationIssue> issues)
    {
        RejectUnknown(op, BatchProperties, path, issues);
        var workItemId = ReadRequiredPositiveInt(op, PropWorkItemId, $"{path}/{PropWorkItemId}", issues);
        var expectedRevision = ReadRequiredPositiveInt(op, PropExpectedRevision, $"{path}/{PropExpectedRevision}", issues);
        var fields = ReadFields(op, path, issues);
        if (workItemId is null || expectedRevision is null || fields is null) return null;
        return new BatchOperation
        {
            Id = id,
            WorkItemId = workItemId.Value,
            ExpectedRevision = expectedRevision.Value,
            Fields = fields,
        };
    }

    private static IReadOnlyDictionary<string, string?>? ReadFields(
        JsonElement op, string path, List<PlanValidationIssue> issues)
    {
        if (!op.TryGetProperty(PropFields, out var element))
        {
            issues.Add(Missing($"{path}/{PropFields}", PropFields));
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(WrongType($"{path}/{PropFields}", "object", element.ValueKind));
            return null;
        }

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        var anyFailed = false;
        foreach (var field in element.EnumerateObject())
        {
            var fieldPath = $"{path}/{PropFields}/{field.Name}";
            switch (field.Value.ValueKind)
            {
                case JsonValueKind.String:
                    map[field.Name] = field.Value.GetString();
                    break;
                case JsonValueKind.Null:
                    map[field.Name] = null;
                    break;
                default:
                    issues.Add(WrongType(fieldPath, "string or null", field.Value.ValueKind));
                    anyFailed = true;
                    break;
            }
        }
        if (anyFailed) return null;
        if (map.Count == 0)
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.EmptyFields,
                Path = $"{path}/{PropFields}",
                Message = "Batch fields map must be non-empty.",
            });
            return null;
        }
        return map;
    }

    private static PlanOperationDefinition? ReadLink(
        JsonElement op, string path, string id, PlanOperationKind kind, List<PlanValidationIssue> issues)
    {
        RejectUnknown(op, LinkProperties, path, issues);
        var workItemId = ReadRequiredPositiveInt(op, PropWorkItemId, $"{path}/{PropWorkItemId}", issues);
        var expectedRevision = ReadRequiredPositiveInt(op, PropExpectedRevision, $"{path}/{PropExpectedRevision}", issues);
        var relation = ReadRequiredNonEmptyString(op, PropRelation, $"{path}/{PropRelation}", issues);
        var otherId = ReadRequiredPositiveInt(op, PropOtherId, $"{path}/{PropOtherId}", issues);

        if (relation is not null && !ValidRelations.Contains(relation))
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.InvalidRelation,
                Path = $"{path}/{PropRelation}",
                Message = $"Relation '{relation}' is not one of parent | predecessor | successor | related.",
            });
            return null;
        }

        if (workItemId is null || expectedRevision is null || relation is null || otherId is null) return null;

        return kind == PlanOperationKind.AddLink
            ? new AddLinkOperation
            {
                Id = id,
                WorkItemId = workItemId.Value,
                ExpectedRevision = expectedRevision.Value,
                Relation = relation,
                OtherId = otherId.Value,
            }
            : new RemoveLinkOperation
            {
                Id = id,
                WorkItemId = workItemId.Value,
                ExpectedRevision = expectedRevision.Value,
                Relation = relation,
                OtherId = otherId.Value,
            };
    }

    private static PlanOperationDefinition? ReadPublishSeed(
        JsonElement op, string path, string id, List<PlanValidationIssue> issues,
        HashSet<Guid> seenPublishTargets)
    {
        RejectUnknown(op, PublishSeedProperties, path, issues);
        var identityText = ReadRequiredNonEmptyString(op, PropStagedIdentity, $"{path}/{PropStagedIdentity}", issues);
        var fingerprint = ReadRequiredNonEmptyString(op, PropExpectedFingerprint, $"{path}/{PropExpectedFingerprint}", issues);
        if (identityText is null || fingerprint is null) return null;

        if (!StagedIdentity.TryParse(identityText, out var identity))
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.InvalidStagedIdentity,
                Path = $"{path}/{PropStagedIdentity}",
                Message = $"Staged identity '{identityText}' is not a valid GUID.",
            });
            return null;
        }

        if (!seenPublishTargets.Add(identity.Value))
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.DuplicateStagedIdentityTarget,
                Path = $"{path}/{PropStagedIdentity}",
                Message = $"Staged identity '{identityText}' is targeted by more than one publish-seed operation.",
            });
            return null;
        }

        return new PublishSeedOperation
        {
            Id = id,
            StagedIdentity = identity,
            ExpectedFingerprint = fingerprint,
        };
    }

    private static PlanOperationDefinition? ReadDelete(
        JsonElement op, string path, string id, List<PlanValidationIssue> issues)
    {
        RejectUnknown(op, DeleteProperties, path, issues);
        var workItemId = ReadRequiredPositiveInt(op, PropWorkItemId, $"{path}/{PropWorkItemId}", issues);
        var expectedRevision = ReadRequiredPositiveInt(op, PropExpectedRevision, $"{path}/{PropExpectedRevision}", issues);
        if (workItemId is null || expectedRevision is null) return null;
        return new DeleteOperation
        {
            Id = id,
            WorkItemId = workItemId.Value,
            ExpectedRevision = expectedRevision.Value,
        };
    }

    private static int? ReadRequiredInt(JsonElement obj, string prop, string path, List<PlanValidationIssue> issues)
    {
        if (!obj.TryGetProperty(prop, out var element))
        {
            issues.Add(Missing(path, prop));
            return null;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            issues.Add(WrongType(path, "integer", element.ValueKind));
            return null;
        }
        return value;
    }

    private static int? ReadRequiredPositiveInt(
        JsonElement obj, string prop, string path, List<PlanValidationIssue> issues)
    {
        if (!obj.TryGetProperty(prop, out var element))
        {
            issues.Add(Missing(path, prop));
            return null;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            issues.Add(WrongType(path, "integer", element.ValueKind));
            return null;
        }
        if (value <= 0)
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.IntegerOutOfRange,
                Path = path,
                Message = $"Property '{prop}' must be a positive integer; got {value}.",
            });
            return null;
        }
        return value;
    }

    private static string? ReadRequiredNonEmptyString(JsonElement obj, string prop, string path, List<PlanValidationIssue> issues)
    {
        if (!obj.TryGetProperty(prop, out var element))
        {
            issues.Add(Missing(path, prop));
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(WrongType(path, "string", element.ValueKind));
            return null;
        }
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new PlanValidationIssue
            {
                Code = PlanValidationCodes.EmptyString,
                Path = path,
                Message = $"Property '{prop}' must be a non-empty string.",
            });
            return null;
        }
        return value;
    }

    private static void RejectUnknown(
        JsonElement element,
        FrozenSet<string> allowed,
        string parentPath,
        List<PlanValidationIssue> issues)
    {
        foreach (var p in element.EnumerateObject())
        {
            if (!allowed.Contains(p.Name))
            {
                issues.Add(new PlanValidationIssue
                {
                    Code = PlanValidationCodes.UnknownProperty,
                    Path = parentPath + "/" + p.Name,
                    Message = $"Property '{p.Name}' is not part of the plan schema.",
                });
            }
        }
    }

    private static PlanValidationIssue Missing(string path, string prop) => new()
    {
        Code = PlanValidationCodes.MissingProperty,
        Path = path,
        Message = $"Required property '{prop}' is missing.",
    };

    private static PlanValidationIssue WrongType(string path, string expected, JsonValueKind actual) => new()
    {
        Code = PlanValidationCodes.WrongType,
        Path = path,
        Message = $"Expected {expected}; got {actual}.",
    };

    /// <summary>
    /// Streams the raw UTF-8 bytes with <see cref="Utf8JsonReader"/> and reports every
    /// case where the same property name appears more than once in a single object —
    /// at any depth, including inside <c>fields</c>. <see cref="JsonDocument"/> silently
    /// keeps the last value on collisions, which would let two distinct authorings
    /// canonicalize to the same digest and slip through unnoticed.
    /// </summary>
    private static void CheckDuplicateProperties(byte[] utf8, List<PlanValidationIssue> issues)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var scopes = new Stack<ScopeInfo>();
        var pathSegments = new List<string>();
        string? pendingProperty = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    PushValueSegment(scopes, pathSegments, ref pendingProperty);
                    scopes.Push(new ScopeInfo(reader.TokenType == JsonTokenType.StartArray));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    if (pathSegments.Count > 0)
                    {
                        pathSegments.RemoveAt(pathSegments.Count - 1);
                    }
                    // The container itself was already advanced when it was opened; nothing more to do.
                    break;

                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    var scope = scopes.Peek();
                    if (!scope.Names!.Add(name))
                    {
                        var basePath = pathSegments.Count == 0 ? "" : "/" + string.Join("/", pathSegments);
                        issues.Add(new PlanValidationIssue
                        {
                            Code = PlanValidationCodes.DuplicateProperty,
                            Path = basePath + "/" + name,
                            Message = $"Property '{name}' appears more than once in the same object.",
                        });
                    }
                    pendingProperty = name;
                    break;

                default:
                    // Primitive value (string, number, true, false, null).
                    if (scopes.Count > 0 && scopes.Peek().IsArray)
                    {
                        scopes.Peek().AdvanceIndex();
                    }
                    pendingProperty = null;
                    break;
            }
        }
    }

    private static void PushValueSegment(
        Stack<ScopeInfo> scopes, List<string> pathSegments, ref string? pendingProperty)
    {
        if (scopes.Count == 0)
        {
            return;
        }
        var parent = scopes.Peek();
        if (parent.IsArray)
        {
            pathSegments.Add(parent.NextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parent.AdvanceIndex();
        }
        else
        {
            pathSegments.Add(pendingProperty ?? "");
            pendingProperty = null;
        }
    }

    private sealed class ScopeInfo
    {
        public ScopeInfo(bool isArray)
        {
            IsArray = isArray;
            Names = isArray ? null : new HashSet<string>(StringComparer.Ordinal);
        }

        public bool IsArray { get; }
        public HashSet<string>? Names { get; }
        public int NextIndex { get; private set; }
        public void AdvanceIndex() => NextIndex++;
    }
}
