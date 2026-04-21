using System.Text.Json;

namespace CommunityStats.Config;

/// <summary>
/// Central configuration for the Community Stats mod.
/// </summary>
public static class ModConfig
{
    public const string ModVersion = "2.0.0";

    // Server (can be overridden via config.json for local testing)
    public static string ApiBaseUrl { get; set; } = "https://statsthespire.duckdns.org/v1";
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

    // Config override file path (next to the mod DLL)
    public static string ConfigPath
    {
        get
        {
            var asmLocation = typeof(ModConfig).Assembly.Location;
            if (!string.IsNullOrEmpty(asmLocation))
                return Path.Combine(Path.GetDirectoryName(asmLocation)!, "config.json");
            return Path.Combine(DataDir, "config.json");
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(PendingDir);
        Directory.CreateDirectory(ContributionsDir);
    }

    /// <summary>
    /// Load config.json overrides (e.g. api_base_url for local testing).
    /// Call this before any API client initialization.
    /// </summary>
    public static void LoadOverrides()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
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
            if (root.TryGetProperty("enable_upload", out var eu))
                EnableUpload = eu.GetBoolean();
            if (root.TryGetProperty("language", out var lang))
                Language = lang.GetString() ?? Language;

            // Feature toggles
            if (root.TryGetProperty("feature_toggles", out var toggles))
            {
                try
                {
                    Toggles = JsonSerializer.Deserialize<FeatureToggles>(toggles.GetRawText()) ?? new();
                }
                catch { Toggles = new(); }
            }

            // Panel position
            if (root.TryGetProperty("panel_position", out var pos))
            {
                if (pos.TryGetProperty("x", out var px)) PanelPositionX = px.GetSingle();
                if (pos.TryGetProperty("y", out var py)) PanelPositionY = py.GetSingle();
            }

            // My data only filter
            if (root.TryGetProperty("use_my_data_only", out var myData))
                UseMyDataOnly = myData.GetBoolean();

            // History import flag (PRD §3.19)
            if (root.TryGetProperty("history_import_completed", out var hic))
                HistoryImportCompleted = hic.GetBoolean();
        }
        catch { /* ignore malformed config */ }
    }

    /// <summary>
    /// Save current settings (feature toggles, panel position, preferences) to config.json.
    /// </summary>
    public static void SaveSettings()
    {
        try
        {
            var data = new Dictionary<string, object?>
            {
                ["api_base_url"] = ApiBaseUrl,
                ["allow_http"] = AllowHttp,
                ["query_timeout_ms"] = QueryTimeoutMs,
                ["upload_timeout_ms"] = UploadTimeoutMs,
                ["enable_upload"] = EnableUpload,
                ["language"] = Language,
                ["feature_toggles"] = Toggles,
                ["use_my_data_only"] = UseMyDataOnly,
                ["history_import_completed"] = HistoryImportCompleted,
            };

            if (PanelPositionX.HasValue && PanelPositionY.HasValue)
            {
                data["panel_position"] = new { x = PanelPositionX.Value, y = PanelPositionY.Value };
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* ignore write failures */ }
    }
}
