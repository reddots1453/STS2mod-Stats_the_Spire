using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityStats.Collection;
using CommunityStats.Config;

namespace CommunityStats.Util;

/// <summary>
/// Persist the in-memory shop-card-offerings list keyed by the active run seed.
/// Survives save+quit+resume.
/// </summary>
public static class ShopOfferingPersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private static string PathFor(string seed)
        => Path.Combine(ModConfig.ContributionsDir, $"{Sanitize(seed)}_shop_offers.json");

    private static string Sanitize(string seed)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new System.Text.StringBuilder(seed.Length);
        foreach (var c in seed)
            buf.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return buf.ToString();
    }

    public static void Save(List<LocalShopCardOffering> offerings)
    {
        var seed = ContributionPersistence.GetActiveSeed();
        if (string.IsNullOrEmpty(seed)) return;
        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var path = PathFor(seed!);
            File.WriteAllText(path, JsonSerializer.Serialize(offerings, JsonOpts));
        });
    }

    public static List<LocalShopCardOffering>? Load()
    {
        var seed = ContributionPersistence.GetActiveSeed();
        if (string.IsNullOrEmpty(seed)) return null;
        var path = PathFor(seed!);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<LocalShopCardOffering>>(json, JsonOpts);
        }
        catch (System.Exception ex)
        {
            Safe.Warn($"ShopOfferingPersistence: load failed: {ex.Message}");
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
