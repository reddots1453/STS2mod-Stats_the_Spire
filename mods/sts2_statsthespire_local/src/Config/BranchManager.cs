using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Platform;

namespace CommunityStats.Config;

/// <summary>
/// Detects whether the player is on the Steam "public" (release) branch
/// or a beta branch, and maps that to a short tag for upload and filtering.
/// </summary>
public static class BranchManager
{
    public const string Release = "release";
    public const string Beta = "beta";
    public const string Unknown = "unknown";
    public const string All = "all";

    /// <summary>
    /// The player's current branch. Uses <see cref="ReleaseInfo.Branch"/>
    /// from release_info.json first (authoritative); falls back to
    /// <see cref="PlatformUtil.GetPlatformBranch"/> Steam API.
    /// "public" maps to "release"; anything else maps to "beta".
    /// </summary>
    private static string? _cachedBranch;

    public static string CurrentBranch
    {
        get
        {
            if (_cachedBranch != null) return _cachedBranch;

            try
            {
                // Path 1: release_info.json (authoritative).
                var releaseBranch = ReleaseInfoManager.Instance?.ReleaseInfo?.Branch;
                if (!string.IsNullOrEmpty(releaseBranch))
                {
                    _cachedBranch = releaseBranch == "public" ? Release : Beta;
                    return _cachedBranch;
                }
            }
            catch { }

            try
            {
                // Path 2: Steam API (PlatformBranch enum).
                var platformBranch = PlatformUtil.GetPlatformBranch();
                _cachedBranch = platformBranch.ToString() == "Public" ? Release : Beta;
                return _cachedBranch;
            }
            catch
            {
                _cachedBranch = Release;
                return _cachedBranch;
            }
        }
    }

    /// <summary>
    /// Resolves the effective branch for API queries.
    /// null/"auto" → user's current branch; "all" → all; specific → that branch.
    /// </summary>
    public static string GetEffectiveBranch(FilterSettings filter)
    {
        if (filter.Branch == "all") return All;
        if (filter.Branch == "release" || filter.Branch == "beta") return filter.Branch;
        return CurrentBranch; // null → auto
    }
}
