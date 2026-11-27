using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Twig.Server;

internal sealed record PageAuthority(Guid Authority, Guid Process, Guid Epoch, Guid Owner, Guid Store, string Connection,
    Guid? Origin, Guid Partition, Guid Generation, long Watermark);
internal sealed record ReceivedPage(PageAuthority Authority, long Started, TimeSpan Lifetime, byte[] Body);
internal sealed record PartitionRead(string Code, IReadOnlyList<ReceivedPage> Pages);

// No cache or rendering here. A receiver must still apply its live revocation
// stream immediately before use; an enqueued HTTP response is not a new grant.
internal static class ReleasedPageReader
{
    internal static async Task<PartitionRead> ReadAsync(HttpClient client, Guid view, Guid bench, Guid partition, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var pages = new List<ReceivedPage>();
        void Discard() { foreach (var page in pages) Array.Clear(page.Body); pages.Clear(); }
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var used = 0;
                string? cursor = null;
                do
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"/api/v2/views/{view:D}/benches/{bench:D}/partitions/{partition:D}/page");
                    if (cursor is not null)
                    {
                        request.Headers.Add("X-Twig-Cursor", cursor);
                        request.Headers.Add("X-Twig-Generation", pages[0].Authority.Generation.ToString("D"));
                    }
                    var started = Stopwatch.GetTimestamp();
                    var sent = DateTimeOffset.UtcNow;
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                    await response.Content.LoadIntoBufferAsync(512 * 1024, budget.Token);
                    var bytes = await response.Content.ReadAsByteArrayAsync(budget.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        Discard();
                        using var failure = JsonDocument.Parse(bytes);
                        var code = failure.RootElement.GetProperty("code").GetString() ?? "RefreshRequired";
                        if (code == "ViewChanged" && attempt == 0) break;
                        return new(code, []);
                    }
                    string? Header(string name) => response.Headers.TryGetValues("X-Twig-" + name, out var values)
                        ? values.Single() : null;
                    Guid Id(string name) => Guid.ParseExact(Header(name) ?? "", "D");
                    var scope = new PageAuthority(Id("Authority"), Id("Process"), Id("Auth-Epoch"), Id("Owner"), Id("Store"),
                        Header("Connection") ?? throw new FormatException(), Header("Origin") is { } origin ? Guid.ParseExact(origin, "D") : null,
                        Id("Partition"), Id("Generation"), long.Parse(Header("Watermark") ?? "", CultureInfo.InvariantCulture));
                    var expires = DateTimeOffset.ParseExact(Header("Expires") ?? "", "O", CultureInfo.InvariantCulture);
                    var ttl = TimeSpan.FromMilliseconds(long.Parse(Header("Remaining-Ms") ?? "", CultureInfo.InvariantCulture));
                    if (expires - sent < ttl) ttl = expires - sent;
                    if (scope.Partition != partition || pages.Count != 0 && scope != pages[0].Authority)
                    { Array.Clear(bytes); Discard(); if (attempt == 0) break; return new("ViewChanged", []); }
                    pages.Add(new(scope, started, ttl, bytes));
                    used += bytes.Length;
                    if (used > 16 * 1024 * 1024 || pages.Count > 256) { Discard(); return new("Incomplete", []); }
                    cursor = Header("Next-Cursor");
                    if (cursor is null)
                    {
                        if (pages.Any(p => Stopwatch.GetElapsedTime(p.Started) >= p.Lifetime))
                        { Discard(); return new("RefreshRequired", []); }
                        return new("Complete", pages);
                    }
                } while (true);
            }
            return new("ViewChanged", []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { Discard(); return new("RefreshRequired", []); }
        catch { Discard(); throw; }
    }
}
