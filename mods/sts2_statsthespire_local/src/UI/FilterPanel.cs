using CommunityStats.Config;
using CommunityStats.Util;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// F7 settings panel — language selector + feature toggles.
/// No community data filters. Changes auto-apply on close.
/// </summary>
public partial class FilterPanel : PanelContainer
{
    private static FilterPanel? _instance;

    private OptionButton? _langDropdown;
    private readonly Dictionary<string, CheckBox> _toggleCheckboxes = new();

    private const int TitleSize   = 28;
    private const int SectionSize = 22;
    private const int LabelSize   = 20;
    private static readonly Color Gold   = new("#EFC851");
    private static readonly Color Cream  = new("#FFF6E2");
    private static readonly Color Gray   = new(0.6f, 0.6f, 0.65f);
    private static readonly Color Border = new(0.55f, 0.70f, 0.95f, 0.75f);
    private static readonly Color BgDark = new(0.13f, 0.16f, 0.23f, 0.96f);

    private static L.Lang _builtLanguage;

    public static FilterPanel Instance => _instance ??= CreatePanel();

    public static void RebuildForLanguage()
    {
        if (_instance == null || !GodotObject.IsInstanceValid(_instance)) return;
        var wasVisible = _instance.Visible;
        var parent = _instance.GetParent();
        _instance.QueueFree();
        _instance = CreatePanel();
        _builtLanguage = L.Current;
        if (parent != null) parent.AddChild(_instance);
        if (wasVisible) _instance.Visible = true;
    }

    private static FilterPanel CreatePanel()
    {
        _builtLanguage = L.Current;
        var panel = new FilterPanel();
        panel.Name = "CommunityStatsFilter";
        panel.Visible = false;

        var style = new StyleBoxFlat
        {
            BgColor = BgDark,
            BorderColor = Border,
            BorderWidthBottom = 2, BorderWidthTop = 2,
            BorderWidthLeft  = 2, BorderWidthRight  = 2,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            CornerRadiusTopLeft    = 10, CornerRadiusTopRight    = 10,
            ContentMarginLeft  = 20, ContentMarginRight  = 20,
            ContentMarginTop   = 16, ContentMarginBottom = 16,
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize  = 6,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        // Smaller panel — only language + toggles
        panel.CustomMinimumSize = new Vector2(420, 500);
        panel.AnchorLeft   = 0.5f;
        panel.AnchorRight  = 0.5f;
        panel.AnchorTop    = 0.08f;
        panel.OffsetLeft   = -210;
        panel.OffsetRight  = 210;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.SizeFlagsVertical   = SizeFlags.ExpandFill;
        panel.AddChild(vbox);

        // ── Title bar ───────────────────────────────────────
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(header);

        var title = MakeLabel("Stats the Spire", Gold, TitleSize);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);

        var closeBtn = new Button
        {
            Text = "✕",  // ✕
            CustomMinimumSize = new Vector2(40, 40),
            MouseFilter = MouseFilterEnum.Stop,
        };
        closeBtn.AddThemeFontSizeOverride("font_size", 22);
        closeBtn.Pressed += () => panel.ApplyAndClose();
        header.AddChild(closeBtn);

        vbox.AddChild(NewSeparator());

        // Scroll area
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
        };
        vbox.AddChild(scroll);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 14);
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(body);

        // ── Language ────────────────────────────────────────
        body.AddChild(MakeSectionHeader(L.Get("settings.language")));
        var langGrid = NewRowGrid();
        body.AddChild(langGrid);

        panel._langDropdown = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel._langDropdown.AddItem("中文", 0);     // 中文
        panel._langDropdown.AddItem("English", 1);
        panel._langDropdown.Selected = L.Current == L.Lang.EN ? 1 : 0;
        AddLabeledControl(langGrid, L.Get("settings.language"), panel._langDropdown);

        body.AddChild(NewSeparator());

        // ── Feature Toggles ─────────────────────────────────
        body.AddChild(MakeSectionHeader(L.Get("settings.toggles_title")));
        var togglesGrid = NewRowGrid();
        body.AddChild(togglesGrid);

        foreach (var (key, labelKey) in FeatureToggles.ToggleDefinitions)
        {
            var cb = NewCheckbox(L.Get(labelKey), ModConfig.Toggles.GetByName(key));
            panel._toggleCheckboxes[key] = cb;
            AddToggleRow(togglesGrid, L.Get(labelKey), cb);
        }

        return panel;
    }

    // ── Layout helpers ──────────────────────────────────────

    private static Label MakeLabel(string text, Color color, int size)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static Label MakeSectionHeader(string text)
        => MakeLabel(text, Gold, SectionSize);

    private static HSeparator NewSeparator()
    {
        var s = new HSeparator();
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return s;
    }

    private static GridContainer NewRowGrid()
    {
        var g = new GridContainer { Columns = 2 };
        g.AddThemeConstantOverride("h_separation", 18);
        g.AddThemeConstantOverride("v_separation", 10);
        g.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return g;
    }

    private static void AddLabeledControl(GridContainer grid, string labelText, Control control)
    {
        var lbl = MakeLabel(labelText, Cream, LabelSize);
        lbl.HorizontalAlignment   = HorizontalAlignment.Right;
        lbl.SizeFlagsHorizontal   = SizeFlags.ExpandFill;
        lbl.SizeFlagsVertical     = SizeFlags.ShrinkCenter;
        lbl.CustomMinimumSize     = new Vector2(160, 0);
        grid.AddChild(lbl);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        control.SizeFlagsVertical   = SizeFlags.ShrinkCenter;
        grid.AddChild(control);
    }

    private static void AddToggleRow(GridContainer grid, string labelText, CheckBox cb)
    {
        var lbl = MakeLabel(labelText, Cream, LabelSize);
        lbl.HorizontalAlignment = HorizontalAlignment.Right;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.SizeFlagsVertical   = SizeFlags.ShrinkCenter;
        lbl.CustomMinimumSize   = new Vector2(160, 0);
        grid.AddChild(lbl);

        cb.Text = "";
        cb.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        cb.SizeFlagsVertical   = SizeFlags.ShrinkCenter;
        grid.AddChild(cb);
    }

    private static CheckBox NewCheckbox(string text, bool initial)
    {
        var cb = new CheckBox { Text = text, ButtonPressed = initial };
        cb.AddThemeFontSizeOverride("font_size", LabelSize);
        return cb;
    }

    // ── Show / hide lifecycle ───────────────────────────────

    public static void Toggle()
    {
        if (_builtLanguage != L.Current)
            RebuildForLanguage();

        var panel = Instance;
        if (panel.Visible)
        {
            panel.ApplyAndClose();
        }
        else
        {
            panel.Visible = true;
        }
    }

    private void ApplyAndClose()
    {
        Safe.Run(() =>
        {
            // Language
            var langIdx = _langDropdown?.Selected ?? 0;
            var newLang = langIdx == 1 ? L.Lang.EN : L.Lang.CN;
            if (newLang != L.Current)
            {
                L.Current = newLang;
                ModConfig.Language = langIdx == 1 ? "EN" : "CN";
            }

            // Feature toggles
            foreach (var (key, cb) in _toggleCheckboxes)
                ModConfig.Toggles.SetByName(key, cb.ButtonPressed);

            ModConfig.SaveSettings();

            Visible = false;
        });
    }
}
