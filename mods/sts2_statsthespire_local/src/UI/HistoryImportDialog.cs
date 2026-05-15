using CommunityStats.Config;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// PRD §3.19: First-launch modal dialog asking the user whether to upload
/// their local run history to the community stats server.
/// Follows UI_STYLE_GUIDE.md §3.2 (secondary panel style).
/// </summary>
public sealed class HistoryImportDialog : PanelContainer
{
    private static readonly Color BgDark = new(0.08f, 0.08f, 0.12f, 0.95f);
    private static readonly Color Border = new(0.4f, 0.4f, 0.6f, 0.5f);
    private static readonly Color Gold = new("#EFC851");
    private static readonly Color Cream = new("#FFF6E2");
    private static readonly Color BtnBg = new(0.15f, 0.2f, 0.35f, 0.9f);
    private static readonly Color BtnBorder = new(0.4f, 0.5f, 0.8f, 0.6f);
    private static readonly Color SkipBg = new(0.12f, 0.12f, 0.18f, 0.9f);
    private static readonly Color SkipBorder = new(0.3f, 0.3f, 0.5f, 0.5f);

    private readonly TaskCompletionSource<bool> _result = new();

    /// <summary>Awaitable: true = user chose upload, false = skip.</summary>
    public Task<bool> ResultTask => _result.Task;

    private HistoryImportDialog() { }

    public static HistoryImportDialog Create()
    {
        var dialog = new HistoryImportDialog();
        dialog.Name = "HistoryImportDialog";

        // Panel style: secondary panel per UI_STYLE_GUIDE §3.2
        var style = new StyleBoxFlat
        {
            BgColor = BgDark,
            BorderColor = Border,
            BorderWidthBottom = 2, BorderWidthTop = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            ContentMarginLeft = 24, ContentMarginRight = 24,
            ContentMarginTop = 20, ContentMarginBottom = 20,
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = 8,
        };
        dialog.AddThemeStyleboxOverride("panel", style);

        // Size & centering
        dialog.CustomMinimumSize = new Vector2(440, 260);
        dialog.AnchorLeft = 0.5f;
        dialog.AnchorRight = 0.5f;
        dialog.AnchorTop = 0.35f;
        dialog.OffsetLeft = -220;
        dialog.OffsetRight = 220;
        dialog.MouseFilter = MouseFilterEnum.Stop;

        // Layout
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        dialog.AddChild(vbox);

        // Title
        var title = new Label { Text = L.Get("import.dialog_title") };
        title.AddThemeColorOverride("font_color", Gold);
        title.AddThemeFontSizeOverride("font_size", 16);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        // Description
        var desc = new Label
        {
            Text = L.Get("import.dialog_desc"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        desc.AddThemeColorOverride("font_color", Cream);
        desc.AddThemeFontSizeOverride("font_size", 12);
        desc.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(desc);

        // Spacer
        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        // Buttons row
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 16);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(btnRow);

        var confirmBtn = MakeButton(L.Get("import.confirm"), BtnBg, BtnBorder, Gold);
        confirmBtn.Pressed += () => dialog.OnChoice(true);
        btnRow.AddChild(confirmBtn);

        var skipBtn = MakeButton(L.Get("import.skip"), SkipBg, SkipBorder, Cream);
        skipBtn.Pressed += () => dialog.OnChoice(false);
        btnRow.AddChild(skipBtn);

        return dialog;
    }

    private void OnChoice(bool upload)
    {
        _result.TrySetResult(upload);
        // Fade-out then free
        var tween = CreateTween();
        tween.TweenProperty(this, "modulate", new Color(1, 1, 1, 0), 0.3f);
        tween.TweenCallback(Callable.From(() => QueueFree()));
    }

    private static Button MakeButton(string text, Color bg, Color border, Color textColor)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(130, 40),
            MouseFilter = MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", textColor);

        var btnStyle = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        btn.AddThemeStyleboxOverride("normal", btnStyle);

        // Hover: slightly brighter
        var hoverStyle = (StyleBoxFlat)btnStyle.Duplicate();
        hoverStyle.BgColor = bg with { R = bg.R + 0.05f, G = bg.G + 0.05f, B = bg.B + 0.05f };
        btn.AddThemeStyleboxOverride("hover", hoverStyle);

        // Pressed: slightly darker
        var pressStyle = (StyleBoxFlat)btnStyle.Duplicate();
        pressStyle.BgColor = bg with { R = bg.R - 0.03f, G = bg.G - 0.03f, B = bg.B - 0.03f };
        btn.AddThemeStyleboxOverride("pressed", pressStyle);

        return btn;
    }
}
