namespace Twig.Domain.ValueObjects;

/// <summary>Exact native staged-create origin, stamped through the existing publish tag mechanism, never a Plan selector.</summary>
public sealed record SeedPublishCorrelation(StagedIdentity Identity, DateTimeOffset IntentRecordedAt)
{
    /// <summary>Opaque server correlation derived solely from the durable staged identity; retry cannot mint another tag.</summary>
    public string Tag => "twig-publishing-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Identity.ToString())));
}
