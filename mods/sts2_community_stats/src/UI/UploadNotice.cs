using CommunityStats.Util;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// Top-right transient notice shown after a real-time run upload
/// (success or offline-queued). Reuses the same corner styling as
/// ImportProgressLabel so the two notifications look consistent.
///
/// Thread-safe: Show() may be called from any thread; the label is
/// lazily attached to the scene root via CallDeferred on first use,
/// and text/visibility are set via SetDeferred.
/// </summary>
public static class UploadNotice
{
    private static ImportProgressLabel? _label;
    private static readonly object _lock = new();

    /// <summary>
    /// Show a transient top-right notice. Auto-hides after ~3s.
    /// Safe to call from background threads.
    /// </summary>
    public static void Show(string text)
    {
        // Suppress while history import is streaming progress to the same
        // top-right slot — otherwise the two labels stomp each other.
        if (Api.HistoryImporter.IsRunning) return;

        Safe.Run(() =>
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root == null) return;

            ImportProgressLabel? label;
            lock (_lock)
            {
                if (_label == null || !GodotObject.IsInstanceValid(_label))
                {
                    _label = ImportProgressLabel.Create();
                    tree.Root.CallDeferred(Node.MethodName.AddChild, _label);
                }
                label = _label;
            }

            label.SetDeferred("text", text);
            label.SetDeferred("visible", true);
            // Custom C# methods aren't in Godot's script method table,
            // so CallDeferred(name) won't resolve them. Marshal via
            // Callable.From so the arm fires on the main thread.
            var armLabel = label;
            Callable.From(() => Safe.Run(armLabel.ArmHideTimer)).CallDeferred();
        });
    }
}
