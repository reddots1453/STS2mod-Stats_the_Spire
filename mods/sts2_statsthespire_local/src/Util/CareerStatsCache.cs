using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityStats.Collection;
using CommunityStats.Config;

namespace CommunityStats.Util;

/// <summary>
/// Disk cache for CareerStatsData. Lets the Stats screen open instantly using the
/// last computed snapshot, while a fresh aggregation runs in the background.
/// One file per character filter ("" = all). Invalidated by RunHistoryAnalyzer
/// after a new run finishes.
///
/// PRD-04 §3.11 — manual feedback "加载时间过长，应将这些信息存放本地".
/// </summary>
public static class CareerStatsCache
{
    private const string FilePrefix = "career_";
    private const string FileSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static CareerStatsData? Load(string? characterFilter)
    {
        try
        {
            var path = PathFor(characterFilter);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<CareerStatsDto>(json, JsonOpts);
            if (dto == null) return null;
            // Round 9 round 49: reject snapshots written by an older schema
            // (no MaxWinStreak). If the player has any wins but the field is
            // 0, the cache predates the field — force a rebuild rather than
            // showing a wrong "0 best streak".
            if (dto.Wins > 0 && dto.MaxWinStreak == 0)
            {
                Safe.Info("CareerStatsCache.Load: stale cache (no MaxWinStreak) — discarding");
                return null;
            }
            return FromDto(dto);
        }
        catch (Exception ex)
        {
            Safe.Warn($"CareerStatsCache.Load failed: {ex.Message}");
            return null;
        }
    }

    public static void Save(CareerStatsData data)
    {
        if (data == null || data.IsEmpty) return;
        try
        {
            ModConfig.EnsureDirectories();
            var path = PathFor(data.CharacterFilter);
            var dto = ToDto(data);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch (Exception ex)
        {
            Safe.Warn($"CareerStatsCache.Save failed: {ex.Message}");
        }
    }

    /// <summary>Delete every cached snapshot. Called from InvalidateAll().</summary>
    public static void DeleteAll()
    {
        try
        {
            var dir = ModConfig.CacheDir;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, FilePrefix + "*" + FileSuffix))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex)
        {
            Safe.Warn($"CareerStatsCache.DeleteAll failed: {ex.Message}");
        }
    }

    private static string PathFor(string? characterFilter)
    {
        var key = string.IsNullOrEmpty(characterFilter) ? "all" : Sanitize(characterFilter!);
        return Path.Combine(ModConfig.CacheDir, FilePrefix + key + FileSuffix);
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var arr = s.ToCharArray();
        for (int i = 0; i < arr.Length; i++)
            if (Array.IndexOf(bad, arr[i]) >= 0) arr[i] = '_';
        return new string(arr);
    }

    // ── DTO mapping ─────────────────────────────────────────

    private static CareerStatsDto ToDto(CareerStatsData d) => new()
    {
        CharacterFilter = d.CharacterFilter,
        TotalRuns = d.TotalRuns,
        Wins = d.Wins,
        MaxWinStreak = d.MaxWinStreak,
        CurrentWinStreak = d.CurrentWinStreak,
        WinRateByWindow = d.WinRateByWindow.ToDictionary(kv => kv.Key, kv => kv.Value),
        DeathCausesByAct = d.DeathCausesByAct.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => new DeathEntryDto
            {
                EncounterId = e.EncounterId,
                Count = e.Count,
                Share = e.Share,
            }).ToList()),
        PathStatsByAct = d.PathStatsByAct.ToDictionary(
            kv => kv.Key,
            kv => new ActPathStatsDto
            {
                CardsGained = kv.Value.CardsGained,
                CardsBought = kv.Value.CardsBought,
                CardsRemoved = kv.Value.CardsRemoved,
                CardsUpgraded = kv.Value.CardsUpgraded,
                UnknownRooms = kv.Value.UnknownRooms,
                MonsterRooms = kv.Value.MonsterRooms,
                EliteRooms = kv.Value.EliteRooms,
                ShopRooms = kv.Value.ShopRooms,
                CampfireRooms = kv.Value.CampfireRooms,
                SampleSize = kv.Value.SampleSize,
            }),
        AncientPickRates = d.AncientPickRates.ToDictionary(
            kv => kv.Key,
            kv => new AncientChoiceStatsDto
            {
                TextKey = kv.Value.TextKey,
                Opportunities = kv.Value.Opportunities,
                Picks = kv.Value.Picks,
            }),
        BossStats = d.BossStats.ToDictionary(
            kv => kv.Key,
            kv => new BossEncounterStatsDto
            {
                EncounterId = kv.Value.EncounterId,
                Encounters = kv.Value.Encounters,
                Deaths = kv.Value.Deaths,
                AverageDamageTaken = kv.Value.AverageDamageTaken,
            }),
        AncientByElder = d.AncientByElder.ToDictionary(
            kv => kv.Key,
            kv => new ElderEntryDto
            {
                ElderId = kv.Value.ElderId,
                Encounters = kv.Value.Encounters,
                Options = kv.Value.Options.Select(o => new ElderOptionDto
                {
                    OptionTextKey = o.OptionTextKey,
                    Picks = o.Picks,
                    Relics = o.Relics.Select(r => new ElderRelicDto
                    {
                        RelicId = r.RelicId,
                        Picks = r.Picks,
                        Wins = r.Wins,
                    }).ToList(),
                }).ToList(),
            }),
    };

    private static CareerStatsData FromDto(CareerStatsDto d) => new()
    {
        CharacterFilter = d.CharacterFilter,
        TotalRuns = d.TotalRuns,
        Wins = d.Wins,
        MaxWinStreak = d.MaxWinStreak,
        CurrentWinStreak = d.CurrentWinStreak,
        WinRateByWindow = d.WinRateByWindow ?? new(),
        DeathCausesByAct = (d.DeathCausesByAct ?? new()).ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<DeathEntry>)kv.Value.Select(e => new DeathEntry
            {
                EncounterId = e.EncounterId,
                Count = e.Count,
                Share = e.Share,
            }).ToList()),
        PathStatsByAct = (d.PathStatsByAct ?? new()).ToDictionary(
            kv => kv.Key,
            kv => new ActPathStats
            {
                CardsGained = kv.Value.CardsGained,
                CardsBought = kv.Value.CardsBought,
                CardsRemoved = kv.Value.CardsRemoved,
                CardsUpgraded = kv.Value.CardsUpgraded,
                UnknownRooms = kv.Value.UnknownRooms,
                MonsterRooms = kv.Value.MonsterRooms,
                EliteRooms = kv.Value.EliteRooms,
                ShopRooms = kv.Value.ShopRooms,
                CampfireRooms = kv.Value.CampfireRooms,
                SampleSize = kv.Value.SampleSize,
            }),
        AncientPickRates = (d.AncientPickRates ?? new()).ToDictionary(
            kv => kv.Key,
            kv => new AncientChoiceStats
            {
                TextKey = kv.Value.TextKey,
                Opportunities = kv.Value.Opportunities,
                Picks = kv.Value.Picks,
            }),
        BossStats = (d.BossStats ?? new()).ToDictionary(
            kv => kv.Key,
            kv => new BossEncounterStats
            {
                EncounterId = kv.Value.EncounterId,
                Encounters = kv.Value.Encounters,
                Deaths = kv.Value.Deaths,
                AverageDamageTaken = kv.Value.AverageDamageTaken,
            }),
        AncientByElder = (d.AncientByElder ?? new()).ToDictionary(
            kv => kv.Key,
            kv => new ElderEntry
            {
                ElderId = kv.Value.ElderId,
                Encounters = kv.Value.Encounters,
                Options = kv.Value.Options.Select(o => new ElderOption
                {
                    OptionTextKey = o.OptionTextKey,
                    Picks = o.Picks,
                    Relics = o.Relics.Select(r => new ElderRelicStats
                    {
                        RelicId = r.RelicId,
                        Picks = r.Picks,
                        Wins = r.Wins,
                    }).ToList(),
                }).ToList(),
            }),
    };

    // ── DTOs (snake_case JSON) ──────────────────────────────

    private sealed class CareerStatsDto
    {
        [JsonPropertyName("char")]   public string? CharacterFilter { get; set; }
        [JsonPropertyName("runs")]   public int TotalRuns { get; set; }
        [JsonPropertyName("wins")]   public int Wins { get; set; }
        [JsonPropertyName("mws")]    public int MaxWinStreak { get; set; }
        [JsonPropertyName("cws")]    public int CurrentWinStreak { get; set; }
        [JsonPropertyName("wr")]     public Dictionary<int, float> WinRateByWindow { get; set; } = new();
        [JsonPropertyName("deaths")] public Dictionary<int, List<DeathEntryDto>> DeathCausesByAct { get; set; } = new();
        [JsonPropertyName("path")]   public Dictionary<int, ActPathStatsDto> PathStatsByAct { get; set; } = new();
        [JsonPropertyName("anc")]    public Dictionary<string, AncientChoiceStatsDto> AncientPickRates { get; set; } = new();
        [JsonPropertyName("boss")]   public Dictionary<string, BossEncounterStatsDto> BossStats { get; set; } = new();
        [JsonPropertyName("elder")]  public Dictionary<string, ElderEntryDto> AncientByElder { get; set; } = new();
    }

    private sealed class DeathEntryDto
    {
        [JsonPropertyName("e")] public string EncounterId { get; set; } = "";
        [JsonPropertyName("c")] public int Count { get; set; }
        [JsonPropertyName("s")] public float Share { get; set; }
    }

    private sealed class ActPathStatsDto
    {
        [JsonPropertyName("cg")] public float CardsGained { get; set; }
        [JsonPropertyName("cb")] public float CardsBought { get; set; }
        [JsonPropertyName("cr")] public float CardsRemoved { get; set; }
        [JsonPropertyName("cu")] public float CardsUpgraded { get; set; }
        [JsonPropertyName("u")]  public float UnknownRooms { get; set; }
        [JsonPropertyName("m")]  public float MonsterRooms { get; set; }
        [JsonPropertyName("el")] public float EliteRooms { get; set; }
        [JsonPropertyName("sh")] public float ShopRooms { get; set; }
        [JsonPropertyName("cf")] public float CampfireRooms { get; set; }
        [JsonPropertyName("ss")] public int SampleSize { get; set; }
    }

    private sealed class AncientChoiceStatsDto
    {
        [JsonPropertyName("k")] public string TextKey { get; set; } = "";
        [JsonPropertyName("o")] public int Opportunities { get; set; }
        [JsonPropertyName("p")] public int Picks { get; set; }
    }

    private sealed class BossEncounterStatsDto
    {
        [JsonPropertyName("id")] public string EncounterId { get; set; } = "";
        [JsonPropertyName("e")]  public int Encounters { get; set; }
        [JsonPropertyName("d")]  public int Deaths { get; set; }
        [JsonPropertyName("a")]  public float AverageDamageTaken { get; set; }
    }

    private sealed class ElderEntryDto
    {
        [JsonPropertyName("id")] public string ElderId { get; set; } = "";
        [JsonPropertyName("e")]  public int Encounters { get; set; }
        [JsonPropertyName("o")]  public List<ElderOptionDto> Options { get; set; } = new();
    }

    private sealed class ElderOptionDto
    {
        [JsonPropertyName("k")] public string OptionTextKey { get; set; } = "";
        [JsonPropertyName("p")] public int Picks { get; set; }
        [JsonPropertyName("r")] public List<ElderRelicDto> Relics { get; set; } = new();
    }

    private sealed class ElderRelicDto
    {
        [JsonPropertyName("id")] public string RelicId { get; set; } = "";
        [JsonPropertyName("p")]  public int Picks { get; set; }
        [JsonPropertyName("w")]  public int Wins { get; set; }
    }
}
