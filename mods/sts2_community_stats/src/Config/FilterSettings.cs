using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityStats.Config;

public class FilterSettings
{
    /// <summary>
    /// Resolved character ID used by query strings (e.g. "IRONCLAD" or null
    /// for all characters). This is the *output* of <see cref="ResolveCharacter"/>
    /// — callers normally do NOT set it directly. Set <see cref="CharacterFilterMode"/>
    /// instead and let the resolver fill this in based on run context.
    /// </summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>
    /// PRD §3.18 — user preference for character filter. Stored as a mode
    /// string so the actual filter value can vary with run context:
    ///   "auto" → match the character of the active run, null if no run
    ///   "all"  → all characters (null query)
    ///   "IRONCLAD"/"SILENT"/"DEFECT"/"NECROBINDER"/"REGENT" → that character
    /// Default is "auto".
    /// </summary>
    [JsonPropertyName("character_filter_mode")]
    public string CharacterFilterMode { get; set; } = "auto";

    [JsonPropertyName("min_ascension")]
    public int? MinAscension { get; set; }

    [JsonPropertyName("max_ascension")]
    public int? MaxAscension { get; set; }

    [JsonPropertyName("min_player_win_rate")]
    public float? MinPlayerWinRate { get; set; }

    [JsonPropertyName("num_players")]
    public int? NumPlayers { get; set; }

    [JsonPropertyName("game_version")]
    public string? GameVersion { get; set; }          // null = current, "all" = all versions

    [JsonPropertyName("auto_match_ascension")]
    public bool AutoMatchAscension { get; set; } = false;

    /// <summary>
    /// PRD §3.13: when true, all stats labels read from the local
    /// RunHistory aggregator instead of the community StatsCache.
    /// </summary>
    [JsonPropertyName("my_data_only")]
    public bool MyDataOnly { get; set; } = false;

    /// <summary>
    /// PRD §3.18.2 — Resolve <see cref="CharacterFilterMode"/> to an actual
    /// character ID using the current run context.
    /// - "all" → null (all characters)
    /// - "auto" → current run's character if a run is active, else null
    ///   (compendium / main menu fallback per PRD §3.18.2)
    /// - specific character ID → that ID verbatim
    /// </summary>
    public string? ResolveCharacter()
    {
        var mode = CharacterFilterMode ?? "auto";
        if (mode == "all") return null;
        if (mode == "auto")
        {
            try
            {
                var state = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
                var player = state?.Players?.FirstOrDefault();
                return player?.Character?.Id.Entry; // null when no active run
            }
            catch
            {
                return null;
            }
        }
        return mode; // specific character id
    }

    /// <summary>
    /// Builds query string parameters for API requests. The character
    /// component is resolved through <see cref="ResolveCharacter"/> so the
    /// "auto" mode picks up the current run's character at request time.
    /// </summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        var resolvedChar = ResolveCharacter();
        if (resolvedChar != null) parts.Add($"char={Uri.EscapeDataString(resolvedChar)}");
        if (MinAscension.HasValue) parts.Add($"min_asc={MinAscension.Value}");
        if (MaxAscension.HasValue) parts.Add($"max_asc={MaxAscension.Value}");
        if (MinPlayerWinRate.HasValue) parts.Add($"min_wr={MinPlayerWinRate.Value:F2}");
        if (NumPlayers.HasValue) parts.Add($"num_players={NumPlayers.Value}");
        if (GameVersion != null) parts.Add($"ver={Uri.EscapeDataString(GameVersion)}");
        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    /// <summary>
    /// Deterministic hash for cache keying.
    /// </summary>
    public string Hash()
    {
        var json = JsonSerializer.Serialize(this);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public override bool Equals(object? obj)
    {
        if (obj is not FilterSettings other) return false;
        // Equality compares the *resolved* character so two filters with the
        // same effective character match (e.g. mode="auto" during a Silent
        // run equals an explicit "SILENT" preference).
        return ResolveCharacter() == other.ResolveCharacter()
            && MinAscension == other.MinAscension
            && MaxAscension == other.MaxAscension
            && MinPlayerWinRate == other.MinPlayerWinRate
            && NumPlayers == other.NumPlayers
            && GameVersion == other.GameVersion
            && MyDataOnly == other.MyDataOnly;
    }

    public override int GetHashCode() =>
        System.HashCode.Combine(ResolveCharacter(), MinAscension, MaxAscension, MinPlayerWinRate, NumPlayers, GameVersion, MyDataOnly);

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ModConfig.SettingsPath, json);
    }

    public static FilterSettings Load()
    {
        if (!File.Exists(ModConfig.SettingsPath)) return new FilterSettings();
        try
        {
            var json = File.ReadAllText(ModConfig.SettingsPath);
            return JsonSerializer.Deserialize<FilterSettings>(json) ?? new FilterSettings();
        }
        catch { return new FilterSettings(); }
    }
}
