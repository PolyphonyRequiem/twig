using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<FieldValues>> ResolveCreateEvidenceAsync(ProcessSchema schema, Op op, CancellationToken ct)
        => Guard(() => WithSession(schema.Connection, (bound, token) => ResolveCreateFields(bound, schema, op, token), ct));

    private async Task<FieldValues> ResolveCreateFields(Bound bound, ProcessSchema schema, Op op, CancellationToken ct)
    {
        if (op.Kind != OpKind.Create || schema.Connection != bound.Connection)
            throw Wire.Refuse(ErrorCode.Conflict, "Create field evidence requires a creation effect in this exact native scope.");
        var type = schema.Types.SingleOrDefault(type => type.Reference == op.Type)
            ?? throw Wire.Refuse(ErrorCode.Unavailable, "The exact native create type was not discovered.");
        var evidence = FieldValues.Empty;
        const string workItemType = "System.WorkItemType";
        if (Declares(workItemType))
        {
            // Discovery fingerprints this native display name; the typed reference is not the endpoint value.
            var names = NativeNames(type).ToArray();
            if (names is not [{ Length: > 0 } name] || schema.Types.SelectMany(NativeNames)
                .Count(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) != 1)
                throw Wire.Refuse(ErrorCode.Unavailable, "The exact native create endpoint name was not discovered.");
            Add(workItemType, FieldValue.String(name));
        }
        await ResolvePath("System.AreaPath", "System.AreaId", "areas", "Area");
        await ResolvePath("System.IterationPath", "System.IterationId", "iterations", "Iteration");
        foreach (var name in op.Fields.Keys)
            if (IsCreateAssignedField(name) && !evidence.ContainsKey(name))
                throw Wire.Refuse(ErrorCode.Conflict, $"Authored field '{name}' lacks exact native create evidence.");
        return evidence;

        bool Declares(string field) => type.Fields.Any(definition => definition.Reference == field);
        static IEnumerable<string?> NativeNames(ItemType itemType) => itemType.Rules
            .Where(rule => rule.Disabled && rule.Action == RuleAction.Unknown
                && rule.Values is [{ Kind: FieldKind.Raw, DataType: "ado.native-type-name" }])
            .Select(rule => rule.Values[0].Text);

        void Add(string field, FieldValue value)
        {
            if (!type.Fields.Any(definition => definition.Reference == field && definition.Kind == value.Kind))
                throw Wire.Refuse(ErrorCode.Unavailable, $"Discovered field '{field}' has an incompatible native type.");
            if (op.Fields.TryGetValue(field, out var authored) && !value.Equals(authored))
                throw Wire.Refuse(ErrorCode.Conflict, $"Authored field '{field}' conflicts with provider create evidence.");
            evidence = evidence.Set(field, value);
        }

        async Task ResolvePath(string path, string id, string route, string structure)
        {
            if (!op.Fields.TryGetValue(path, out var selected))
            {
                if (Declares(id))
                    throw Wire.Refuse(ErrorCode.Unavailable, $"Select an exact '{path}' to establish '{id}'; no native path was assumed.");
                return;
            }
            if (!Declares(path) || selected is not { Kind: FieldKind.Text, Text: { Length: > 0 } selectedPath })
                throw Wire.Refuse(ErrorCode.InvalidInput, $"'{path}' must be an exact discovered classification path.");
            var parts = selectedPath.Split('\\');
            if (parts[0] != bound.ProjectName)
                throw Wire.Refuse(ErrorCode.Conflict, $"'{path}' must include the exact native project name.");
            if (parts.Skip(1).Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                || part.Contains('/') || part.Any(char.IsControl)))
                throw Wire.Refuse(ErrorCode.InvalidInput, $"'{path}' has an invalid native path segment.");
            var relative = parts.Length == 1 ? "" : "/" + string.Join("/", parts.Skip(1).Select(Wire.Escape));
            var node = await Get(bound,
                $"{bound.Project}/_apis/wit/classificationnodes/{route}{relative}?$depth=0&api-version={Api}", ct);
            if (!Wire.Text(node, "structureType").Equals(structure, StringComparison.OrdinalIgnoreCase)
                || ClassificationPath(bound, node, structure) != selectedPath)
                throw Wire.Refuse(ErrorCode.Conflict, $"'{path}' did not match the returned native classification node.");
            if (Declares(id)) Add(id, FieldValue.Integer(Wire.Positive(node, "id")));
        }
    }

    private static bool IsCreateAssignedField(string name) =>
        name.Equals("System.WorkItemType", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System.AreaId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System.IterationId", StringComparison.OrdinalIgnoreCase);

    private async Task CheckCreateEvidence(Bound bound, ProcessSchema schema, Op op, CancellationToken ct)
    {
        if (op.Kind == OpKind.Create && !(await ResolveCreateFields(bound, schema, op, ct)).Equals(op.CreateEvidence))
            throw Wire.Refuse(ErrorCode.Conflict, "Native create field evidence is missing or changed; preview a new proposal.");
    }
}
