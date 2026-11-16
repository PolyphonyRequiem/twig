namespace Twig.Infrastructure.Auth;

/// <summary>Read compatibility for original native requests; cannot authorize a new legacy create.</summary>
internal sealed record SeedPublishCorrelation(Guid Identity, DateTimeOffset IntentRecordedAt);
