using CommunityStats.Config;
using CommunityStats.Util;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// Mixin behavior for making a PanelContainer draggable via mouse.
/// Position is persisted to ModConfig on drag end.
///
/// Usage: call DraggablePanel.Attach(panel, titleBar) to enable dragging
/// when the title bar area is pressed and moved.
/// </summary>
public static class DraggablePanel
{
    private static bool _isDragging;
    private static Vector2 _dragOffset;
    private static Control? _dragTarget;

    /// <summary>
    /// Make a panel draggable by attaching input handlers to a drag handle area.
    /// The dragHandle is typically the title bar HBoxContainer.
    /// </summary>
    public static void Attach(Control panel, Control dragHandle)
    {
        dragHandle.GuiInput += (InputEvent @event) =>
        {
            Safe.Run(() => HandleInput(panel, @event));
        };
    }

    private static void HandleInput(Control panel, InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isDragging = true;
                    _dragTarget = panel;
                    _dragOffset = panel.GlobalPosition - mb.GlobalPosition;
                }
                else
                {
                    if (_isDragging && _dragTarget == panel)
                    {
                        _isDragging = false;
                        _dragTarget = null;
                        // Persist position
                        ModConfig.PanelPositionX = panel.GlobalPosition.X;
                        ModConfig.PanelPositionY = panel.GlobalPosition.Y;
                        ModConfig.SaveSettings();
                    }
                }
            }
        }
        else if (@event is InputEventMouseMotion mm && _isDragging && _dragTarget == panel)
        {
            var newPos = mm.GlobalPosition + _dragOffset;

            // Clamp to viewport
            var viewportSize = panel.GetViewportRect().Size;
            newPos = new Vector2(
                Mathf.Clamp(newPos.X, 0, viewportSize.X - panel.Size.X),
                Mathf.Clamp(newPos.Y, 0, viewportSize.Y - panel.Size.Y)
            );

            panel.GlobalPosition = newPos;
        }
    }

    /// <summary>
    /// Restore panel position from saved config, or use default.
    /// </summary>
    public static void RestorePosition(Control panel, Vector2 defaultPosition)
    {
        if (ModConfig.PanelPositionX.HasValue && ModConfig.PanelPositionY.HasValue)
        {
            panel.GlobalPosition = new Vector2(
                ModConfig.PanelPositionX.Value,
                ModConfig.PanelPositionY.Value);
        }
        else
        {
            panel.GlobalPosition = defaultPosition;
        }
    }
}
