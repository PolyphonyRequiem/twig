namespace Twig.Infrastructure.Persistence;

internal sealed record StoreParityReport(
    int FormatVersion,
    int? MirrorSchemaVersion,
    int DurableSchemaVersion,
    bool IsConsistent,
    StoreParityClassReport[] Classes,
    StoreParityCheckResult[] Checks,
    StoreParityAnomaly[] Anomalies);

internal sealed record StoreParityClassReport(
    string ClassName,
    bool Available,
    long? RowCount,
    string? CanonicalDigest,
    string[] Anomalies);

internal sealed record StoreParityCheckResult(
    string Name,
    string Status,
    long AffectedRows,
    string? AnomalyCode,
    string? DetailCode);

internal sealed record StoreParityAnomaly(string Code, long Count);
