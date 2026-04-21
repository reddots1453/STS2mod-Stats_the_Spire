using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace CommunityStats.Util;

/// <summary>
/// Per-elder pool structure + act mapping for the Ancient relic compendium
/// (PRD-04 §3.11 round 5). Pool structure is hand-curated from each
/// AncientEventModel subclass because the elders use heterogeneous private
/// field names (Pael/Tezcatara/Orobas use OptionPool1-3, Vakuu uses
/// Pool1-3, Tanx/Nonupeipe use a single BaseOptionPool, Darv uses
/// dynamic ValidRelicSets) which makes reflection-based discovery brittle.
///
/// The act mapping comes from `ActModel.AllAncients` walked at first use:
///   Overgrowth (1) → NEOW
///   Hive       (2) → OROBAS / PAEL / TEZCATARA
///   Glory      (3) → NONUPEIPE / TANX / VAKUU
///   Underdocks (4) → NEOW (rerun)
///   shared         → DARV
/// </summary>
public static class AncientPoolMap
{
    /// <summary>One named option pool inside an elder. Order matches the in-game pool index.</summary>
    public sealed class Pool
    {
        public string DisplayKey { get; init; } = ""; // L key for "选项一池"/"Pool 1"
        public List<string> RelicIds { get; init; } = new();
        /// <summary>Optional per-relic act gate. Keys are relic IDs that only
        /// appear in a specific act (e.g. Darv's Ectoplasm/Sozu only in Act 2,
        /// PhilosophersStone/VelvetChoker only in Act 3). UI appends a
        /// "(only in Act N)" suffix when rendering these relics.</summary>
        public Dictionary<string, int>? ActGate { get; init; }
    }

    public sealed class ElderInfo
    {
        public string ElderId { get; init; } = "";
        public int ActIndex { get; init; }                // 1..4, 0 if shared
        public List<Pool> Pools { get; init; } = new();
    }

    private static readonly Dictionary<string, ElderInfo> _elders = Build();

    public static ElderInfo? Get(string elderId) =>
        _elders.TryGetValue(elderId, out var info) ? info : null;

    public static int ActIndexOf(string elderId) =>
        _elders.TryGetValue(elderId, out var info) ? info.ActIndex : 0;

    public static IEnumerable<ElderInfo> AllElders => _elders.Values;

    /// <summary>Try to load an elder's run-history icon via the game's model db.</summary>
    public static Texture2D? GetElderIcon(string elderId)
    {
        try
        {
            foreach (var ancient in ModelDb.AllAncients)
            {
                if (ancient.Id.Entry == elderId)
                {
                    try { return ancient.RunHistoryIcon; } catch { }
                    try { return ancient.MapIcon; } catch { }
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Warn($"AncientPoolMap.GetElderIcon failed for {elderId}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Try to load a relic's small icon texture.</summary>
    public static Texture2D? GetRelicIcon(string relicId)
    {
        try
        {
            foreach (var relic in ModelDb.AllRelics)
            {
                if (relic.Id.Entry == relicId)
                {
                    try { return relic.Icon; } catch { return null; }
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Warn($"AncientPoolMap.GetRelicIcon failed for {relicId}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Try to load a Boss / encounter icon used by the run history screen.
    /// Resolves the texture from `res://images/ui/run_history/{id}.png`,
    /// which is what `ImageHelper.GetRoomIconPath` would return for the
    /// same entry. Returns null if the asset isn't packed.
    /// </summary>
    public static Texture2D? GetEncounterIcon(string encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return null;
        try
        {
            var path = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath(
                "ui/run_history/" + encounterId.ToLowerInvariant() + ".png");
            if (Godot.ResourceLoader.Exists(path))
                return Godot.ResourceLoader.Load<Texture2D>(path, null, Godot.ResourceLoader.CacheMode.Reuse);
        }
        catch { }
        return null;
    }

    private static Dictionary<string, ElderInfo> Build()
    {
        // Hand-curated from _decompiled/sts2/MegaCrit.Sts2.Core.Models.Events/{Pael,Tezcatara,...}.cs
        var d = new Dictionary<string, ElderInfo>();

        // ── Act 1 — NEOW ───────────────────────────────────────
        // Round 9 split (Neow.cs lines 49-82, 192-245). Each Neow visit shows
        // 3 options = 2 randomly drawn from PositiveOptions + 1 randomly drawn
        // from CurseOptions. The two underlying pools are independent, so we
        // expose them as separate sub-pools so per-relic pick rates are
        // computed against the right denominator.
        d["NEOW"] = new ElderInfo
        {
            ElderId = "NEOW",
            ActIndex = 1,
            Pools = new()
            {
                // Positive: 9 base options + Cleric/Toughness/Safety/Patience/
                // Scavenger conditional adds. Cleric is multiplayer-only,
                // Toughness/Safety are coin-flipped, Patience/Scavenger only
                // appear when the curse isn't LargeCapsule.
                new Pool
                {
                    DisplayKey = "ancient.positive_pool",
                    RelicIds = new()
                    {
                        "ARCANE_SCROLL", "BOOMING_CONCH", "POMANDER", "GOLDEN_PEARL",
                        "LEAD_PAPERWEIGHT", "NEW_LEAF", "NEOWS_TORMENT", "PRECISE_SCISSORS",
                        "LOST_COFFER",
                        "MASSIVE_SCROLL", "NUTRITIOUS_OYSTER", "STONE_HUMIDIFIER",
                        "LAVA_ROCK", "SMALL_CAPSULE",
                    },
                },
                // Curse: 4 base options + ScrollBoxes (Bundle, conditional) +
                // SilverCrucible (Empower, single-player only).
                new Pool
                {
                    DisplayKey = "ancient.curse_pool",
                    RelicIds = new()
                    {
                        "CURSED_PEARL", "LARGE_CAPSULE", "LEAFY_POULTICE", "PRECARIOUS_SHEARS",
                        "SCROLL_BOXES", "SILVER_CRUCIBLE",
                    },
                },
            },
        };

        // ── Act 2 — PAEL ──────────────────────────────────────
        d["PAEL"] = new ElderInfo
        {
            ElderId = "PAEL",
            ActIndex = 2,
            Pools = new()
            {
                new Pool { DisplayKey = "ancient.pool_1", RelicIds = new() { "PAELS_FLESH", "PAELS_HORN", "PAELS_TEARS" } },
                new Pool { DisplayKey = "ancient.pool_2", RelicIds = new() { "PAELS_WING", "PAELS_CLAW", "PAELS_TOOTH", "PAELS_GROWTH" } },
                new Pool { DisplayKey = "ancient.pool_3", RelicIds = new() { "PAELS_EYE", "PAELS_BLOOD", "PAELS_LEGION" } },
            },
        };

        // ── Act 2 — TEZCATARA ─────────────────────────────────
        d["TEZCATARA"] = new ElderInfo
        {
            ElderId = "TEZCATARA",
            ActIndex = 2,
            Pools = new()
            {
                new Pool { DisplayKey = "ancient.pool_1", RelicIds = new() { "NUTRITIOUS_SOUP", "VERY_HOT_COCOA", "YUMMY_COOKIE" } },
                new Pool { DisplayKey = "ancient.pool_2", RelicIds = new() { "BIIIG_HUG", "STORYBOOK", "SEAL_OF_GOLD", "TOASTY_MITTENS" } },
                new Pool { DisplayKey = "ancient.pool_3", RelicIds = new() { "GOLDEN_COMPASS", "PUMPKIN_CANDLE", "TOY_BOX" } },
            },
        };

        // ── Act 2 — OROBAS ────────────────────────────────────
        // OptionPool1/2/3 + DiscoveryTotems + PrismaticGemOption.
        d["OROBAS"] = new ElderInfo
        {
            ElderId = "OROBAS",
            ActIndex = 2,
            Pools = new()
            {
                // Round 9 round 49: pool 1 includes the alternate slot-1
                // rewards (PrismaticGem 33% / SeaGlass 67%); the previous
                // DISCOVERY_TOTEM_* IDs were placeholders that never resolved.
                // See _decompiled/.../Models.Events/Orobas.cs for the canonical
                // option layout.
                new Pool { DisplayKey = "ancient.pool_1", RelicIds = new() { "ELECTRIC_SHRYMP", "GLASS_EYE", "SAND_CASTLE", "PRISMATIC_GEM", "SEA_GLASS" } },
                new Pool { DisplayKey = "ancient.pool_2", RelicIds = new() { "ALCHEMICAL_COFFER", "DRIFTWOOD", "RADIANT_PEARL" } },
                new Pool { DisplayKey = "ancient.pool_3", RelicIds = new() { "TOUCH_OF_OROBAS", "ARCHAIC_TOOTH" } },
            },
        };

        // ── Act 3 — VAKUU ─────────────────────────────────────
        d["VAKUU"] = new ElderInfo
        {
            ElderId = "VAKUU",
            ActIndex = 3,
            Pools = new()
            {
                new Pool { DisplayKey = "ancient.pool_1", RelicIds = new() { "BLOOD_SOAKED_ROSE", "WHISPERING_EARRING", "FIDDLE" } },
                new Pool { DisplayKey = "ancient.pool_2", RelicIds = new() { "PRESERVED_FOG", "SERE_TALON", "DISTINGUISHED_CAPE" } },
                new Pool { DisplayKey = "ancient.pool_3", RelicIds = new() { "CHOICES_PARADOX", "MUSIC_BOX", "LORDS_PARASOL", "JEWELED_MASK" } },
            },
        };

        // ── Act 3 — TANX ──────────────────────────────────────
        // Single pool of 9 weapons + ApexOption (TriBoomerang).
        d["TANX"] = new ElderInfo
        {
            ElderId = "TANX",
            ActIndex = 3,
            Pools = new()
            {
                new Pool
                {
                    DisplayKey = "ancient.pool_all",
                    RelicIds = new()
                    {
                        "CLAWS", "CROSSBOW", "IRON_CLUB", "MEAT_CLEAVER", "SAI",
                        "SPIKED_GAUNTLETS", "TANXS_WHISTLE", "THROWING_AXE", "WAR_HAMMER",
                        "TRI_BOOMERANG",
                    },
                },
            },
        };

        // ── Act 3 — NONUPEIPE ────────────────────────────────
        d["NONUPEIPE"] = new ElderInfo
        {
            ElderId = "NONUPEIPE",
            ActIndex = 3,
            Pools = new()
            {
                new Pool
                {
                    DisplayKey = "ancient.pool_all",
                    RelicIds = new()
                    {
                        "BLESSED_ANTLER", "BRILLIANT_SCARF", "DELICATE_FROND", "DIAMOND_DIADEM",
                        "FUR_COAT", "GLITTER", "JEWELRY_BOX", "LOOMING_FRUIT", "SIGNET_RING",
                        "BEAUTIFUL_BRACELET",
                    },
                },
            },
        };

        // ── Shared — DARV ─────────────────────────────────────
        // Darv uses 9 _validRelicSets + DustyTome (Darv.cs lines 191-201).
        // Sets 7 (Ectoplasm/Sozu) and 8 (PhilosophersStone/VelvetChoker) are
        // gated by CurrentActIndex (== 1 → Act 2; == 2 → Act 3). All other
        // sets are unrestricted across both acts. ActGate marks the gated
        // relics so the UI can flag them as act-only.
        d["DARV"] = new ElderInfo
        {
            ElderId = "DARV",
            ActIndex = 0,
            Pools = new()
            {
                new Pool
                {
                    DisplayKey = "ancient.pool_all",
                    RelicIds = new()
                    {
                        "ASTROLABE", "BLACK_STAR", "CALLING_BELL", "EMPTY_CAGE", "PANDORAS_BOX",
                        "RUNIC_PYRAMID", "SNECKO_EYE", "ECTOPLASM", "SOZU",
                        "PHILOSOPHERS_STONE", "VELVET_CHOKER", "DUSTY_TOME",
                    },
                    ActGate = new Dictionary<string, int>
                    {
                        ["ECTOPLASM"]          = 2,
                        ["SOZU"]               = 2,
                        ["PHILOSOPHERS_STONE"] = 3,
                        ["VELVET_CHOKER"]      = 3,
                    },
                },
            },
        };

        return d;
    }
}
