using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>Read compatibility for original native requests; cannot authorize a new legacy create.</summary>
internal sealed record SeedPublishCorrelation(Guid Identity, DateTimeOffset IntentRecordedAt)
{
    public string Tag => "twig-publishing-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Identity.ToString("D"))));
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OwnsIntentTag { get; init; }
}
