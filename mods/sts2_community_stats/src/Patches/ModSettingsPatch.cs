using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace CommunityStats.Patches;

/// <summary>
/// Patches NModdingScreen to add a "Community Stats Settings" button
/// that opens our FilterPanel from the mod settings screen.
/// </summary>
[HarmonyPatch]
public static class ModSettingsPatch
{
    private const string ButtonName = "CommunityStatsSettingsBtn";

    [HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen._Ready))]
    [HarmonyPostfix]
    public static void AfterModdingScreenReady(NModdingScreen __instance)
    {
        Safe.Run(() =>
        {
            // Avoid duplicate buttons on re-entry
            var existing = __instance.FindChild(ButtonName, recursive: true, owned: false);
            if (existing != null) return;

            var btn = new Button
            {
                Name = ButtonName,
                Text = L.Get("mod.settings_btn"),
                CustomMinimumSize = new Vector2(200, 36)
            };
            btn.AddThemeFontSizeOverride("font_size", 14);
            btn.Pressed += () =>
            {
                FilterPanel.Instance.Visible = !FilterPanel.Instance.Visible;
                FilterPanel.Instance.UpdateSampleSizeLabel();
            };

            // Add the button to the screen
            __instance.AddChild(btn);
            // Position at top-right area
            btn.AnchorLeft = 1.0f;
            btn.AnchorRight = 1.0f;
            btn.AnchorTop = 0.0f;
            btn.OffsetLeft = -220;
            btn.OffsetRight = -10;
            btn.OffsetTop = 10;

            Safe.Info("Added Community Stats Settings button to mod screen");
        });
    }
}
