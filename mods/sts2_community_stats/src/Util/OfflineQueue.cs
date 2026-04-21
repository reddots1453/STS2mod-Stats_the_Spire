using System.Text.Json;
using CommunityStats.Config;

namespace CommunityStats.Util;

/// <summary>
/// Disk-backed queue for run uploads that failed due to network issues.
/// Payloads are serialized to individual JSON files and retried on next opportunity.
///
/// PRD-04 §4.6: max 10 queued payloads, drop entries older than 7 days.
/// </summary>
public static class OfflineQueue
{
    private const int MaxEntries = 10;
    private const int RetentionDays = 7;

    public static void Enqueue<T>(T payload)
    {
        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var filename = $"run_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json";
            var path = Path.Combine(ModConfig.PendingDir, filename);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            Safe.Info($"Queued offline upload: {filename}");

            // PRD §4.6: enforce max 10 entries, evicting the oldest first.
            TrimToMaxEntries();
        });
    }

    /// <summary>
    /// Drop payloads older than the retention window. Called by Enqueue/Drain.
    /// </summary>
    private static void PruneExpired()
    {
        if (!Directory.Exists(ModConfig.PendingDir)) return;
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var path in Directory.EnumerateFiles(ModConfig.PendingDir, "*.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                    Safe.Info($"Offline queue: pruned expired entry {Path.GetFileName(path)}");
                }
            }
            catch (Exception ex)
            {
                Safe.Warn($"Offline queue: prune failed for {path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enforce the maximum queue depth by deleting the oldest entries.
    /// </summary>
    private static void TrimToMaxEntries()
    {
        if (!Directory.Exists(ModConfig.PendingDir)) return;
        var files = Directory.GetFiles(ModConfig.PendingDir, "*.json")
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToList();
        int excess = files.Count - MaxEntries;
        for (int i = 0; i < excess; i++)
        {
            try
            {
                File.Delete(files[i]);
                Safe.Info($"Offline queue: evicted {Path.GetFileName(files[i])} (max {MaxEntries})");
            }
            catch (Exception ex)
            {
                Safe.Warn($"Offline queue: evict failed for {files[i]}: {ex.Message}");
            }
        }
    }

    // Server rate limit is 10 uploads/minute per IP (nginx + slowapi).
    // Pace sustained uploads at ~6.5 s each so we stay under the rate
    // even across multiple drains. First upload in each drain fires
    // immediately (the burst=5 allowance absorbs it).
    private const int UploadPacingMs = 6500;

    /// <summary>
    /// Attempts to upload all pending payloads. Called at mod init and
    /// after successful uploads. Paces requests to respect the server's
    /// 10/min rate limit, and aborts early on HTTP 429 so the remaining
    /// files get retried on the next drain cycle instead of being
    /// hammered against a throttled endpoint.
    /// </summary>
    public static async Task DrainAsync(Func<string, Task<int>> uploadFunc)
    {
        if (!Directory.Exists(ModConfig.PendingDir)) return;

        // PRD §4.6: drop expired entries before attempting drain.
        PruneExpired();

        var files = Directory.GetFiles(ModConfig.PendingDir, "*.json")
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0) return;
        Safe.Info($"Draining offline queue: {files.Count} pending uploads");

        bool first = true;
        foreach (var file in files)
        {
            if (!first) await Task.Delay(UploadPacingMs);
            first = false;

            var json = await File.ReadAllTextAsync(file);
            int status = 0;
            var backoff = 1000; // exponential backoff on transient errors

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { status = await uploadFunc(json); }
                catch { status = 0; }

                if (status >= 200 && status < 300) break;

                // Permanent client errors (4xx except 429) won't succeed on retry.
                // Keep the file queued and move on so one poison payload
                // doesn't block the rest of the queue.
                if (status >= 400 && status < 500 && status != 429) break;

                // Rate-limited: abort the drain. Coming back later is the
                // only thing that actually helps here.
                if (status == 429)
                {
                    Safe.Warn("Offline drain hit rate limit (429); stopping until next cycle");
                    return;
                }

                await Task.Delay(backoff);
                backoff *= 2;
            }

            bool success = status >= 200 && status < 300;
            if (success)
            {
                File.Delete(file);
                Safe.Info($"Offline upload succeeded: {Path.GetFileName(file)}");
            }
            else
            {
                Safe.Warn($"Offline upload still failing ({status}): {Path.GetFileName(file)}, will retry next time");
                // Keep draining: a 4xx on one payload shouldn't block newer
                // runs behind it. The pacing above still applies.
            }
        }
    }
}
