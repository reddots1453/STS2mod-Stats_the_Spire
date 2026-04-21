using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityStats.Api;
using CommunityStats.Config;

namespace CommunityStats.Util;

/// <summary>
/// Persist the in-memory shop purchase list (<c>RunDataCollector._shopPurchases</c>)
/// keyed by the active run seed. The game's MapPointHistory only records
/// colorless card buys in <c>BoughtColorless</c>; colored shop buys, relic
/// buys, and potion buys are not recoverable from the save. We write them
/// alongside to survive save+quit+resume and feed into per-act counts such
/// as <c>CardsBought</c> in the Run History detail view.
/// </summary>
public static class ShopPurchasePersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private static string PathFor(string seed)
        => Path.Combine(ModConfig.ContributionsDir, $"{Sanitize(seed)}_shop_purchases.json");

    private static string Sanitize(string seed)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new System.Text.StringBuilder(seed.Length);
        foreach (var c in seed)
            buf.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return buf.ToString();
    }

    public static void Save(List<ShopPurchaseUpload> purchases)
    {
        var seed = ContributionPersistence.GetActiveSeed();
        if (string.IsNullOrEmpty(seed)) return;
        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var path = PathFor(seed!);
            File.WriteAllText(path, JsonSerializer.Serialize(purchases, JsonOpts));
        });
    }

    public static List<ShopPurchaseUpload>? Load(string? seed = null)
    {
        seed ??= ContributionPersistence.GetActiveSeed();
        if (string.IsNullOrEmpty(seed)) return null;
        var path = PathFor(seed!);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ShopPurchaseUpload>>(json, JsonOpts);
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"ShopPurchasePersistence: load failed: {ex.Message}");
            return null;
        }
    }

    public static void Delete(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return;
        try
        {
            var path = PathFor(seed);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
