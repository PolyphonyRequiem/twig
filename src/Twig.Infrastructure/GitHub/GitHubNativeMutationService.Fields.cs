using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

internal sealed partial class GitHubNativeMutationService
{
    private static readonly IReadOnlyDictionary<string, string> ScalarFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["System.Title"] = "title",
        ["System.Description"] = "body",
        ["System.State"] = "state",
        ["github.milestoneId"] = "milestoneId",
    };

    private static readonly IReadOnlyDictionary<string, (string Mutation, string Input, string Member, string Result, string Snapshot)> CollectionFields =
        new Dictionary<string, (string, string, string, string, string)>(StringComparer.Ordinal)
        {
            ["github.labels.add"] = ("addLabelsToLabelable", "AddLabelsToLabelableInput", "labelIds", "labelable", "github.labelIds"),
            ["github.labels.remove"] = ("removeLabelsFromLabelable", "RemoveLabelsFromLabelableInput", "labelIds", "labelable", "github.labelIds"),
            ["github.assignees.add"] = ("addAssigneesToAssignable", "AddAssigneesToAssignableInput", "assigneeIds", "assignable", "github.assigneeIds"),
            ["github.assignees.remove"] = ("removeAssigneesFromAssignable", "RemoveAssigneesFromAssignableInput", "assigneeIds", "assignable", "github.assigneeIds"),
        };

    private const string ResourceQuery = """
        query($id: ID!) { node(id: $id) { __typename id ... on Label { repository { id owner { id } } }
          ... on Milestone { repository { id owner { id } } } } }
        """;

    private static bool NativeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim()
        && value.Length <= 256 && !value.Any(char.IsControl);

    private static bool HasNativeObservation(ProviderWorkItemSnapshot snapshot) =>
        snapshot.Fields.TryGetValue("github.updatedAt", out var value)
        && value.Value is TextValue text
        && DateTimeOffset.TryParse(text.Value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out _);

    private static bool ValidBatch(ProviderMutationOperation operation)
    {
        if (operation is not { OperationKind: PlanOperationKind.Batch, Concurrency: "observed-non-atomic",
                Other: null, Relation: null, Fields.Count: > 0 }
            || operation.Target.Item.ExternalIdentity.ProviderKind != "github")
            return false;
        var fields = operation.Fields;
        if (fields.Count == 1 && CollectionFields.ContainsKey(fields.First().Key))
            return NativeId(fields.First().Value);
        return fields.All(pair => ScalarFields.ContainsKey(pair.Key) && pair.Key switch
        {
            "System.Title" => !string.IsNullOrWhiteSpace(pair.Value),
            "System.State" => pair.Value is "OPEN" or "CLOSED",
            "github.milestoneId" => pair.Value is null || NativeId(pair.Value),
            _ => true,
        });
    }

    private async Task<PlanExecutionResult> ExecuteBatchAsync(
        ProviderMutationOperation operation, PlanOrigin? expectedOrigin, CancellationToken ct)
    {
        if (!ValidBatch(operation))
            return PlanExecutionResult.Failure("Unsupported GitHub fields or concurrency; no write attempted.");
        try
        {
            using var session = await CaptureAsync(ct).ConfigureAwait(false);
            if (expectedOrigin is null || Origin(session.Reads) != expectedOrigin)
                return PlanExecutionResult.Failure("Proposal target, attachment, credential selection or authenticated actor changed; no write attempted.");
            await ValidateMappingAsync(operation.Target.Item, ct).ConfigureAwait(false);
            var fields = operation.Fields!;
            var pair = fields.First();
            var collection = CollectionFields.TryGetValue(pair.Key, out var shape);
            var resource = collection ? pair.Value : fields.GetValueOrDefault("github.milestoneId");
            if (resource is not null)
                await ValidateResourceAsync(session, resource,
                    collection ? pair.Key.StartsWith("github.labels.", StringComparison.Ordinal) ? "Label" : "User" : "Milestone", ct)
                    .ConfigureAwait(false);
            var before = await session.Reads.FetchSnapshotAsync(operation.Target.Item.ExternalIdentity, ct).ConfigureAwait(false);
            if (!HasNativeObservation(before) || before.RevisionToken != operation.Target.ExpectedProviderRevision)
                return PlanExecutionResult.Failure("Observed item token changed; no write attempted. Review a fresh proposal.");
            if (collection && !before.Fields.TryGetValue(shape.Snapshot, out _))
                return PlanExecutionResult.Failure("Complete native collection identity evidence is unavailable; no write attempted.");
            await targets.ValidateCapturedSessionAsync(session.Reads.Target, ct).ConfigureAwait(false);
            var mutation = collection ? shape.Mutation : "updateIssue";
            var input = collection ? shape.Input : "UpdateIssueInput";
            var result = collection ? shape.Result : "issue";
            var id = operation.Target.Item.ExternalIdentity.NativeId;
            return await SendNativeAsync(session, mutation, input, result, writer =>
            {
                writer.WriteString(collection ? shape.Member == "labelIds" ? "labelableId" : "assignableId" : "id", id);
                if (collection)
                {
                    writer.WriteStartArray(shape.Member);
                    writer.WriteStringValue(pair.Value);
                    writer.WriteEndArray();
                }
                else
                    foreach (var (field, value) in fields)
                        writer.WriteString(ScalarFields[field], field == "System.Description" ? value ?? string.Empty : value);
            }, id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsObservationFailure(ex))
        {
            return PlanExecutionResult.Failure("Native field preflight unavailable; no write attempted: " + SafeReason(ex));
        }
    }

    private async Task<PlanReadbackOutcome> ReadbackBatchAsync(
        ProviderMutationOperation operation, PlanOrigin? expectedOrigin, CancellationToken ct)
    {
        try
        {
            using var session = await CaptureAsync(ct).ConfigureAwait(false);
            if (expectedOrigin is null || Origin(session.Reads) != expectedOrigin)
                return PlanReadbackOutcome.Indeterminate("Original target or authenticated actor changed; native effect remains unresolved.");
            var snapshot = await session.Reads.FetchSnapshotAsync(operation.Target.Item.ExternalIdentity, ct).ConfigureAwait(false);
            if (!HasNativeObservation(snapshot))
                return PlanReadbackOutcome.Indeterminate("Native item timestamp observation is incomplete.");
            return FieldsMatch(snapshot, operation.Fields!)
                ? PlanReadbackOutcome.VerifiedWith("""{"nativeReadback":true,"concurrency":"observed-non-atomic"}""")
                : PlanReadbackOutcome.Failed("Native readback does not confirm the intended field effects; non-atomic interleaving remains possible.");
        }
        catch (Exception ex) when (IsObservationFailure(ex))
        {
            return PlanReadbackOutcome.Indeterminate("Native field readback unavailable: " + SafeReason(ex));
        }
    }

    private static bool FieldsMatch(ProviderWorkItemSnapshot snapshot, IReadOnlyDictionary<string, string?> fields)
    {
        foreach (var (field, expected) in fields)
        {
            if (CollectionFields.TryGetValue(field, out var collection))
            {
                if (!snapshot.Fields.TryGetValue(collection.Snapshot, out var raw)
                    || raw.Value is not StringListValue list
                    || list.Values.Contains(expected!, StringComparer.Ordinal) != field.EndsWith(".add", StringComparison.Ordinal))
                    return false;
                continue;
            }
            if (!snapshot.Fields.TryGetValue(field, out var value)) return false;
            var actual = value.Value switch
            {
                TextValue text => text.Value,
                RichTextValue text => text.Content,
                NullValue => null,
                _ => "\0",
            };
            if (field == "System.Description")
            {
                if ((actual ?? string.Empty) != (expected ?? string.Empty)) return false;
            }
            else if (actual != expected) return false;
        }
        return true;
    }

    private async Task ValidateResourceAsync(Session session, string id, string type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, scope.GraphQlEndpoint);
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Content = new StringContent(JsonSerializer.Serialize(new GitHubIssueGraphQlRequest(
            ResourceQuery, new() { ["id"] = id }), TwigJsonContext.Default.GitHubIssueGraphQlRequest), Encoding.UTF8, "application/json");
        using var http = NewHttpClient();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var node = Payload(body.RootElement, "node");
        if (GitHubRepositoryDiscovery.Text(node, "__typename") != type || GitHubRepositoryDiscovery.Text(node, "id") != id)
            throw new InvalidDataException("Native resource kind or identity changed.");
        if (type != "User")
        {
            var repo = GitHubRepositoryDiscovery.Required(node, "repository");
            if (new GitHubRepositoryTarget(GitHubRepositoryDiscovery.Text(
                    GitHubRepositoryDiscovery.Required(repo, "owner"), "id"),
                    GitHubRepositoryDiscovery.Text(repo, "id")) != session.Reads.Target.Target)
                throw new InvalidDataException("Native field resource is outside the bound repository.");
        }
    }

    private HttpClient NewHttpClient() => new(transport?.CreateHandler()
        ?? new SocketsHttpHandler { AllowAutoRedirect = false });

    private static JsonElement Payload(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)
            || root.TryGetProperty("errors", out var errors) && !(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0))
            throw new JsonException("Incomplete native response.");
        var data = GitHubRepositoryDiscovery.Required(root, "data");
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 1)
            throw new JsonException("Incomplete native payload.");
        return GitHubRepositoryDiscovery.Required(data, name);
    }

    private async Task<PlanExecutionResult> SendNativeAsync(
        Session session, string mutation, string inputType, string resultMember,
        Action<Utf8JsonWriter> input, string? expectedId, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query", $"mutation($input: {inputType}!) {{ {mutation}(input: $input) {{ {resultMember} {{ id }} }} }}");
            writer.WriteStartObject("variables");
            writer.WriteStartObject("input");
            input(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var http = NewHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, scope.GraphQlEndpoint);
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Content = new ByteArrayContent(buffer.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return PlanExecutionResult.Indeterminate($"GitHub mutation response HTTP {(int)response.StatusCode}; no redirect or retry. Native readback is required.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var result = GitHubRepositoryDiscovery.Required(Payload(body.RootElement, mutation), resultMember);
            var id = GitHubRepositoryDiscovery.Text(result, "id");
            if (!NativeId(id) || expectedId is not null && id != expectedId)
                return PlanExecutionResult.Indeterminate("Native acknowledgment identity does not match; no retry.");
            return PlanExecutionResult.Success(NativeResult(id));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            return PlanExecutionResult.Indeterminate("GitHub mutation response unavailable; the write may have landed. No retry; native readback is required.");
        }
    }

    private static string NativeResult(string id)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("nativeId", id);
            writer.WriteString("concurrency", "observed-non-atomic");
            writer.WriteBoolean("nativeAcknowledged", true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
