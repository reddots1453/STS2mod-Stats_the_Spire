using CommunityStats.Config;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// PRD §3.19: Small corner label showing "上传数据中... (N/M)" during
/// history import. Positioned below the top bar on the right side.
/// Text and Visible are set via SetDeferred from background threads.
/// Auto-hides 3 seconds after import completes using _Process timer.
/// </summary>
public sealed class ImportProgressLabel : Label
{
    private static readonly Color Cream = new("#FFF6E2");

    // Re-arm token: each ArmHideTimer call increments this; pending
    // SceneTreeTimer callbacks check the token before hiding so a
    // newer Show() resets the visible window instead of being killed
    // by an older timer firing.
    private int _armToken = 0;

    private ImportProgressLabel() { }

    public static ImportProgressLabel Create()
    {
        var label = new ImportProgressLabel();
        label.Name = "ImportProgressLabel";

        label.AddThemeColorOverride("font_color", Cream);
        label.AddThemeFontSizeOverride("font_size", 12);
        label.HorizontalAlignment = HorizontalAlignment.Right;

        // Position: top-right, below the top bar (~60px from top)
        label.AnchorLeft = 1.0f;
        label.AnchorRight = 1.0f;
        label.AnchorTop = 0.0f;
        label.OffsetLeft = -300;
        label.OffsetRight = -16;
        label.OffsetTop = 60;
        label.OffsetBottom = 80;

        label.Visible = false;
        return label;
    }

    /// <summary>
    /// Arm (or re-arm) the 3-second auto-hide countdown. Must be called
    /// on the main thread after the label is in the SceneTree.
    /// </summary>
    public void ArmHideTimer()
    {
        var tree = GetTree();
        if (tree == null) return;
        int token = ++_armToken;
        var timer = tree.CreateTimer(3.0);
        timer.Timeout += () =>
        {
            if (token != _armToken) return;
            if (!GodotObject.IsInstanceValid(this)) return;
            Visible = false;
        };
    }
}
