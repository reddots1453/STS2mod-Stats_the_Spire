using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;

namespace CommunityStats.Api;

/// <summary>
/// Auto-update: the mod checks for new versions and notifies the user.
/// The actual download and file replacement is done by update.bat,
/// which the user runs after closing the game.
/// </summary>
public sealed class Updater
{
    public static Updater Instance { get; } = new();

    public string Edition { get; set; } = "local";

    /// <summary>
    /// Background check. If a newer version is found, shows a dialog
    /// telling the user to run update.bat.
    /// </summary>
    public async System.Threading.Tasks.Task CheckForUpdateAsync()
    {
        if (!ModConfig.AutoUpdate)
        {
            Safe.Info("[Updater] Auto-update disabled via config");
            return;
        }

        UpdateInfo? info;
        try
        {
            info = await FetchUpdateInfoAsync();
        }
        catch (Exception ex)
        {
            Safe.Warn($"[Updater] Update check failed: {ex.Message}");
            return;
        }

        if (info == null || !info.UpdateAvailable) return;

        Safe.Info($"[Updater] New version available: {info.Latest} (current: {ModConfig.ModVersion})");

        // Marshal to main thread to show dialog.
        Callable.From(() => ShowUpdateDialog(info.Latest)).CallDeferred();
    }

    // ── Dialog ──────────────────────────────────────────────

    private static void ShowUpdateDialog(string version)
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root == null) return;

            var backdrop = CreateBackdrop(tree);
            var vbox = CreatePanel(backdrop, 440);

            AddTitle(vbox, "Stats the Spire");
            AddSeparator(vbox);

            var msg = string.Format(L.Get("update.found"), version, ModConfig.ModVersion);
            AddMessage(vbox, msg, 400);

            var btnRow = AddButtonRow(vbox);
            var okBtn = MakeButton(L.Get("update.ok"), new Color(0.2f, 0.55f, 0.2f));
            btnRow.AddChild(okBtn);

            okBtn.Pressed += () => backdrop.QueueFree();
            RegisterEscClose(backdrop);
            okBtn.GrabFocus();
        }
        catch (Exception ex)
        {
            Safe.Warn($"[Updater] Failed to show dialog: {ex.Message}");
        }
    }

    // ── UI helpers ──────────────────────────────────────────

    private static ColorRect CreateBackdrop(SceneTree tree)
    {
        var backdrop = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.5f),
            AnchorRight = 1, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        tree.Root.AddChild(backdrop);
        return backdrop;
    }

    private static VBoxContainer CreatePanel(Node parent, float minWidth)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", StyleBox(new Color(0.08f, 0.08f, 0.1f, 0.95f)));
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.CustomMinimumSize = new Vector2(minWidth, 0);
        parent.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        panel.AddChild(vbox);
        return vbox;
    }

    private static void AddTitle(VBoxContainer vbox, string text)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        lbl.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.2f));
        lbl.AddThemeFontSizeOverride("font_size", 18);
        vbox.AddChild(lbl);
    }

    private static void AddSeparator(VBoxContainer vbox) => vbox.AddChild(new HSeparator());

    private static void AddMessage(VBoxContainer vbox, string text, float minWidth)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Word,
            CustomMinimumSize = new Vector2(minWidth, 0),
        };
        lbl.AddThemeColorOverride("font_color", new Color(0.9f, 0.88f, 0.82f));
        lbl.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(lbl);
    }

    private static HBoxContainer AddButtonRow(VBoxContainer vbox)
    {
        var row = new HBoxContainer();
        row.Alignment = BoxContainer.AlignmentMode.Center;
        row.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(row);
        return row;
    }

    private static void RegisterEscClose(ColorRect backdrop)
    {
        backdrop.GuiInput += e =>
        {
            if (e is InputEventKey key && key.Keycode == Key.Escape && key.Pressed)
                backdrop.QueueFree();
        };
    }

    private static StyleBoxFlat StyleBox(Color bg) => new()
    {
        BgColor = bg,
        CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        BorderWidthLeft = 1, BorderWidthRight = 1,
        BorderWidthTop = 1, BorderWidthBottom = 1,
        BorderColor = new Color(0.3f, 0.3f, 0.35f, 0.6f),
    };

    private static Button MakeButton(string text, Color bgColor)
    {
        var btn = new Button { Text = text, CustomMinimumSize = new Vector2(100, 36) };
        btn.AddThemeStyleboxOverride("normal", StyleBox(bgColor));
        btn.AddThemeStyleboxOverride("hover", StyleBox(new Color(
            bgColor.R + 0.1f, bgColor.G + 0.1f, bgColor.B + 0.1f)));
        btn.AddThemeFontSizeOverride("font_size", 14);
        return btn;
    }

    // ── Network ─────────────────────────────────────────────

    private static async Task<UpdateInfo?> FetchUpdateInfoAsync()
    {
        var url = $"meta/update-info?edition={Uri.EscapeDataString(Instance.Edition)}"
                + $"&current={Uri.EscapeDataString(ModConfig.ModVersion)}";

        using var client = new System.Net.Http.HttpClient
        {
            BaseAddress = new System.Uri(ModConfig.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = System.TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.Add("X-Mod-Version", ModConfig.ModVersion);

        var json = await client.GetStringAsync(url);
        return JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }

    private class UpdateInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("latest")]
        public string Latest { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("update_available")]
        public bool UpdateAvailable { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = "";
    }
}
