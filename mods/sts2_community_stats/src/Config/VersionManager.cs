using MegaCrit.Sts2.Core.Debug;

namespace CommunityStats.Config;

public static class VersionManager
{
    public static string GameVersion =>
        ReleaseInfoManager.Instance?.ReleaseInfo?.Version ?? "unknown";

    public static string GameCommit =>
        ReleaseInfoManager.Instance?.ReleaseInfo?.Commit ?? "unknown";

    /// <summary>
    /// Resolves the effective game version for API queries.
    /// If filter specifies a version, use that; otherwise use current game version.
    /// </summary>
    public static string GetEffectiveVersion(FilterSettings filter)
    {
        if (filter.GameVersion is not null and not "all")
            return filter.GameVersion;
        if (filter.GameVersion == "all")
            return "all";
        return GameVersion;
    }
}
