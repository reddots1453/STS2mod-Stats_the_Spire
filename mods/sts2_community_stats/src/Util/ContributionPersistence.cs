using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityStats.Collection;
using CommunityStats.Config;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.Util;

/// <summary>
/// Serializes per-combat and per-run contribution data to disk so the
/// Run History detail screen can load and replay them later.
///
/// Layout:
///   {DataDir}/contributions/{seed}_{floor}.json   — single combat
///   {DataDir}/contributions/{seed}_summary.json   — full run roll-up
///
/// Files older than 90 days are pruned at mod init.
///
/// PRD-04 §3.12 (Run History "查看贡献" button), Phase 6 task 7.
/// </summary>
public static class ContributionPersistence
{
    private const int RetentionDays = 90;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    // ── Public API ──────────────────────────────────────────

    /// <summary>
    /// Resolve the seed of the active run, or null if no run is active.
    /// </summary>
    public static string? GetActiveSeed()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.Rng?.StringSeed;
        }
        catch (Exception ex)
        {
            Safe.Warn($"ContributionPersistence: GetActiveSeed failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persist a single combat snapshot. Called from CombatLifecyclePatch.AfterCombatEnded.
    /// </summary>
    public static void SaveCombat(int floor, IReadOnlyDictionary<string, ContributionAccum>? data)
    {
        if (data == null || data.Count == 0) return;
        var seed = GetActiveSeed();
        if (string.IsNullOrEmpty(seed)) return;

        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var path = CombatPath(seed, floor);
            var dto = new ContributionDoc
            {
                Seed = seed,
                Floor = floor,
                Kind = "combat",
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Sources = data.Values.Select(ToDto).ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        });
    }

    /// <summary>
    /// Persist the full-run aggregate. Called from RunLifecyclePatch / RunDataCollector.OnMetricsUpload.
    /// </summary>
    public static void SaveRunSummary(string seed, IReadOnlyDictionary<string, ContributionAccum>? data)
    {
        if (data == null || data.Count == 0 || string.IsNullOrEmpty(seed)) return;

        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var path = SummaryPath(seed);
            var dto = new ContributionDoc
            {
                Seed = seed,
                Kind = "summary",
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Sources = data.Values.Select(ToDto).ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        });
    }

    /// <summary>
    /// Load the run summary for the given seed (used by Run History "查看贡献").
    /// Returns null when no file exists or it cannot be parsed.
    ///
    /// Manual feedback Q4: defeat/abandoned runs may not have a *_summary.json
    /// (the metrics upload hook fires only on completion). If the canonical
    /// summary file is missing we walk every per-combat snapshot saved for the
    /// same seed and merge them on the fly so the player can still review the
    /// contribution chart of any historical run that had at least one combat.
    /// </summary>
    public static IReadOnlyDictionary<string, ContributionAccum>? LoadRunSummary(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return null;
        var direct = TryLoadDoc(SummaryPath(seed));
        if (direct != null && direct.Count > 0) return direct;
        return AssembleFromCombats(seed);
    }

    private static IReadOnlyDictionary<string, ContributionAccum>? TryLoadDoc(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<ContributionDoc>(json, JsonOpts);
            return dto?.Sources?.ToDictionary(s => s.SourceId, FromDto);
        }
        catch (Exception ex)
        {
            Safe.Warn($"ContributionPersistence: load {Path.GetFileName(path)} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Round 14 v5+ post-test fix: re-hydrate
    /// <see cref="RunContributionAggregator._runTotals"/> from every per-combat
    /// file on disk. Called at upload time so the payload always has the
    /// authoritative per-combat record even when in-memory state was wiped
    /// by a save+quit+Reset cycle that failed to replay live.json.
    ///
    /// Per-combat files are written at combat end (CombatLifecyclePatch) and
    /// never touched until the full run is retired, so they are a reliable
    /// disk-side source of truth.
    /// </summary>
    public static void AssembleAndHydrateRunTotals(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return;
        var merged = AssembleFromCombats(seed);
        if (merged == null || merged.Count == 0) return;
        RunContributionAggregator.Instance.HydrateRunTotals(merged);
        Safe.Info($"ContributionPersistence: hydrated run totals from {merged.Count} disk sources (seed={seed})");
    }

    /// <summary>
    /// Walk every {seed}_{floor}.json snapshot and merge into a single dictionary.
    /// Used as a fallback when no _summary.json was written for the run.
    /// </summary>
    public static IReadOnlyDictionary<string, ContributionAccum>? AssembleFromCombats(string seed)
    {
        var dir = ModConfig.ContributionsDir;
        if (!Directory.Exists(dir)) return null;
        var sanitized = Sanitize(seed);
        var pattern = sanitized + "_*.json";
        var merged = new Dictionary<string, ContributionAccum>();
        int files = 0;
        foreach (var path in Directory.EnumerateFiles(dir, pattern))
        {
            // Skip the canonical summary file (already tried above and missing).
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("_summary", StringComparison.Ordinal)) continue;
            // Skip the live-state file (different schema: LiveContributionDoc).
            if (name.EndsWith("_live", StringComparison.Ordinal)) continue;
            // Skip the shop-offerings side-channel file — it's a plain JSON
            // array, not a ContributionDoc, and trying to parse it triggers
            // a JsonException warning on every upload.
            if (name.EndsWith("_shop_offers", StringComparison.Ordinal)) continue;

            var combat = TryLoadDoc(path);
            if (combat == null) continue;
            files++;
            foreach (var (key, accum) in combat)
            {
                if (merged.TryGetValue(key, out var existing))
                    existing.MergeFrom(accum);
                else
                    merged[key] = Clone(accum);
            }
        }
        return files > 0 ? merged : null;
    }

    private static ContributionAccum Clone(ContributionAccum src)
    {
        var dst = new ContributionAccum
        {
            SourceId = src.SourceId,
            SourceType = src.SourceType,
        };
        dst.MergeFrom(src);
        return dst;
    }

    // ── Live mid-run state (round 8 §3.6.1) ─────────────────

    /// <summary>
    /// Persist the in-progress combat tracker state so a save+quit mid-run
    /// doesn't lose 本场战斗 / 本局汇总 data. Called from CombatTracker
    /// after every card play and from CombatLifecyclePatch on combat end.
    /// </summary>
    public static void SaveLiveState(LiveContributionSnapshot snapshot)
    {
        if (snapshot == null) return;
        var seed = GetActiveSeed();
        if (string.IsNullOrEmpty(seed)) return;

        Safe.Run(() =>
        {
            ModConfig.EnsureDirectories();
            var path = LivePath(seed);
            var dto = new LiveContributionDoc
            {
                Seed = seed,
                EncounterId = snapshot.EncounterId,
                EncounterType = snapshot.EncounterType,
                Floor = snapshot.Floor,
                TurnCount = snapshot.TurnCount,
                TotalDamageDealt = snapshot.TotalDamageDealt,
                DamageTakenByPlayer = snapshot.DamageTakenByPlayer,
                CombatInProgress = snapshot.CombatInProgress,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CurrentCombat = snapshot.CurrentCombat?.Values.Select(ToDto).ToList() ?? new(),
                RunTotals = snapshot.RunTotals?.Values.Select(ToDto).ToList() ?? new(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        });
    }

    /// <summary>
    /// Load the live state for the given seed. Returns null when no live
    /// snapshot exists or when the file fails to parse.
    /// </summary>
    public static LiveContributionSnapshot? LoadLiveState(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return null;
        var path = LivePath(seed);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<LiveContributionDoc>(json, JsonOpts);
            if (dto == null) return null;
            var snap = new LiveContributionSnapshot
            {
                EncounterId = dto.EncounterId ?? "",
                EncounterType = dto.EncounterType ?? "",
                Floor = dto.Floor,
                TurnCount = dto.TurnCount,
                TotalDamageDealt = dto.TotalDamageDealt,
                DamageTakenByPlayer = dto.DamageTakenByPlayer,
                CombatInProgress = dto.CombatInProgress,
            };
            if (dto.CurrentCombat != null && dto.CurrentCombat.Count > 0)
                snap.CurrentCombat = dto.CurrentCombat.ToDictionary(s => s.SourceId, FromDto);
            if (dto.RunTotals != null && dto.RunTotals.Count > 0)
                snap.RunTotals = dto.RunTotals.ToDictionary(s => s.SourceId, FromDto);
            return snap;
        }
        catch (Exception ex)
        {
            Safe.Warn($"ContributionPersistence: LoadLiveState failed for {seed}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Delete the live snapshot for a finished run.</summary>
    public static void DeleteLiveState(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return;
        try
        {
            var path = LivePath(seed);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// Delete contribution files older than 90 days. Called once at mod init.
    /// </summary>
    public static void PruneOldFiles()
    {
        Safe.Run(() =>
        {
            var dir = ModConfig.ContributionsDir;
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            int removed = 0;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    Safe.Warn($"ContributionPersistence: failed to prune {path}: {ex.Message}");
                }
            }
            if (removed > 0) Safe.Info($"Contribution prune removed {removed} expired files.");
        });
    }

    // ── Path helpers ────────────────────────────────────────

    private static string CombatPath(string seed, int floor)
        => Path.Combine(ModConfig.ContributionsDir, $"{Sanitize(seed)}_{floor}.json");

    private static string SummaryPath(string seed)
        => Path.Combine(ModConfig.ContributionsDir, $"{Sanitize(seed)}_summary.json");

    private static string LivePath(string seed)
        => Path.Combine(ModConfig.ContributionsDir, $"{Sanitize(seed)}_live.json");

    private static string Sanitize(string seed)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = seed.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    // ── DTO mapping ─────────────────────────────────────────

    private static SourceDto ToDto(ContributionAccum a) => new()
    {
        SourceId = a.SourceId,
        SourceType = a.SourceType,
        TimesPlayed = a.TimesPlayed,
        DirectDamage = a.DirectDamage,
        AttributedDamage = a.AttributedDamage,
        ModifierDamage = a.ModifierDamage,
        UpgradeDamage = a.UpgradeDamage,
        EffectiveBlock = a.EffectiveBlock,
        ModifierBlock = a.ModifierBlock,
        UpgradeBlock = a.UpgradeBlock,
        MitigatedByDebuff = a.MitigatedByDebuff,
        MitigatedByBuff = a.MitigatedByBuff,
        MitigatedByStrReduction = a.MitigatedByStrReduction,
        SelfDamage = a.SelfDamage,
        CardsDrawn = a.CardsDrawn,
        EnergyGained = a.EnergyGained,
        HpHealed = a.HpHealed,
        StarsContribution = a.StarsContribution,
        OriginSourceId = a.OriginSourceId,
    };

    private static ContributionAccum FromDto(SourceDto d) => new()
    {
        SourceId = d.SourceId,
        SourceType = d.SourceType,
        TimesPlayed = d.TimesPlayed,
        DirectDamage = d.DirectDamage,
        AttributedDamage = d.AttributedDamage,
        ModifierDamage = d.ModifierDamage,
        UpgradeDamage = d.UpgradeDamage,
        EffectiveBlock = d.EffectiveBlock,
        ModifierBlock = d.ModifierBlock,
        UpgradeBlock = d.UpgradeBlock,
        MitigatedByDebuff = d.MitigatedByDebuff,
        MitigatedByBuff = d.MitigatedByBuff,
        MitigatedByStrReduction = d.MitigatedByStrReduction,
        SelfDamage = d.SelfDamage,
        CardsDrawn = d.CardsDrawn,
        EnergyGained = d.EnergyGained,
        HpHealed = d.HpHealed,
        StarsContribution = d.StarsContribution,
        OriginSourceId = d.OriginSourceId,
    };

    // ── Wire format ─────────────────────────────────────────

    private sealed class ContributionDoc
    {
        public string Seed { get; set; } = "";
        public int? Floor { get; set; }
        public string Kind { get; set; } = "";
        public long SavedAtUnix { get; set; }
        public List<SourceDto> Sources { get; set; } = new();
    }

    /// <summary>Live mid-run snapshot — `{seed}_live.json`. Round 8 §3.6.1.</summary>
    private sealed class LiveContributionDoc
    {
        public string Seed { get; set; } = "";
        public string? EncounterId { get; set; }
        public string? EncounterType { get; set; }
        public int Floor { get; set; }
        public int TurnCount { get; set; }
        public int TotalDamageDealt { get; set; }
        public int DamageTakenByPlayer { get; set; }
        public bool CombatInProgress { get; set; }
        public long SavedAtUnix { get; set; }
        public List<SourceDto> CurrentCombat { get; set; } = new();
        public List<SourceDto> RunTotals { get; set; } = new();
    }

    private sealed class SourceDto
    {
        public string SourceId { get; set; } = "";
        public string SourceType { get; set; } = "card";
        public int TimesPlayed { get; set; }
        public int DirectDamage { get; set; }
        public int AttributedDamage { get; set; }
        public int ModifierDamage { get; set; }
        public int UpgradeDamage { get; set; }
        public int EffectiveBlock { get; set; }
        public int ModifierBlock { get; set; }
        public int UpgradeBlock { get; set; }
        public int MitigatedByDebuff { get; set; }
        public int MitigatedByBuff { get; set; }
        public int MitigatedByStrReduction { get; set; }
        public int SelfDamage { get; set; }
        public int CardsDrawn { get; set; }
        public int EnergyGained { get; set; }
        public int HpHealed { get; set; }
        public int StarsContribution { get; set; }
        public string? OriginSourceId { get; set; }
    }
}
