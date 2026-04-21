using System;
using CommunityStats.Config;
using MegaCrit.Sts2.Core.Localization;

namespace CommunityStats.Util;

/// <summary>
/// Resolves localized display names for game model IDs by querying the
/// game's LocManager tables directly. Used by the career stats / run history
/// UIs so death causes, bosses, ancient relics, etc. show the same translated
/// names the game uses (e.g. "女王", "燃烧之血") instead of raw IDs.
///
/// All methods return the input ID unchanged when the lookup fails so the
/// UI never shows an empty cell.
///
/// PRD-04 §3.11 — manual feedback "中文化未覆盖、英文代号".
/// </summary>
public static class NameLookup
{
    public static string Encounter(string id) => Lookup("encounters", id, ".title");
    public static string Event(string id)     => Lookup("events", id, ".title");
    public static string Relic(string id)     => Lookup("relics", id, ".title");
    public static string Card(string id)      => Lookup("cards", id, ".title");
    public static string Monster(string id)   => Lookup("monsters", id, ".name");
    public static string Potion(string id)    => Lookup("potions", id, ".title");

    /// <summary>
    /// Localized name for an Ancient elder (NEOW/PAEL/...). The game's loc
    /// tables don't carry these IDs as encounter titles, so we fall back to
    /// hardcoded Chinese names. PRD-04 §3.11 round 9 — user reported that
    /// 先古之民 dropdown was showing raw English IDs.
    /// </summary>
    public static string Ancient(string elderId)
    {
        if (string.IsNullOrEmpty(elderId)) return "";
        // Try the regular event/encounter tables first in case loc data exists.
        var ev = TryLookup("events", elderId, ".title");
        if (ev != null) return ev;
        var enc = TryLookup("encounters", elderId, ".title");
        if (enc != null) return enc;

        if (L.Current == L.Lang.CN)
        {
            return elderId switch
            {
                "NEOW"      => "涅奥",
                "PAEL"      => "帕埃尔",
                "TEZCATARA" => "泰兹卡塔拉",
                "OROBAS"    => "奥洛巴斯",
                "VAKUU"     => "瓦库",
                "TANX"      => "坦克斯",
                "NONUPEIPE" => "诺努皮佩",
                "DARV"      => "达弗",
                _ => elderId,
            };
        }
        return elderId switch
        {
            "NEOW"      => "Neow",
            "PAEL"      => "Pael",
            "TEZCATARA" => "Tezcatara",
            "OROBAS"    => "Orobas",
            "VAKUU"     => "Vakuu",
            "TANX"      => "Tanx",
            "NONUPEIPE" => "Nonupeipe",
            "DARV"      => "Darv",
            _ => elderId,
        };
    }

    /// <summary>
    /// Resolves a death cause id (which may be either an encounter or event id, or
    /// the sentinel "ABANDONED"). Tries encounters table first, then events.
    /// </summary>
    public static string DeathCause(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (id == "ABANDONED") return L.Get("career.cause_abandoned");

        var enc = TryLookup("encounters", id, ".title");
        if (enc != null) return enc;
        var ev = TryLookup("events", id, ".title");
        if (ev != null) return ev;
        var mon = TryLookup("monsters", id, ".name");
        if (mon != null) return mon;
        return id;
    }

    /// <summary>Localized "Act N" label, e.g. "第一幕"/"Act 1".</summary>
    public static string ActLabel(int actIndex)
    {
        try { return string.Format(L.Get("career.act_n"), actIndex); }
        catch { return "Act " + actIndex; }
    }

    private static string Lookup(string table, string id, string suffix)
    {
        return TryLookup(table, id, suffix) ?? id;
    }

    private static string? TryLookup(string table, string id, string suffix)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var key = id + suffix;
            if (!LocString.Exists(table, key)) return null;
            return LocManager.Instance.GetTable(table).GetRawText(key);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
