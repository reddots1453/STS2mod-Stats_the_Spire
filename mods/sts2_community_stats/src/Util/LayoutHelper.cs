using System.Text;
using Godot;

namespace CommunityStats.Util;

/// <summary>
/// Runtime helpers for finding the right place to inject mod content into
/// native Godot scenes whose .tscn structure we cannot inspect directly.
///
/// PRD §3.11 / §3.12 round 7: native screens (`NGeneralStatsGrid`,
/// `NRunHistory`) hold most of their visible widgets as absolutely-positioned
/// children of a plain `Control`, so naive `AddChild` lands the new section
/// at (0,0) and overlaps the native widgets. The fix is to walk up the
/// parent chain looking for the first auto-layout container
/// (`VBoxContainer`, `HBoxContainer`, `GridContainer`, `ScrollContainer` /
/// its inner viewport) and add the new section as one of its children —
/// Godot then routes layout for us.
/// </summary>
public static class LayoutHelper
{
    /// <summary>
    /// Walk upwards from <paramref name="start"/> looking for the first
    /// ancestor that is an auto-layout container we can safely append to.
    /// Returns null when no suitable ancestor exists.
    /// </summary>
    public static Control? FindLayoutAncestor(Node? start)
    {
        var n = start;
        while (n != null)
        {
            if (IsLayoutContainer(n))
                return (Control)n;
            n = n.GetParent();
        }
        return null;
    }

    public static bool IsLayoutContainer(Node n) =>
        n is VBoxContainer ||
        n is HBoxContainer ||
        n is GridContainer ||
        n is FlowContainer ||
        n is ScrollContainer;

    /// <summary>
    /// Append <paramref name="section"/> to the layout-aware ancestor of
    /// <paramref name="anchor"/>. Returns the parent the section was added
    /// to (null if none could be found and no fallback was performed).
    /// </summary>
    public static Control? AppendToLayoutAncestor(Node anchor, Control section, bool moveBeforeAnchor = false)
    {
        var parent = FindLayoutAncestor(anchor);
        if (parent == null) return null;

        // ScrollContainer wraps a single child; if we add a sibling Godot
        // ignores us. Walk into the inner child instead.
        if (parent is ScrollContainer scroll)
        {
            for (int i = 0; i < scroll.GetChildCount(); i++)
            {
                if (scroll.GetChild(i) is Control inner && IsLayoutContainer(inner))
                {
                    parent = inner;
                    break;
                }
            }
        }

        parent.AddChild(section);

        if (moveBeforeAnchor)
        {
            // Walk anchor's chain to find the direct child of `parent`,
            // then move section right before that.
            Node? walker = anchor;
            while (walker != null && walker.GetParent() != parent)
                walker = walker.GetParent();
            if (walker != null)
                parent.MoveChild(section, walker.GetIndex());
        }

        return parent;
    }

    /// <summary>
    /// Diagnostic: walk up the parent chain and return a one-line summary
    /// listing each ancestor's class name + size. Helpful when injection
    /// goes wrong and we need the user to give us scene structure.
    /// </summary>
    public static string DescribeAncestry(Node? start, int maxDepth = 8)
    {
        var sb = new StringBuilder();
        var n = start;
        int depth = 0;
        while (n != null && depth < maxDepth)
        {
            sb.Append(new string(' ', depth * 2));
            sb.Append(n.GetType().Name);
            sb.Append(" \"").Append(n.Name).Append('"');
            if (n is Control c)
                sb.Append(' ').Append(c.Size.X.ToString("F0")).Append('x').Append(c.Size.Y.ToString("F0"));
            sb.Append('\n');
            n = n.GetParent();
            depth++;
        }
        return sb.ToString();
    }
}
