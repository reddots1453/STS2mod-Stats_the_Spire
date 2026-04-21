using System.Collections.Concurrent;
using System.Text.Json;
using CommunityStats.Config;
using CommunityStats.Util;

namespace CommunityStats.Api;

/// <summary>
/// Two-tier cache: in-memory (ConcurrentDictionary with TTL) + disk (JSON files with TTL).
/// The bulk bundle has its own dedicated slot; on-demand results use keyed entries.
/// </summary>
public sealed class StatsCache
{
    public static StatsCache Instance { get; } = new();

    private readonly ConcurrentDictionary<string, CacheEntry> _memory = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private record CacheEntry(object Data, DateTime Expiry);

    // ── Memory Cache ────────────────────────────────────────

    public void Set<T>(string key, T data)
    {
        var expiry = DateTime.UtcNow.AddSeconds(ModConfig.MemoryCacheTtlSeconds);
        _memory[key] = new CacheEntry(data!, expiry);
    }

    public T? Get<T>(string key) where T : class
    {
        if (!_memory.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow > entry.Expiry)
        {
            _memory.TryRemove(key, out _);
            return null;
        }
        return entry.Data as T;
    }

    public void Invalidate(string key) => _memory.TryRemove(key, out _);

    public void InvalidateAll() => _memory.Clear();

    // ── Disk Cache ──────────────────────────────────────────

    public void WriteDisk<T>(string key, T data)
    {
        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var path = DiskPath(key);
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(path, json);
        });
    }

    public T? ReadDisk<T>(string key) where T : class
    {
        return Safe.Run<T?>(() =>
        {
            var path = DiskPath(key);
            if (!File.Exists(path)) return null;

            var info = new FileInfo(path);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(ModConfig.DiskCacheTtlHours))
            {
                File.Delete(path);
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json);
        });
    }

    /// <summary>
    /// Removes disk cache files older than the configured TTL.
    /// </summary>
    public void CleanupDisk()
    {
        Safe.Run(() =>
        {
            if (!Directory.Exists(ModConfig.CacheDir)) return;
            var cutoff = DateTime.UtcNow.AddHours(-ModConfig.DiskCacheTtlHours);
            foreach (var file in Directory.GetFiles(ModConfig.CacheDir, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        });
    }

    private static string DiskPath(string key) =>
        Path.Combine(ModConfig.CacheDir, $"{key}.json");
}
