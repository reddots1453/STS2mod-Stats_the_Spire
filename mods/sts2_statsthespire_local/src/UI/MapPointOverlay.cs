using Godot;

namespace CommunityStats.UI;

/// <summary>
/// Map encounter danger overlay — removed in local edition.
/// DetachFrom kept as no-op for ABI compatibility.
/// </summary>
public static class MapPointOverlay
{
    private const string OverlayMeta = "cs_overlay";

    public static void AttachTo(Control mapPointNode) { }

    public static void DetachFrom(Control mapPointNode)
    {
        if (!mapPointNode.HasMeta(OverlayMeta)) return;
        foreach (var child in mapPointNode.GetChildren())
        {
            if (child is StatsLabel)
            {
                child.QueueFree();
                break;
            }
        }
        mapPointNode.RemoveMeta(OverlayMeta);
    }
}
