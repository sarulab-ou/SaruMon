using System.Net;
using System.Net.Sockets;

namespace SaruMon;

/// <summary>Optional reverse DNS with bounded positive/negative caching. No lookup occurs on cache reads.</summary>
internal sealed class ReverseDnsResolver
{
    private readonly Func<IPAddress, CancellationToken, Task<string?>> lookup;
    private readonly Func<DateTimeOffset> clock;
    private readonly int capacity;
    private readonly TimeSpan timeout;
    private readonly TimeSpan successTtl;
    private readonly TimeSpan failureTtl;
    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = new();

    public ReverseDnsResolver(
        Func<IPAddress, CancellationToken, Task<string?>>? lookup = null,
        int capacity = 256,
        TimeSpan? timeout = null,
        TimeSpan? successTtl = null,
        TimeSpan? failureTtl = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.timeout = timeout ?? TimeSpan.FromSeconds(2);
        this.successTtl = successTtl ?? TimeSpan.FromMinutes(5);
        this.failureTtl = failureTtl ?? TimeSpan.FromMinutes(1);
        if (this.timeout <= TimeSpan.Zero || this.timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (this.successTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(successTtl));
        if (this.failureTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(failureTtl));
        this.capacity = capacity;
        this.lookup = lookup ?? LookupHostNameAsync;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    // true + null represents a cached failure, distinct from a cache miss.
    public bool TryGetCached(IPAddress ip, out string? name)
    {
        var key = Normalize(ip).ToString();
        lock (gate)
        {
            if (cache.TryGetValue(key, out var entry))
            {
                if (clock() < entry.ExpiresAt)
                {
                    recency.Remove(entry.Recency);
                    recency.AddLast(entry.Recency);
                    name = entry.Name;
                    return true;
                }
                RemoveEntry(key, entry);
            }
        }
        name = null;
        return false;
    }

    public async Task<string?> ResolveAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        var address = Normalize(ip);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCached(address, out var cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        string? name;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(timeout);
            try
            {
                // WaitAsync also bounds injected/noncooperative lookups; user cancellation
                // remains separate from a timeout and must never write a negative cache entry.
                var result = await lookup(address, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                name = ValidateHostName(result);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                name = null;
            }
            catch (SocketException)
            {
                name = null;
            }
            catch (TimeoutException)
            {
                name = null;
            }
        }

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = address.ToString();
            if (cache.TryGetValue(key, out var existing)) RemoveEntry(key, existing);
            while (cache.Count >= capacity)
            {
                var oldest = recency.First!;
                cache.Remove(oldest.Value);
                recency.RemoveFirst();
            }
            var now = clock();
            var ttl = name is null ? failureTtl : successTtl;
            var expires = ttl >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(ttl);
            cache.Add(key, new CacheEntry(name, expires, recency.AddLast(key)));
        }
        return name;
    }

    private static IPAddress Normalize(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static async Task<string?> LookupHostNameAsync(IPAddress ip, CancellationToken cancellationToken) =>
        (await Dns.GetHostEntryAsync(ip.ToString(), cancellationToken).ConfigureAwait(false)).HostName;

    private static string? ValidateHostName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 253 ||
            name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            return null;
        // A PTR failure can be represented by the input address; display the IP once
        // rather than present an address literal as if it were a resolved host name.
        return IPAddress.TryParse(name, out _) ? null : name;
    }

    private void RemoveEntry(string key, CacheEntry entry)
    {
        cache.Remove(key);
        recency.Remove(entry.Recency);
    }

    private sealed record CacheEntry(string? Name, DateTimeOffset ExpiresAt, LinkedListNode<string> Recency);
}
