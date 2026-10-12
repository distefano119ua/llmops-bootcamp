using System.Collections.Concurrent;
using System.Diagnostics;

namespace LlmOps.Infrastructure;

public sealed class MemoryResponseCache : IDisposable
{
    private sealed record Entry(CachedResponse Response, long StoredAt);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeSpan ttl;
    private readonly Timer cleanupTimer;

    public MemoryResponseCache(TimeSpan ttl)
    {
        this.ttl = ttl;
        var cleanupInterval = TimeSpan.FromSeconds(Math.Min(ttl.TotalSeconds, 30));
        cleanupTimer = new Timer(_ => RemoveExpired(), null, cleanupInterval, cleanupInterval);
    }

    public CachedResponse? Get(string key)
    {
        if (!entries.TryGetValue(key, out var entry)) return null;
        if (!IsExpired(entry)) return entry.Response;

        RemoveEntry(key, entry);
        return null;
    }

    public void Set(string key, CachedResponse response) =>
        entries[key] = new Entry(response, Stopwatch.GetTimestamp());

    private bool IsExpired(Entry entry) => Stopwatch.GetElapsedTime(entry.StoredAt) >= ttl;

    private void RemoveExpired()
    {
        foreach (var entry in entries)
            if (IsExpired(entry.Value))
                RemoveEntry(entry.Key, entry.Value);
    }

    // Видаляємо лише перевірену версію запису, не новий запис паралельного запиту.
    private void RemoveEntry(string key, Entry entry) =>
        ((ICollection<KeyValuePair<string, Entry>>)entries).Remove(new(key, entry));

    public void Dispose() => cleanupTimer.Dispose();
}
