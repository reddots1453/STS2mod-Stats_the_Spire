using CommunityStats.Api;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace CommunityStats.UI;

/// <summary>
/// Round 9 round 52: rewrote in the style of the native 游戏设置 screen —
/// large gold title, 2-column grid layout (label | control) with HSeparators,
/// SectionPanel dark background, character dropdown with icons + Chinese
/// names, and NO "应用" button (changes auto-apply when the panel is hidden).
/// </summary>
public partial class FilterPanel : PanelContainer
{
    private static FilterPanel? _instance;

    private SpinBox? _minAscSpinBox;
    private SpinBox? _maxAscSpinBox;
    private CheckBox? _autoMatchAscCheckbox;
    private OptionButton? _versionDropdown;
    private SpinBox? _minWinRateSpinBox;
    private Label? _sampleSizeLabel;
    private CheckBox? _uploadCheckbox;
    private CheckBox? _myDataCheckbox;
    private OptionButton? _langDropdown;
    private OptionButton? _characterDropdown;
    private readonly Dictionary<string, CheckBox> _toggleCheckboxes = new();

    // Style constants matching CareerStatsSection / native settings tab.
    private const int TitleSize    = 28;
    private const int SectionSize  = 22;
    private const int LabelSize    = 20;
    private static readonly Color Gold   = new("#EFC851");
    private static readonly Color Cream  = new("#FFF6E2");
    private static readonly Color Gray   = new(0.6f, 0.6f, 0.65f);
    private static readonly Color Border = new(0.55f, 0.70f, 0.95f, 0.75f);
    private static readonly Color BgDark = new(0.13f, 0.16f, 0.23f, 0.96f);

    // PRD §3.18 — order matches the dropdown items: auto, all, then 5 characters.
    private static readonly string[] _characterModes = new[]
    {
        "auto", "all", "IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT",
    };

    public static event Action? FilterApplied;

    public static FilterPanel Instance => _instance ??= CreatePanel();

    private static FilterPanel CreatePanel()
    {
        var panel = new FilterPanel();
        panel.Name = "CommunityStatsFilter";
        panel.Visible = false;

        // Outer panel: dark rounded card, blue border, matching SectionPanel.
        var style = new StyleBoxFlat
        {
            BgColor = BgDark,
            BorderColor = Border,
            BorderWidthBottom = 2, BorderWidthTop = 2,
            BorderWidthLeft = 2, BorderWidthRight = 2,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            ContentMarginLeft = 20, ContentMarginRight = 20,
            ContentMarginTop = 16, ContentMarginBottom = 16,
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = 6,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        panel.CustomMinimumSize = new Vector2(540, 720);
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.08f;
        panel.OffsetLeft = -270;
        panel.OffsetRight = 270;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.SizeFlagsVertical = SizeFlags.ExpandFill;
        panel.AddChild(vbox);

        // ── Title bar ───────────────────────────────────────
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(header);

        var title = MakeLabel(L.Get("settings.title"), Gold, TitleSize);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);

        var closeBtn = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(40, 40),
            MouseFilter = MouseFilterEnum.Stop,
        };
        closeBtn.AddThemeFontSizeOverride("font_size", 22);
        closeBtn.Pressed += () => panel.ApplyAndClose();
        header.AddChild(closeBtn);

        vbox.AddChild(NewSeparator());

        // Scroll area for the rest of the settings (overflow-friendly).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        vbox.AddChild(scroll);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 14);
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(body);

        // ── Section: 基础设置 ────────────────────────────────
        body.AddChild(MakeSectionHeader(L.Get("settings.section_basic")));
        var dataGrid = NewRowGrid();
        body.AddChild(dataGrid);

        // Upload toggle (label + checkbox)
        panel._uploadCheckbox = NewCheckbox(L.Get("settings.upload"), ModConfig.EnableUpload);
        AddToggleRow(dataGrid, L.Get("settings.upload"), panel._uploadCheckbox);

        // My-data toggle
        panel._myDataCheckbox = NewCheckbox(L.Get("settings.my_data"), ModConfig.CurrentFilter.MyDataOnly);
        AddToggleRow(dataGrid, L.Get("settings.my_data"), panel._myDataCheckbox);

        // Character dropdown — Round 9 round 52: icons + Chinese names.
        panel._characterDropdown = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        PopulateCharacterDropdown(panel._characterDropdown);
        var savedMode = ModConfig.CurrentFilter.CharacterFilterMode ?? "auto";
        var savedIdx = Array.IndexOf(_characterModes, savedMode);
        panel._characterDropdown.Selected = savedIdx >= 0 ? savedIdx : 0;
        AddLabeledControl(dataGrid, L.Get("settings.character"), panel._characterDropdown);

        // Version dropdown
        panel._versionDropdown = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel._versionDropdown.AddItem(L.Get("settings.ver_current"), 0);
        panel._versionDropdown.AddItem(L.Get("settings.ver_all"), 1);
        var savedVer = ModConfig.CurrentFilter.GameVersion == "all" ? 1 : 0;
        panel._versionDropdown.Selected = savedVer;
        AddLabeledControl(dataGrid, L.Get("settings.version"), panel._versionDropdown);

        // Language dropdown
        panel._langDropdown = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel._langDropdown.AddItem("中文", 0);
        panel._langDropdown.AddItem("English", 1);
        panel._langDropdown.Selected = L.Current == L.Lang.EN ? 1 : 0;
        AddLabeledControl(dataGrid, L.Get("settings.language"), panel._langDropdown);

        body.AddChild(NewSeparator());

        // ── Section: 进阶 / 胜率 筛选 ─────────────────────────
        body.AddChild(MakeSectionHeader(L.Get("settings.filter_section")));
        var filterGrid = NewRowGrid();
        body.AddChild(filterGrid);

        panel._autoMatchAscCheckbox = NewCheckbox(L.Get("settings.auto_asc"), ModConfig.CurrentFilter.AutoMatchAscension);
        AddToggleRow(filterGrid, L.Get("settings.auto_asc"), panel._autoMatchAscCheckbox);

        panel._minAscSpinBox = NewSpinBox(
            0, 10, Math.Clamp(ModConfig.CurrentFilter.MinAscension ?? 0, 0, 10));
        AddLabeledControl(filterGrid, L.Get("settings.min_asc"), panel._minAscSpinBox);

        panel._maxAscSpinBox = NewSpinBox(
            0, 10, Math.Clamp(ModConfig.CurrentFilter.MaxAscension ?? 10, 0, 10));
        AddLabeledControl(filterGrid, L.Get("settings.max_asc"), panel._maxAscSpinBox);

        // Round 9 round 49: enforce min ≤ max via cross-clamp.
        panel._minAscSpinBox.ValueChanged += v =>
        {
            if (panel._maxAscSpinBox != null && v > panel._maxAscSpinBox.Value)
                panel._maxAscSpinBox.Value = v;
        };
        panel._maxAscSpinBox.ValueChanged += v =>
        {
            if (panel._minAscSpinBox != null && v < panel._minAscSpinBox.Value)
                panel._minAscSpinBox.Value = v;
        };

        panel._minWinRateSpinBox = NewSpinBox(
            0, 100, (int)((ModConfig.CurrentFilter.MinPlayerWinRate ?? 0) * 100));
        panel._minWinRateSpinBox.Suffix = "%";
        AddLabeledControl(filterGrid, L.Get("settings.min_wr"), panel._minWinRateSpinBox);

        // Sample size status line.
        panel._sampleSizeLabel = MakeLabel("", Gray, LabelSize - 2);
        body.AddChild(panel._sampleSizeLabel);
        panel.UpdateSampleSizeLabel();

        body.AddChild(NewSeparator());

        // ── Section: 功能开关 ─────────────────────────────────
        body.AddChild(MakeSectionHeader(L.Get("settings.toggles_title")));
        var togglesGrid = NewRowGrid();
        body.AddChild(togglesGrid);

        foreach (var (key, labelKey) in FeatureToggles.ToggleDefinitions)
        {
            var cb = NewCheckbox(L.Get(labelKey), ModConfig.Toggles.GetByName(key));
            panel._toggleCheckboxes[key] = cb;
            AddToggleRow(togglesGrid, L.Get(labelKey), cb);
        }

        // ── Hint: changes auto-apply on close ────────────────
        var hint = MakeLabel(L.Get("settings.close_to_apply"), Gray, LabelSize - 4);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(hint);

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
    {
        var l = MakeLabel(text, Gold, SectionSize);
        return l;
    }

    private static HSeparator NewSeparator()
    {
        var s = new HSeparator();
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return s;
    }

    /// <summary>
    /// 2-column grid: label (right-aligned, fixed width) | control (ExpandFill).
    /// </summary>
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
        lbl.HorizontalAlignment = HorizontalAlignment.Right;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        lbl.CustomMinimumSize = new Vector2(180, 0);
        grid.AddChild(lbl);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        control.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        grid.AddChild(control);
    }

    private static void AddToggleRow(GridContainer grid, string labelText, CheckBox cb)
    {
        var lbl = MakeLabel(labelText, Cream, LabelSize);
        lbl.HorizontalAlignment = HorizontalAlignment.Right;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        lbl.CustomMinimumSize = new Vector2(180, 0);
        grid.AddChild(lbl);

        // CheckBox.Text is cleared — the left label is the single source of
        // truth. This also makes the checkbox align with SpinBox/OptionButton
        // on the same grid column.
        cb.Text = "";
        cb.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        cb.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        grid.AddChild(cb);
    }

    private static CheckBox NewCheckbox(string text, bool initial)
    {
        var cb = new CheckBox { Text = text, ButtonPressed = initial };
        cb.AddThemeFontSizeOverride("font_size", LabelSize);
        return cb;
    }

    private static SpinBox NewSpinBox(int min, int max, int value)
    {
        var sb = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = 1,
            Value = value,
        };
        sb.AddThemeFontSizeOverride("font_size", LabelSize);
        return sb;
    }

    /// <summary>
    /// Round 9 round 52: populate the character dropdown with icons + Chinese
    /// names via CharacterModel.Title.GetFormattedText(). "auto" has no icon
    /// per user request; "all" uses RandomCharacter's icon.
    ///
    /// Round 14 v5: clears existing items before re-populating so callers
    /// can refresh the dropdown when icons become available later (e.g. the
    /// user opens F9 before any run, when PreloadManager hasn't cached the
    /// character textures yet — subsequent opens after entering a character
    /// select screen will then have the icons available).
    /// </summary>
    private static void PopulateCharacterDropdown(OptionButton dropdown)
    {
        dropdown.Clear();

        // auto — no icon
        dropdown.AddItem(L.Get("settings.char_auto"), 0);

        // all — RandomCharacter icon
        AddCharItem(dropdown, 1,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.RandomCharacter>(),
            L.Get("settings.char_all"));
        AddCharItem(dropdown, 2,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Ironclad>("char.IRONCLAD"));
        AddCharItem(dropdown, 3,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Silent>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Silent>("char.SILENT"));
        AddCharItem(dropdown, 4,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Defect>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Defect>("char.DEFECT"));
        AddCharItem(dropdown, 5,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Necrobinder>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Necrobinder>("char.NECROBINDER"));
        AddCharItem(dropdown, 6,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Regent>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Regent>("char.REGENT"));
    }

    private static void AddCharItem(OptionButton dropdown, int id, Texture2D? icon, string label)
    {
        if (icon != null)
            dropdown.AddIconItem(icon, label, id);
        else
            dropdown.AddItem(label, id);
    }

    /// <summary>
    /// Round 14 v5: robust icon lookup. Tries the game's PreloadManager cache
    /// first (which may be empty if the user opens F9 before any run starts),
    /// then falls back to ResourceLoader.Load to fetch the texture directly
    /// from the resource pack. This guarantees the dropdown shows character
    /// icons consistently regardless of game state.
    /// </summary>
    private static Texture2D? TryGetCharIcon<T>() where T : CharacterModel
    {
        try
        {
            var charModel = ModelDb.Character<T>();
            // Path 1: PreloadManager cache (used by character select / top bar)
            var cached = charModel.IconTexture;
            if (cached != null) return cached;

            // Path 2: direct ResourceLoader from the same path PreloadManager uses.
            // Path constructed identically to CharacterModel.IconTexturePath:
            //   res://images/ui/top_panel/character_icon_<id-lower>.png
            var idLower = charModel.Id.Entry.ToLowerInvariant();
            var path = $"res://images/ui/top_panel/character_icon_{idLower}.png";
            var loaded = Godot.ResourceLoader.Load<Texture2D>(path);
            if (loaded != null) return loaded;
        }
        catch (Exception ex)
        {
            Util.Safe.Warn($"[FilterPanel] TryGetCharIcon<{typeof(T).Name}> failed: {ex.Message}");
        }
        return null;
    }

    private static string TryGetCharTitle<T>(string fallbackKey) where T : CharacterModel
    {
        try
        {
            var t = ModelDb.Character<T>().Title.GetFormattedText();
            if (!string.IsNullOrEmpty(t)) return t!;
        }
        catch { }
        return L.Get(fallbackKey);
    }

    // ── Show / hide lifecycle ───────────────────────────────

    public static void Toggle()
    {
        var panel = Instance;
        if (panel.Visible)
        {
            // Round 9 round 52: apply settings on hide instead of via an
            // explicit "应用" button. User explicitly requested this.
            panel.ApplyAndClose();
        }
        else
        {
            // Round 14 v5: refresh the character dropdown each time the panel
            // is shown. PreloadManager may have cached character textures since
            // the panel was first created (e.g. user entered character select),
            // so a re-populate gives the icons a chance to appear even if they
            // were missing on first build.
            if (panel._characterDropdown != null)
            {
                var savedMode = ModConfig.CurrentFilter.CharacterFilterMode ?? "auto";
                var savedIdx = Array.IndexOf(_characterModes, savedMode);
                PopulateCharacterDropdown(panel._characterDropdown);
                panel._characterDropdown.Selected = savedIdx >= 0 ? savedIdx : 0;
            }

            panel.Visible = true;
            panel.UpdateSampleSizeLabel();
        }
    }

    public void UpdateSampleSizeLabel()
    {
        if (_sampleSizeLabel == null) return;
        var provider = StatsProvider.Instance;
        if (provider.HasBundle)
        {
            _sampleSizeLabel.Text = string.Format(L.Get("settings.sample"),
                provider.TotalRunCount.ToString("N0"));
        }
        else
        {
            _sampleSizeLabel.Text = L.Get("settings.no_data");
        }
    }

    private void ApplyAndClose()
    {
        Safe.Run(() =>
        {
            ModConfig.EnableUpload = _uploadCheckbox?.ButtonPressed ?? true;

            var langIdx = _langDropdown?.Selected ?? 0;
            L.Current = langIdx == 1 ? L.Lang.EN : L.Lang.CN;
            ModConfig.Language = langIdx == 1 ? "EN" : "CN";

            var filter = ModConfig.CurrentFilter;
            filter.AutoMatchAscension = _autoMatchAscCheckbox?.ButtonPressed ?? false;
            if (!filter.AutoMatchAscension)
            {
                filter.MinAscension = (int?)_minAscSpinBox?.Value;
                filter.MaxAscension = (int?)_maxAscSpinBox?.Value;
            }
            var wrPercent = (int?)_minWinRateSpinBox?.Value ?? 0;
            filter.MinPlayerWinRate = wrPercent > 0 ? wrPercent / 100f : null;
            var verIdx = _versionDropdown?.Selected ?? 0;
            filter.GameVersion = verIdx == 1 ? "all" : null;
            filter.MyDataOnly = _myDataCheckbox?.ButtonPressed ?? false;

            var charIdx = _characterDropdown?.Selected ?? 0;
            if (charIdx < 0 || charIdx >= _characterModes.Length) charIdx = 0;
            filter.CharacterFilterMode = _characterModes[charIdx];

            Safe.Info($"[DIAG:FilterPanel] verIdx={verIdx}, GameVersion={filter.GameVersion}, CharMode={filter.CharacterFilterMode}, MinAsc={filter.MinAscension}, MaxAsc={filter.MaxAscension}, AutoAsc={filter.AutoMatchAscension}");

            filter.Save();

            foreach (var (key, cb) in _toggleCheckboxes)
            {
                ModConfig.Toggles.SetByName(key, cb.ButtonPressed);
            }

            Safe.Run(() => ModConfig.SaveSettings());

            Safe.Info("[DIAG:FilterPanel] About to invoke FilterApplied event");
            FilterApplied?.Invoke();
            Safe.Info("[DIAG:FilterPanel] FilterApplied invoked, hiding panel");
            Visible = false;
        });
    }
}
