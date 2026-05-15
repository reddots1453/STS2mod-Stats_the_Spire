using System.Text.Json;

namespace CommunityStats.Config;

/// <summary>
/// Central configuration for the Community Stats mod.
/// </summary>
public static class ModConfig
{
    public const string ModVersion = "0.16.2";

    // Server (can be overridden via config.json for local testing)
    public static string ApiBaseUrl { get; set; } = "https://statsthespire.org.cn/v1";
    public static int QueryTimeoutMs { get; set; } = 5000;
    public static int UploadTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Security: explicitly allow HTTP (non-TLS) API connections. Default
    /// false — only HTTPS is accepted. Set to true in config.json when
    /// HTTPS is unavailable (e.g. GFW SNI blocking forces HTTP fallback
    /// via bare IP). ApiClient checks this flag at init and refuses to
    /// send data over HTTP unless explicitly opted-in.
    /// </summary>
    public static bool AllowHttp { get; set; } = false;
    public static bool AutoUpdate { get; set; } = true;

    // User preferences
    public static bool EnableUpload { get; set; } = true;
    public static string Language { get; set; } = "CN"; // "CN" | "EN"

    // Feature toggles
    public static FeatureToggles Toggles { get; set; } = new();

    // Panel position (null = default right side)
    public static float? PanelPositionX { get; set; }
    public static float? PanelPositionY { get; set; }

    // "My Data" filter
    public static bool UseMyDataOnly { get; set; }

    // History import (PRD §3.19)
    public static bool HistoryImportCompleted { get; set; }

    // Offline queue limits
    public static int MaxPendingCount { get; set; } = 10;
    public static int MaxPendingAgeDays { get; set; } = 7;

    // Cache
    public static int MemoryCacheTtlSeconds { get; set; } = 900;   // 15 min
    public static int DiskCacheTtlHours { get; set; } = 24;

    // Paths
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "sts2_community_stats");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string PendingDir => Path.Combine(DataDir, "pending");
    public static string ContributionsDir => Path.Combine(DataDir, "contributions");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    // Active filter (mutable at runtime)
    public static FilterSettings CurrentFilter { get; set; } = new();

    // Config override file path (next to the mod DLL).
    // Named .cfg so the game engine doesn't scan it as a mod manifest.
    public static string ConfigPath
    {
        get
        {
            var asmLocation = typeof(ModConfig).Assembly.Location;
            if (!string.IsNullOrEmpty(asmLocation))
                return Path.Combine(Path.GetDirectoryName(asmLocation)!, "settings.cfg");
            return Path.Combine(DataDir, "settings.cfg");
        }
    }

    /// <summary>User-modifiable preferences saved to AppData (always writable).</summary>
    public static string PrefsPath => Path.Combine(DataDir, "mod_prefs.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(PendingDir);
        Directory.CreateDirectory(ContributionsDir);
    }

    /// <summary>
    /// Load config overrides from disk. Reads shipped config.json first, then
    /// user's mod_prefs.json from AppData (overrides take priority — survives
    /// mod updates and directory permission issues).
    /// </summary>
    public static void LoadOverrides()
    {
        // Phase 1: shipped defaults (config.json next to the DLL)
        ApplyConfigFile(ConfigPath);
        // Phase 2: user prefs (AppData, always writable — overrides shipped defaults)
        ApplyConfigFile(PrefsPath);
    }

    private static void ApplyConfigFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("api_base_url", out var url))
                ApiBaseUrl = url.GetString() ?? ApiBaseUrl;
            if (root.TryGetProperty("query_timeout_ms", out var qt))
                QueryTimeoutMs = qt.GetInt32();
            if (root.TryGetProperty("upload_timeout_ms", out var ut))
                UploadTimeoutMs = ut.GetInt32();
            if (root.TryGetProperty("allow_http", out var ah))
                AllowHttp = ah.GetBoolean();
            if (root.TryGetProperty("auto_update", out var au))
                AutoUpdate = au.GetBoolean();
            if (root.TryGetProperty("enable_upload", out var eu))
                EnableUpload = eu.GetBoolean();
            if (root.TryGetProperty("language", out var lang))
                Language = lang.GetString() ?? Language;

            if (root.TryGetProperty("feature_toggles", out var toggles))
            {
                try
                {
                    Toggles = JsonSerializer.Deserialize<FeatureToggles>(toggles.GetRawText()) ?? new();
                }
                catch { Toggles = new(); }
            }

            if (root.TryGetProperty("panel_position", out var pos))
            {
                if (pos.TryGetProperty("x", out var px)) PanelPositionX = px.GetSingle();
                if (pos.TryGetProperty("y", out var py)) PanelPositionY = py.GetSingle();
            }

            if (root.TryGetProperty("use_my_data_only", out var myData))
                UseMyDataOnly = myData.GetBoolean();

            if (root.TryGetProperty("history_import_completed", out var hic))
                HistoryImportCompleted = hic.GetBoolean();
        }
        catch { /* ignore malformed config */ }
    }

    /// <summary>
    /// Save current settings (feature toggles, language, preferences) to AppData.
    /// Uses PrefsPath so settings survive mod updates and directory permissions.
    /// </summary>
    public static void SaveSettings()
    {
        try
        {
            var data = new Dictionary<string, object?>
            {
                ["language"] = Language,
                ["feature_toggles"] = Toggles,
                ["auto_update"] = AutoUpdate,
                ["enable_upload"] = EnableUpload,
                ["use_my_data_only"] = UseMyDataOnly,
                ["history_import_completed"] = HistoryImportCompleted,
            };

            if (PanelPositionX.HasValue && PanelPositionY.HasValue)
            {
                data["panel_position"] = new { x = PanelPositionX.Value, y = PanelPositionY.Value };
            }

            EnsureDirectories();
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PrefsPath, json);
        }
        catch { /* ignore write failures */ }
    }
}
