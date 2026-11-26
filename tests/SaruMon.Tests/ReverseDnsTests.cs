using System.Net;
using System.Net.Sockets;

namespace SaruMon.Tests;

internal static class ReverseDnsTests
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        await SuccessfulCacheExpires();
        await NegativeCacheExpires();
        await PreCancelledLookupCanRetry();
        await CancelledInFlightLookupCanRetry();
        await TimeoutBoundsNoncooperativeLookup();
        await LeastRecentlyUsedCacheIsBounded();
        await MappedIpv4SharesCache();
        await InvalidNamesFallBackToAddress();
        await ParallelLookupsKeepCacheBounded();
        await CancelledLookupDoesNotReplaceConcurrentSuccess();
        return 10;
    }

    private static async Task SuccessfulCacheExpires()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var calls = 0;
        var resolver = new ReverseDnsResolver((_, _) => Task.FromResult<string?>("host-" + ++calls + ".example"), clock: () => now);
        var ip = IPAddress.Parse("192.0.2.1");
        Assert(!resolver.TryGetCached(ip, out _), "An unread address must be a cache miss.");
        Assert(await resolver.ResolveAsync(ip, default) == "host-1.example", "Successful lookup was not returned.");
        Assert(await resolver.ResolveAsync(ip, default) == "host-1.example" && calls == 1, "A successful cache entry triggered another lookup.");
        now = now.AddMinutes(5);
        Assert(!resolver.TryGetCached(ip, out _), "Success cache must expire at its TTL.");
        Assert(await resolver.ResolveAsync(ip, default) == "host-2.example" && calls == 2, "Expired success was not refreshed.");
    }

    private static async Task NegativeCacheExpires()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var calls = 0;
        var resolver = new ReverseDnsResolver((_, _) => ++calls == 1
            ? Task.FromException<string?>(new SocketException((int)SocketError.HostNotFound))
            : Task.FromResult<string?>("available.example"), clock: () => now);
        var ip = IPAddress.Parse("192.0.2.2");
        Assert(await resolver.ResolveAsync(ip, default) is null, "DNS failure must fall back to the IP address.");
        Assert(resolver.TryGetCached(ip, out var name) && name is null, "Negative cache must be distinguishable from a miss.");
        Assert(await resolver.ResolveAsync(ip, default) is null && calls == 1, "Repeated failed addresses should not cause repeated DNS queries.");
        now = now.AddMinutes(1);
        Assert(await resolver.ResolveAsync(ip, default) == "available.example" && calls == 2, "Failure cache must permit retry after its shorter TTL.");
    }

    private static async Task PreCancelledLookupCanRetry()
    {
        var calls = 0;
        var resolver = new ReverseDnsResolver((_, _) => { calls++; return Task.FromResult<string?>("retry.example"); });
        var ip = IPAddress.Parse("192.0.2.3");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectCancellation(resolver.ResolveAsync(ip, cancellation.Token));
        Assert(calls == 0 && !resolver.TryGetCached(ip, out _), "Pre-cancellation must not query or cache.");
        Assert(await resolver.ResolveAsync(ip, default) == "retry.example" && calls == 1, "A cancelled request poisoned the retry.");
    }

    private static async Task CancelledInFlightLookupCanRetry()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ReverseDnsResolver(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return "retry.example";
        });
        var ip = IPAddress.Parse("192.0.2.4");
        using var cancellation = new CancellationTokenSource();
        var pending = resolver.ResolveAsync(ip, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await ExpectCancellation(pending);
        Assert(!resolver.TryGetCached(ip, out _), "User cancellation must not write a failure cache entry.");
        Assert(await resolver.ResolveAsync(ip, default) == "retry.example" && calls == 2, "In-flight cancellation prevented the next enabled lookup.");
    }

    private static async Task TimeoutBoundsNoncooperativeLookup()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var calls = 0;
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ReverseDnsResolver((_, _) => ++calls == 1 ? late.Task : Task.FromResult<string?>("retried.example"),
            timeout: TimeSpan.FromMilliseconds(40), clock: () => now);
        var ip = IPAddress.Parse("192.0.2.5");
        Assert(await resolver.ResolveAsync(ip, default).WaitAsync(TimeSpan.FromSeconds(3)) is null, "Timeout did not bound a lookup ignoring its cancellation token.");
        Assert(resolver.TryGetCached(ip, out var name) && name is null, "Timeout should use the failure TTL.");
        late.SetResult("too-late.example");
        Assert(await resolver.ResolveAsync(ip, default) is null && calls == 1, "A late lookup result replaced the timeout cache.");
        now = now.AddMinutes(1);
        Assert(await resolver.ResolveAsync(ip, default) == "retried.example" && calls == 2, "A timed-out address could not retry after expiry.");
    }

    private static async Task LeastRecentlyUsedCacheIsBounded()
    {
        var resolver = new ReverseDnsResolver((ip, _) => Task.FromResult<string?>("host-" + ip.GetAddressBytes()[3] + ".example"), capacity: 2);
        var first = IPAddress.Parse("192.0.2.11");
        var second = IPAddress.Parse("192.0.2.12");
        var third = IPAddress.Parse("192.0.2.13");
        await resolver.ResolveAsync(first, default);
        await resolver.ResolveAsync(second, default);
        Assert(resolver.TryGetCached(first, out _), "First address was not cached.");
        await resolver.ResolveAsync(third, default);
        Assert(resolver.TryGetCached(first, out _) && resolver.TryGetCached(third, out _) && !resolver.TryGetCached(second, out _),
            "Capacity eviction must keep the most recently used entries.");
        for (var index = 20; index < 100; index++) await resolver.ResolveAsync(IPAddress.Parse("192.0.2." + index), default);
        var retained = Enumerable.Range(1, 100).Count(index => resolver.TryGetCached(IPAddress.Parse("192.0.2." + index), out _));
        Assert(retained == 2, "Many remote addresses grew the cache beyond its configured capacity.");
    }

    private static async Task MappedIpv4SharesCache()
    {
        var calls = 0;
        var resolver = new ReverseDnsResolver((ip, _) =>
        {
            calls++;
            Assert(ip.AddressFamily == AddressFamily.InterNetwork, "IPv4-mapped address was not normalized before lookup.");
            return Task.FromResult<string?>("mapped.example");
        });
        var plain = IPAddress.Parse("192.0.2.6");
        Assert(await resolver.ResolveAsync(plain.MapToIPv6(), default) == "mapped.example", "Mapped address lookup failed.");
        Assert(await resolver.ResolveAsync(plain, default) == "mapped.example" && calls == 1, "IPv4 and its mapped form must share one cache entry.");
        Assert(resolver.TryGetCached(plain.MapToIPv6(), out var name) && name == "mapped.example", "Mapped cache reads did not normalize.");
    }

    private static async Task InvalidNamesFallBackToAddress()
    {
        var ip = IPAddress.Parse("192.0.2.7");
        foreach (var invalid in new string?[] { null, "", "  ", "bad\n.example", "bad\0.example", "bad host.example", new('a', 254), "192.0.2.7", "::ffff:192.0.2.7" })
        {
            var resolver = new ReverseDnsResolver((_, _) => Task.FromResult(invalid));
            Assert(await resolver.ResolveAsync(ip, default) is null, "Invalid host name was accepted.");
            Assert(resolver.TryGetCached(ip, out var name) && name is null, "Invalid names must use the failure cache.");
        }
        var longest = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61);
        var valid = new ReverseDnsResolver((_, _) => Task.FromResult<string?>(longest));
        Assert(longest.Length == 253 && await valid.ResolveAsync(ip, default) == longest, "The supported maximum host-name length was rejected.");
    }

    private static async Task ParallelLookupsKeepCacheBounded()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var resolver = new ReverseDnsResolver(async (ip, token) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(token);
            return "host-" + ip.GetAddressBytes()[3] + ".example";
        }, capacity: 3);
        var addresses = Enumerable.Range(1, 4).Select(index => IPAddress.Parse("198.51.100." + index)).ToArray();
        var pending = addresses.Select(ip => resolver.ResolveAsync(ip, default)).ToArray();
        Assert(started == 4, "The resolver unexpectedly blocked independent UI-batched lookups.");
        release.SetResult();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3));
        Assert(results.All(name => name is not null), "Parallel successful lookups lost results.");
        Assert(addresses.Count(ip => resolver.TryGetCached(ip, out _)) == 3, "Parallel completion exceeded cache capacity.");
    }

    private static async Task CancelledLookupDoesNotReplaceConcurrentSuccess()
    {
        var calls = 0;
        var resolver = new ReverseDnsResolver(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "surviving.example";
        });
        var ip = IPAddress.Parse("203.0.113.8");
        using var cancellation = new CancellationTokenSource();
        var cancelled = resolver.ResolveAsync(ip, cancellation.Token);
        Assert(await resolver.ResolveAsync(ip, default) == "surviving.example", "Independent lookup did not complete.");
        cancellation.Cancel();
        await ExpectCancellation(cancelled);
        Assert(resolver.TryGetCached(ip, out var name) && name == "surviving.example", "A cancelled request removed or poisoned a concurrent success.");
    }

    private static async Task ExpectCancellation(Task<string?> task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected user cancellation to be propagated.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Reverse DNS test failed: " + message);
    }
}
