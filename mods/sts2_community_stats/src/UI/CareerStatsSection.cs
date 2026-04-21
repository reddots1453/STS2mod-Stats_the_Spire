using System.Collections.Generic;
using System.Linq;
using CommunityStats.Collection;
using CommunityStats.Config;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// "Stats the Spire" career stats block injected into the game's
/// 百科大全 → 角色数据 → 统计 page (PRD §3.11).
///
/// Layout sections (top to bottom):
///   - Title row "Stats the Spire" + total runs/wins summary
///   - Win rate trend (10 / 50 / all)
///   - Death cause Top 5 by Act
///   - Path stats per Act (cards & rooms)
///   - Ancient relic pick rates
///   - Boss damage taken summary
///
/// Reads CareerStatsData from RunHistoryAnalyzer (cached or loaded async).
/// Empty placeholder shown while loading; rebuilt when CareerStatsLoaded fires.
/// </summary>
public sealed partial class CareerStatsSection : VBoxContainer
{
    // ── Style ───────────────────────────────────────────────
    private static readonly Color Gold      = new("#EFC851");
    private static readonly Color Cream     = new("#FFF6E2");
    private static readonly Color Gray      = new(0.6f, 0.6f, 0.65f);
    private static readonly Color Green     = new(0.30f, 0.85f, 0.40f);
    private static readonly Color Red       = new(0.90f, 0.30f, 0.30f);
    private static readonly Color SectionBg = new(0.05f, 0.06f, 0.10f, 0.85f);
    private static readonly Color Border    = new(0.3f, 0.4f, 0.6f, 0.45f);

    // Round 9 round 48: +4 across the board per user request.
    private const int TitleSize    = 36;
    private const int SubtitleSize = 28;
    private const int LabelSize    = 26;
    private const int ValueSize    = 26;

    private string? _characterFilter;
    private int _minAscension; // Round 9 round 46: ascension floor for the panel
    private CareerStatsData? _data;

    private CareerStatsSection() { }

    /// <summary>Build a new CareerStatsSection. characterFilter null = all characters.</summary>
    public static CareerStatsSection Create(string? characterFilter)
    {
        var s = new CareerStatsSection
        {
            Name = "ModCareerStatsSection",
            _characterFilter = characterFilter,
        };
        // Round 9 round 45: bigger gap between SectionPanels so each card
        // breathes against its neighbour.
        s.AddThemeConstantOverride("separation", 16);
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        s.MouseFilter = MouseFilterEnum.Pass;

        // Try cached snapshot first
        s._data = RunHistoryAnalyzer.Instance.GetCached(characterFilter, s._minAscension);
        s.Rebuild();

        // Subscribe for async result
        RunHistoryAnalyzer.Instance.CareerStatsLoaded += s.OnCareerStatsLoaded;
        s.TreeExiting += () => RunHistoryAnalyzer.Instance.CareerStatsLoaded -= s.OnCareerStatsLoaded;

        // Kick off async load if cache miss
        if (s._data == null || s._data.IsEmpty)
        {
            var capturedChar = characterFilter;
            var capturedAsc = s._minAscension;
            Util.Safe.RunAsync(async () =>
                await RunHistoryAnalyzer.Instance.LoadAllAsync(capturedChar, minAscension: capturedAsc));
        }

        return s;
    }

    private void OnCareerStatsLoaded(CareerStatsData data)
    {
        // Filter must match (character + ascension)
        if ((data.CharacterFilter ?? "") != (_characterFilter ?? "")) return;
        if (data.MinAscension != _minAscension) return;
        // Round 9 round 49: CallDeferred(string) routes through Godot's
        // reflection, which can't see private C# methods like Rebuild —
        // the call silently failed with "Method not found" and the screen
        // froze on "loading". Use Callable.From(Rebuild) instead so the
        // dispatch goes through the C# delegate path.
        Util.Safe.Run(() =>
        {
            _data = data;
            Godot.Callable.From(Rebuild).CallDeferred();
        });
    }

    // ── Build ───────────────────────────────────────────────

    private void Rebuild()
    {
        // Clear existing children
        for (int i = GetChildCount() - 1; i >= 0; i--)
            GetChild(i).QueueFree();

        AddChild(BuildHeader());

        if (_data == null)
        {
            AddChild(MakeLabel(L.Get("career.loading"), Gray, SubtitleSize));
            return;
        }

        if (_data.IsEmpty)
        {
            AddChild(MakeLabel(L.Get("career.loading"), Gray, SubtitleSize));
            return;
        }

        // Round 9 round 49: merged 数据汇总 + 胜率趋势 into a single
        // 胜率汇总卡片 panel with the min-ascension selector on top.
        AddChild(BuildWinRateSummaryCard());
        AddChild(BuildDeathCauses());
        AddChild(BuildDeckTable());
        AddChild(BuildPathTable());
        AddChild(BuildAncientPickRates());
        AddChild(BuildBossStats());
    }

    private Control BuildWinRateSummaryCard()
    {
        // Round 9 round 52: min-ascension selector moved to BuildHeader so
        // it sits next to the character dropdown. This panel now contains
        // only the NStat summary cards + window cells.
        var panel = SectionPanel(L.Get("career.win_summary_card"));
        var v = (VBoxContainer)panel.GetChild(0);

        // Row 1: NStat summary cards (range / W-L / win rate / streak).
        v.AddChild(BuildSummaryNStatGrid());

        // Row 2: rolling-window cells (10 / 50 / 100 / all).
        // Round 9 round 52: horizontal separation bumped 24 → 72 (3×) so the
        // four window cells aren't bunched up against each other.
        if (_data != null)
        {
            var grid = new HBoxContainer();
            grid.AddThemeConstantOverride("separation", 72);
            v.AddChild(grid);

            AppendWindowCell(grid, 10, _data.WinRateByWindow);
            AppendWindowCell(grid, 50, _data.WinRateByWindow);
            AppendWindowCell(grid, 100, _data.WinRateByWindow);
            AppendWindowCell(grid, int.MaxValue, _data.WinRateByWindow);
        }

        return panel;
    }

    // Native asset paths shared across the summary cards.
    private static string IconClock        => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_clock.tres");
    private static string IconSwords       => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_swords.tres");
    private static string IconChain        => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_chain.tres");
    private static string IconAchievements => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_achievements.tres");
    private static string IconCards    => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_cards.tres");
    private static string IconMonsters => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_monsters.tres");
    private static string IconAncients => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("atlases/stats_screen_atlas.sprites/stats_ancients.tres");

    /// <summary>
    /// Round 9 round 46: top summary now has only 3 cards in a single row,
    /// all driven by the current ascension filter:
    ///   1. 数据范围: 64 局     (run count for current filter)
    ///   2. 进阶N 胜利/失败     (W/L count at ascension >= N)
    ///   3. 进阶N 胜率           (win rate at ascension >= N)
    /// The bottom rows of avg cards-gained / removed / upgraded were dropped
    /// per user feedback — they belong in the per-Act path stats panel.
    /// </summary>
    private Control BuildSummaryNStatGrid()
    {
        // Round 9 round 49: 4 cards on a single row pushed the panel beyond
        // the screen width — wrap into a 2×2 grid instead.
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 12);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        int total = _data?.TotalRuns ?? 0;
        int wins = _data?.Wins ?? 0;
        int losses = total - wins;
        float overall = total > 0 ? (float)wins / total : 0f;

        // Card 1: data scope — only the bottom line "数据范围: N 局".
        AddSummaryCard(grid, IconClock,
            "",
            string.Format(L.Get("settings.sample"), total));

        // Cards 2 + 3: ascension-filtered W/L and win rate. The label uses
        // the currently selected min ascension; ascension 0 falls back to "全部".
        string ascLabel = _minAscension > 0
            ? string.Format(L.Get("career.ascension_n"), _minAscension)
            : L.Get("career.ascension_all");

        // Round 9 round 49/52: descriptive label on top, right-aligned
        // numeric value on bottom (BBCode [right]...[/right] since the
        // NStatEntry bottom label is a MegaRichTextLabel).
        AddSummaryCard(grid, IconSwords,
            string.Format(L.Get("career.asc_winloss"), ascLabel),
            $"[right]{wins} W / {losses} L[/right]");
        AddSummaryCard(grid, IconChain,
            string.Format(L.Get("career.asc_winrate"), ascLabel),
            $"[right]{overall * 100:F1}%[/right]");
        // Round 9 round 52: best win streak card now shows "current / max".
        int bestStreak = _data?.MaxWinStreak ?? 0;
        int curStreak = _data?.CurrentWinStreak ?? 0;
        AddSummaryCard(grid, IconAchievements,
            string.Format(L.Get("career.asc_max_streak"), ascLabel),
            $"[right]{curStreak}  /  {bestStreak}[/right]");

        return grid;
    }

    /// <summary>
    /// Helper: instantiate the native NStatEntry prefab and set text on it.
    /// Uses CallDeferred so the text is set after _Ready has wired up the
    /// internal labels (instantiation order can be subtle for PackedScenes).
    /// </summary>
    private static void AddSummaryCard(GridContainer grid, string iconPath, string topText, string bottomText)
    {
        try
        {
            var entry = MegaCrit.Sts2.Core.Nodes.Screens.StatsScreen.NStatEntry.Create(iconPath);
            grid.AddChild(entry);

            // Schedule text-setting on the next idle frame so NStatEntry._Ready
            // (which lazily resolves _topLabel / _bottomLabel via %-syntax)
            // is guaranteed to have run.
            // Round 9 round 49: skip SetTopText when topText is empty so the
            // native NStatEntry leaves _topLabel hidden — that lets the
            // remaining bottom label vertically center inside the card.
            if (!string.IsNullOrEmpty(topText))
            {
                entry.CallDeferred(
                    MegaCrit.Sts2.Core.Nodes.Screens.StatsScreen.NStatEntry.MethodName.SetTopText,
                    topText);
            }
            if (!string.IsNullOrEmpty(bottomText))
            {
                entry.CallDeferred(
                    MegaCrit.Sts2.Core.Nodes.Screens.StatsScreen.NStatEntry.MethodName.SetBottomText,
                    bottomText);
            }
            Util.Safe.Info($"[CareerStatsSection] AddSummaryCard scheduled top='{topText}' bottom='{bottomText}'");
        }
        catch (System.Exception ex)
        {
            Util.Safe.Warn($"[CareerStatsSection] AddSummaryCard failed: {ex.Message}");
        }
    }

    // PRD §3.18.3 — independent character dropdown for CareerStats. Order
    // mirrors the F9 dropdown but without "auto" (this view is character-
    // agnostic by design and defaults to All Characters).
    private static readonly string?[] _careerCharacters = new string?[]
    {
        null,           // All
        "IRONCLAD",
        "SILENT",
        "DEFECT",
        "NECROBINDER",
        "REGENT",
    };
    private static readonly string[] _careerCharacterLocKeys = new[]
    {
        "settings.char_all",
        "char.IRONCLAD",
        "char.SILENT",
        "char.DEFECT",
        "char.NECROBINDER",
        "char.REGENT",
    };

    private Control BuildHeader()
    {
        // Round 9 round 52: header now contains both the character filter
        // dropdown AND the min-ascension selector, laid out in a 2-column
        // GridContainer so both rows share identical label/control widths.
        // The min-ascension row used to live inside BuildWinRateSummaryCard.
        var panel = SectionPanel(L.Get("career.title"));
        var v = (VBoxContainer)panel.GetChild(0);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(grid);

        // Row 1: character source dropdown.
        var charLbl = MakeLabel(L.Get("settings.character"), Cream, LabelSize);
        charLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        charLbl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        grid.AddChild(charLbl);

        var charDropdown = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        PopulateCharacterDropdown(charDropdown);
        var currentIdx = Array.IndexOf(_careerCharacters, _characterFilter);
        charDropdown.Selected = currentIdx >= 0 ? currentIdx : 0;
        charDropdown.ItemSelected += i => OnCharacterDropdownChanged((int)i);
        grid.AddChild(charDropdown);

        // Row 2: min-ascension spin box (moved here from win-rate summary).
        var ascLbl = MakeLabel(L.Get("career.min_ascension"), Cream, LabelSize);
        ascLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ascLbl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        grid.AddChild(ascLbl);

        var ascSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 10,
            Step = 1,
            Value = _minAscension,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        ascSpin.AddThemeFontSizeOverride("font_size", LabelSize);
        ascSpin.ValueChanged += d => OnAscensionChanged((int)d);
        grid.AddChild(ascSpin);

        return panel;
    }

    /// <summary>
    /// Add icon+name items for "全部角色" + the 5 characters. Uses
    /// CharacterModel.IconTexture (the small top-panel character icon) and
    /// CharacterModel.Title.GetFormattedText() for the localized name.
    /// "全部角色" reuses RandomCharacter's icon (the same one that appears
    /// on the character-select random button).
    /// </summary>
    private static void PopulateCharacterDropdown(OptionButton dropdown)
    {
        AddCharIcon(dropdown, 0,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.RandomCharacter>(),
            L.Get("settings.char_all"));
        AddCharIcon(dropdown, 1,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Ironclad>("char.IRONCLAD"));
        AddCharIcon(dropdown, 2,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Silent>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Silent>("char.SILENT"));
        AddCharIcon(dropdown, 3,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Defect>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Defect>("char.DEFECT"));
        AddCharIcon(dropdown, 4,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Necrobinder>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Necrobinder>("char.NECROBINDER"));
        AddCharIcon(dropdown, 5,
            TryGetCharIcon<MegaCrit.Sts2.Core.Models.Characters.Regent>(),
            TryGetCharTitle<MegaCrit.Sts2.Core.Models.Characters.Regent>("char.REGENT"));
    }

    private static void AddCharIcon(OptionButton dropdown, int id, Texture2D? icon, string label)
    {
        if (icon != null)
            dropdown.AddIconItem(icon, label, id);
        else
            dropdown.AddItem(label, id);
    }

    private static Texture2D? TryGetCharIcon<T>() where T : MegaCrit.Sts2.Core.Models.CharacterModel
    {
        try { return MegaCrit.Sts2.Core.Models.ModelDb.Character<T>().IconTexture; }
        catch { return null; }
    }

    private static string TryGetCharTitle<T>(string fallbackKey) where T : MegaCrit.Sts2.Core.Models.CharacterModel
    {
        try
        {
            var t = MegaCrit.Sts2.Core.Models.ModelDb.Character<T>().Title.GetFormattedText();
            if (!string.IsNullOrEmpty(t)) return t!;
        }
        catch { }
        return L.Get(fallbackKey);
    }

    /// <summary>
    /// Build a SectionPanel that visually matches the native 总体数据 panel:
    /// dark rounded background, gold header label at the top, content VBox
    /// underneath. The first child is the VBox so callers can grab it via
    /// `panel.GetChild(0)` and append rows.
    /// </summary>
    private static PanelContainer SectionPanel(string headerText)
    {
        var panel = new PanelContainer();
        // Round 9 round 43: brighter, more saturated colors so the panel
        // actually contrasts against the StatsScreen background. The previous
        // (0.06, 0.07, 0.11) was within 1 channel of the screen tone and
        // appeared completely invisible.
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.16f, 0.23f, 0.96f),
            BorderColor = new Color(0.55f, 0.70f, 0.95f, 0.75f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 14,
            ContentMarginBottom = 14,
            ShadowColor = new Color(0f, 0f, 0f, 0.4f),
            ShadowSize = 4,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        panel.AddChild(v);

        var header = MakeLabel(headerText, Gold, TitleSize);
        v.AddChild(header);

        // Subtle separator under the header to mimic the native 总体数据 panel.
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 4);
        v.AddChild(sep);

        return panel;
    }

    private void OnCharacterDropdownChanged(int idx)
    {
        if (idx < 0 || idx >= _careerCharacters.Length) return;
        var newFilter = _careerCharacters[idx];
        if (newFilter == _characterFilter) return;
        _characterFilter = newFilter;
        ReloadForCurrentFilter();
    }

    private void OnAscensionChanged(int newMinAsc)
    {
        if (newMinAsc == _minAscension) return;
        _minAscension = newMinAsc;
        ReloadForCurrentFilter();
    }

    private void ReloadForCurrentFilter()
    {
        _data = RunHistoryAnalyzer.Instance.GetCached(_characterFilter, _minAscension);
        Rebuild();
        if (_data == null || _data.IsEmpty)
        {
            var c = _characterFilter;
            var a = _minAscension;
            Util.Safe.RunAsync(async () =>
                await RunHistoryAnalyzer.Instance.LoadAllAsync(c, minAscension: a));
        }
    }

    private void AppendWindowCell(HBoxContainer parent, int window, IReadOnlyDictionary<int, float> map)
    {
        var cell = new VBoxContainer();
        cell.AddThemeConstantOverride("separation", 4);

        string header = window == int.MaxValue
            ? L.Get("career.all")
            : string.Format(L.Get("career.last_n"), window);
        cell.AddChild(MakeLabel(header, Cream, LabelSize));

        float rate = map.TryGetValue(window, out var v) ? v : 0f;
        var color = rate >= 0.6f ? Green : (rate >= 0.4f ? Cream : Red);
        cell.AddChild(MakeLabel($"{rate * 100:F1}%", color, ValueSize + 4));

        parent.AddChild(cell);
    }

    private Control BuildDeathCauses()
    {
        var panel = SectionPanel(L.Get("career.death_causes"));
        var v = (VBoxContainer)panel.GetChild(0);

        if (_data == null || _data.DeathCausesByAct.Count == 0)
        {
            v.AddChild(MakeLabel("—", Gray, LabelSize));
            return panel;
        }

        // Round 9 round 47: aggregate across acts, preserve source for icon,
        // top 10 rows. Each row prefixed with a TextureRect icon (or Unicode
        // fallback for Abandoned).
        var aggregated = new Dictionary<(string id, DeathSource src), int>();
        int totalDeaths = 0;
        foreach (var entries in _data.DeathCausesByAct.Values)
        {
            foreach (var entry in entries)
            {
                var key = (entry.EncounterId, entry.Source);
                aggregated[key] = aggregated.GetValueOrDefault(key) + entry.Count;
                totalDeaths += entry.Count;
            }
        }

        var top10 = aggregated
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToList();

        foreach (var (key, count) in top10)
        {
            v.AddChild(BuildDeathRow(key.id, key.src, count, totalDeaths));
        }

        return panel;
    }

    private Control BuildDeathRow(string encounterId, DeathSource src, int count, int totalDeaths)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        // Icon column — fixed width so labels line up. Round 9 round 49:
        // bumped from 36×28 → 44×36 and centered vertically with the label.
        var iconWrap = new Control { CustomMinimumSize = new Vector2(44, 36) };
        iconWrap.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var iconNode = BuildDeathIcon(src, encounterId);
        if (iconNode != null)
        {
            iconWrap.AddChild(iconNode);
            if (iconNode is Control ic)
            {
                ic.AnchorLeft = 0; ic.AnchorRight = 1;
                ic.AnchorTop = 0;  ic.AnchorBottom = 1;
            }
        }
        row.AddChild(iconWrap);

        var name = MakeLabel(Util.NameLookup.DeathCause(encounterId), Cream, LabelSize);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(name);

        float share = totalDeaths > 0 ? (float)count / totalDeaths : 0f;
        var countLbl = MakeLabel($"{count} ({share * 100:F1}%)", Gray, LabelSize);
        row.AddChild(countLbl);

        return row;
    }

    // Round 9 round 49: ancient elder ids that should render an Ancient
    // portrait icon instead of the generic Combat / Event one. NEOW shows up
    // as a Combat death (engine attributes it to KilledByEncounter) but the
    // user expects the elder portrait — same for the other elders.
    private static readonly HashSet<string> AncientElderIds = new()
    {
        "NEOW", "PAEL", "TEZCATARA", "OROBAS", "VAKUU", "TANX", "NONUPEIPE", "DARV",
    };

    private static Control? BuildDeathIcon(DeathSource src, string encounterId)
    {
        // 1. Ancient elder portrait — game stores per-elder PNGs at
        //    ui/run_history/{lowercase_id}.png (see AncientEventModel).
        if (!string.IsNullOrEmpty(encounterId) && AncientElderIds.Contains(encounterId))
        {
            var ancTex = TryLoadTexture("ui/run_history/" + encounterId.ToLowerInvariant() + ".png");
            if (ancTex != null) return MakeIconRect(ancTex);
        }

        // 2. Generic source-based icon: monster for combat, event PNG for events.
        string? path = src switch
        {
            DeathSource.Combat
                => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("ui/run_history/monster.png"),
            DeathSource.Event
                => MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath("ui/run_history/event.png"),
            _ => null,
        };
        if (path != null)
        {
            var tex = TryLoadTextureFromAbsolute(path);
            if (tex != null) return MakeIconRect(tex);
        }

        // 3. Abandoned: render a colored "←" glyph as the return arrow.
        //    "←" (U+2190) is supported by the screen-theme font; "↩" was
        //    rendering as a tofu / fallback emoji on the user's setup.
        string glyph = src == DeathSource.Abandoned ? "←"
                     : src == DeathSource.Event ? "?"
                     : "⚔";
        var lbl = new Label { Text = glyph };
        lbl.HorizontalAlignment = HorizontalAlignment.Center;
        lbl.VerticalAlignment = VerticalAlignment.Center;
        lbl.AddThemeFontSizeOverride("font_size", LabelSize + 10);
        lbl.AddThemeColorOverride("font_color", new Color(0.95f, 0.55f, 0.25f));
        return lbl;
    }

    private static Texture2D? TryLoadTexture(string innerPath)
    {
        try
        {
            var path = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath(innerPath);
            return TryLoadTextureFromAbsolute(path);
        }
        catch { return null; }
    }

    private static Texture2D? TryLoadTextureFromAbsolute(string path)
    {
        try
        {
            if (!Godot.ResourceLoader.Exists(path)) return null;
            return Godot.ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
        }
        catch { return null; }
    }

    private static TextureRect MakeIconRect(Texture2D tex) => new TextureRect
    {
        Texture = tex,
        CustomMinimumSize = new Vector2(36, 36),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
    };

    // ── Deck construction table (round 5 — split from path stats) ──

    private static readonly Color[] DeckColors = new[]
    {
        new Color("#EFC851"),               // gold
        new Color(0.16f, 0.92f, 0.75f),     // aqua
        new Color(0.90f, 0.30f, 0.30f),     // red
        new Color(0.30f, 0.85f, 0.40f),     // green
    };
    // Round 9 round 49: recolored per user spec — monsters yellow, elite orange,
    // ? lavender (kept), shops blue, campfire red.
    private static readonly Color[] PathColors = new[]
    {
        new Color("#EFC851"),               // yellow — monsters
        new Color(0.95f, 0.55f, 0.25f),     // orange — elite
        new Color(0.74f, 0.55f, 0.95f),     // lavender — ?
        new Color(0.36f, 0.66f, 0.98f),     // blue — shop
        new Color(0.90f, 0.30f, 0.30f),     // red — campfire
    };

    private Control BuildDeckTable()
    {
        var panel = SectionPanel(L.Get("career.deck_section"));
        var v = (VBoxContainer)panel.GetChild(0);

        if (_data == null || _data.PathStatsByAct.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        var acts = _data.PathStatsByAct.Keys.OrderBy(k => k).ToList();
        var grid = NewActGrid(acts);
        v.AddChild(grid);

        // Round 9 round 48: per-row icons in front of the label.
        AddPathRow(grid, acts, "career.cards_gained",   DeckColors[0], s => s.CardsGained,
            iconPath: "ui/reward_screen/reward_icon_card.png");
        AddPathRow(grid, acts, "career.cards_bought",   DeckColors[1], s => s.CardsBought,
            iconPath: "ui/run_history/shop.png");
        AddPathRow(grid, acts, "career.cards_removed",  DeckColors[2], s => s.CardsRemoved,
            iconPath: "ui/reward_screen/reward_icon_card_removal.png");
        AddPathRow(grid, acts, "career.cards_upgraded", DeckColors[3], s => s.CardsUpgraded,
            iconPath: "ui/rest_site/option_smith.png");
        return panel;
    }

    private Control BuildPathTable()
    {
        var panel = SectionPanel(L.Get("career.path_section"));
        var v = (VBoxContainer)panel.GetChild(0);

        if (_data == null || _data.PathStatsByAct.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        var acts = _data.PathStatsByAct.Keys.OrderBy(k => k).ToList();
        var grid = NewActGrid(acts);
        v.AddChild(grid);

        AddPathRow(grid, acts, "career.monster_rooms",  PathColors[0], s => s.MonsterRooms,
            iconPath: "ui/run_history/monster.png");
        AddPathRow(grid, acts, "career.elite_rooms",    PathColors[1], s => s.EliteRooms,
            iconPath: "ui/run_history/elite.png");
        AddPathRow(grid, acts, "career.unknown_rooms",  PathColors[2], s => s.UnknownRooms,
            iconPath: "ui/run_history/event.png");
        AddPathRow(grid, acts, "career.shop_rooms",     PathColors[3], s => s.ShopRooms,
            iconPath: "ui/run_history/shop.png");
        AddPathRow(grid, acts, "career.campfire_rooms", PathColors[4], s => s.CampfireRooms,
            iconPath: "ui/run_history/rest_site.png");
        return panel;
    }

    private GridContainer NewActGrid(List<int> acts)
    {
        // Round 9 alignment fix (mirrors RunHistoryStatsSection): header
        // cells must use the same right-aligned, ExpandFill, fixed-min-width
        // sizing as the data cells so each act column lines up.
        var grid = new GridContainer { Columns = acts.Count + 1 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 2);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var corner = MakeLabel("", Gray, LabelSize);
        corner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(corner);

        foreach (var act in acts)
        {
            var lbl = MakeLabel(Util.NameLookup.ActLabel(act), Gold, LabelSize);
            lbl.HorizontalAlignment = HorizontalAlignment.Right;
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            lbl.CustomMinimumSize = new Vector2(56, 0);
            grid.AddChild(lbl);
        }
        return grid;
    }

    private void AddPathRow(GridContainer grid, List<int> acts, string labelKey, Color rowColor,
        System.Func<ActPathStats, float> selector, string? iconPath = null)
    {
        // First column is now an HBox containing optional icon + label so the
        // icon sits flush left of the row label.
        var headerHb = new HBoxContainer();
        headerHb.AddThemeConstantOverride("separation", 8);
        headerHb.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        if (iconPath != null)
        {
            var iconNode = TryLoadIconRect(iconPath, 32);
            if (iconNode != null) headerHb.AddChild(iconNode);
        }

        var label = MakeLabel(L.Get(labelKey), rowColor, LabelSize);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        headerHb.AddChild(label);
        grid.AddChild(headerHb);

        foreach (var act in acts)
        {
            float v = _data!.PathStatsByAct.TryGetValue(act, out var stats)
                ? selector(stats)
                : 0f;
            var cell = MakeLabel($"{v:F1}", Cream, ValueSize);
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cell.CustomMinimumSize = new Vector2(56, 0);
            grid.AddChild(cell);
        }
    }

    /// <summary>
    /// PRD §3.11 round 5: Ancient panel — dropdown of elders prefixed with
    /// their Act and a small icon. Each elder is split into 1-3 option pools
    /// (from `AncientPoolMap`); the per-pool list shows every relic with its
    /// pick rate / pick count / win rate / delta. Pulled icons via
    /// `AncientPoolMap.GetElderIcon` and `GetRelicIcon`.
    /// </summary>
    private Control BuildAncientPickRates()
    {
        var panel = SectionPanel(L.Get("career.ancient_title"));
        var v = (VBoxContainer)panel.GetChild(0);

        // Always show every elder we know about (even with 0 encounters) so the
        // dropdown is consistent across runs and shows the full menu structure.
        var allElders = Util.AncientPoolMap.AllElders
            .OrderBy(e => e.ActIndex == 0 ? 99 : e.ActIndex)
            .ThenBy(e => e.ElderId)
            .ToList();
        if (allElders.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        var dropdown = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = L.Get("career.elder_select"),
        };
        // (font size inherits from screen theme)
        for (int i = 0; i < allElders.Count; i++)
        {
            var elder = allElders[i];
            int encounters = (_data?.AncientByElder.TryGetValue(elder.ElderId, out var ee) ?? false)
                ? ee!.Encounters : 0;
            var actLabel = elder.ActIndex == 0 ? L.Get("ancient.shared_act") : Util.NameLookup.ActLabel(elder.ActIndex);
            var name = Util.NameLookup.Ancient(elder.ElderId);
            var label = $"{actLabel}  {name}  ({encounters} {L.Get("career.encounters")})";
            dropdown.AddItem(label, i);
            // Try to attach the elder icon to the dropdown item.
            var icon = Util.AncientPoolMap.GetElderIcon(elder.ElderId);
            if (icon != null)
            {
                try { dropdown.SetItemIcon(i, icon); } catch { }
            }
        }
        v.AddChild(dropdown);

        var detail = new VBoxContainer();
        detail.AddThemeConstantOverride("separation", 4);
        v.AddChild(detail);

        Action<int> showElder = idx =>
        {
            for (int i = detail.GetChildCount() - 1; i >= 0; i--)
                detail.GetChild(i).QueueFree();
            if (idx < 0 || idx >= allElders.Count) return;
            BuildElderDetail(detail, allElders[idx]);
        };

        dropdown.ItemSelected += i => showElder((int)i);
        dropdown.Selected = 0;
        showElder(0);

        return panel;
    }

    /// <summary>
    /// Render one elder's pools. The pool layout comes from `AncientPoolMap`
    /// (per-elder hand-curated metadata) and pick stats from `_data.AncientByElder`.
    /// </summary>
    private void BuildElderDetail(VBoxContainer parent, Util.AncientPoolMap.ElderInfo info)
    {
        // Resolve the player's per-relic counts for this elder, if any.
        var relicStats = new Dictionary<string, ElderRelicStats>();
        if (_data != null && _data.AncientByElder.TryGetValue(info.ElderId, out var entry) && entry != null)
        {
            foreach (var opt in entry.Options)
            foreach (var r in opt.Relics)
                relicStats[r.RelicId] = r;
        }

        // Header row: act + icon + name + encounters
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);

        var icon = Util.AncientPoolMap.GetElderIcon(info.ElderId);
        if (icon != null)
        {
            var iconRect = new TextureRect
            {
                Texture = icon,
                CustomMinimumSize = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspect,
            };
            header.AddChild(iconRect);
        }

        var actLabel = info.ActIndex == 0 ? L.Get("ancient.shared_act") : Util.NameLookup.ActLabel(info.ActIndex);
        header.AddChild(MakeLabel(actLabel, Gold, LabelSize + 1));
        var nameLbl = MakeLabel(Util.NameLookup.Ancient(info.ElderId), Cream, LabelSize + 2);
        nameLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(nameLbl);
        int totalEnc = (_data?.AncientByElder.TryGetValue(info.ElderId, out var ee2) ?? false) ? ee2!.Encounters : 0;
        header.AddChild(MakeLabel($"{totalEnc} {L.Get("career.encounters")}", Gray, LabelSize));
        parent.AddChild(header);

        // Render each pool as its own subsection. Round 9: each pool now
        // uses a GridContainer so the relic row columns (icon | name |
        // pick rate | picks | win rate | delta) line up under the column
        // header row, and have visual breathing room between metrics.
        foreach (var pool in info.Pools)
        {
            // Round 9 round 49: extra vertical breathing room above each pool
            // header so the title doesn't crowd the previous pool's last row.
            var spacer = new Control { CustomMinimumSize = new Vector2(0, 14) };
            parent.AddChild(spacer);

            var poolHeader = MakeLabel("• " + L.Get(pool.DisplayKey), Gold, LabelSize);
            parent.AddChild(poolHeader);

            int totalPicksInPool = pool.RelicIds.Sum(rid =>
                relicStats.TryGetValue(rid, out var rs) ? rs.Picks : 0);
            float avgWin = pool.RelicIds
                .Select(rid => relicStats.TryGetValue(rid, out var rs) ? rs.WinRate : 0f)
                .Where(w => w > 0).DefaultIfEmpty(0f).Average();

            var grid = NewElderGrid();
            parent.AddChild(grid);
            AddElderHeaderRow(grid);

            foreach (var relicId in pool.RelicIds)
            {
                var stats = relicStats.TryGetValue(relicId, out var rs) ? rs : null;
                AddRelicGridRow(grid, relicId, stats, totalPicksInPool, avgWin, pool);
            }
        }
    }

    private GridContainer NewElderGrid()
    {
        // 6 columns: icon | name | pick rate | picks | win rate | delta
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 2);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return grid;
    }

    private void AddElderHeaderRow(GridContainer grid)
    {
        // icon column spacer
        grid.AddChild(new Control { CustomMinimumSize = new Vector2(20, 0) });
        // name column header (empty — name doesn't need a label)
        var nameSpacer = MakeLabel("", Gray, LabelSize);
        nameSpacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(nameSpacer);

        AppendNumericHeader(grid, L.Get("career.col_pick_rate"));
        AppendNumericHeader(grid, L.Get("career.col_pick_count"));
        AppendNumericHeader(grid, L.Get("career.col_win_rate"));
        AppendNumericHeader(grid, L.Get("career.col_delta"));
    }

    private void AppendNumericHeader(GridContainer grid, string text)
    {
        var lbl = MakeLabel(text, Gray, LabelSize);
        lbl.HorizontalAlignment = HorizontalAlignment.Right;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.CustomMinimumSize = new Vector2(60, 0);
        grid.AddChild(lbl);
    }

    private void AddRelicGridRow(GridContainer grid, string relicId, ElderRelicStats? stats,
        int poolTotalPicks, float poolAvgWin, Util.AncientPoolMap.Pool pool)
    {
        // Round 9 round 49: enlarge relic icons to match the row text height
        // and vertically center them with the row label.
        var icon = Util.AncientPoolMap.GetRelicIcon(relicId);
        if (icon != null)
        {
            var rect = new TextureRect
            {
                Texture = icon,
                CustomMinimumSize = new Vector2(LabelSize + 6, LabelSize + 6),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            grid.AddChild(rect);
        }
        else
        {
            grid.AddChild(new Control { CustomMinimumSize = new Vector2(LabelSize + 6, LabelSize + 6) });
        }

        var displayName = Util.NameLookup.Relic(relicId);
        if (pool.ActGate != null && pool.ActGate.TryGetValue(relicId, out var gateAct))
            displayName += " " + string.Format(L.Get("ancient.act_only_n"), gateAct);
        var name = MakeLabel(displayName, Cream, LabelSize);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        grid.AddChild(name);

        if (stats != null && stats.Picks > 0)
        {
            float pickRate = poolTotalPicks > 0 ? (float)stats.Picks / poolTotalPicks : 0f;
            AppendNumericCell(grid, $"{pickRate * 100:F1}%", Gold);
            AppendNumericCell(grid, $"{stats.Picks}", Gray);

            var winColor = stats.WinRate >= 0.6f ? Green
                        : stats.WinRate >= 0.4f ? Cream : Red;
            AppendNumericCell(grid, $"{stats.WinRate * 100:F1}%", winColor);

            // Round 9 round 49: delta = relic win rate − overall career win
            // rate (under the same character + ascension filter), NOT vs the
            // pool average. Pool average was a poor baseline because it shifts
            // when one relic dominates picks.
            float overallWin = (_data != null && _data.TotalRuns > 0)
                ? (float)_data.Wins / _data.TotalRuns
                : 0f;
            float delta = (stats.WinRate - overallWin) * 100f;
            var sign = delta >= 0 ? "+" : "";
            var dColor = MathF.Abs(delta) < 1f ? Gray : (delta >= 0 ? Green : Red);
            AppendNumericCell(grid, $"{sign}{delta:F1}%", dColor);
        }
        else
        {
            AppendNumericCell(grid, "—", Gray);
            AppendNumericCell(grid, "0", Gray);
            AppendNumericCell(grid, "—", Gray);
            AppendNumericCell(grid, "—", Gray);
        }
    }

    private void AppendNumericCell(GridContainer grid, string text, Color color)
    {
        var lbl = MakeLabel(text, color, LabelSize);
        lbl.HorizontalAlignment = HorizontalAlignment.Right;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.CustomMinimumSize = new Vector2(60, 0);
        grid.AddChild(lbl);
    }

    /// <summary>
    /// PRD §3.11 round 5: Boss panel — dropdown listing bosses with the act
    /// prefix, encounter count and full localized name. The selected boss
    /// expands to a small detail card with avg damage / death rate / encounters.
    /// Boss icons are sourced from the encounter model when available.
    /// </summary>
    private Control BuildBossStats()
    {
        var panel = SectionPanel(L.Get("career.boss_title"));
        var v = (VBoxContainer)panel.GetChild(0);

        // Round 9 round 49: union the player's BossStats with the full game
        // boss list so 0-encounter bosses still appear as placeholder rows.
        var statsByEnc = _data?.BossStats ?? new Dictionary<string, BossEncounterStats>();
        var allIds = AllKnownBossEncounterIds();
        // Add any extra ids the player has data for that aren't in ModelDb
        // (paranoid catch — modded encounters etc.).
        foreach (var id in statsByEnc.Keys)
            if (!allIds.Contains(id)) allIds.Add(id);

        if (allIds.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        // Build display rows: every known boss + its (possibly zero) stats.
        var ranked = allIds
            .Select(id => statsByEnc.TryGetValue(id, out var s)
                ? s
                : new BossEncounterStats { EncounterId = id, Encounters = 0, Deaths = 0, AverageDamageTaken = 0f })
            .OrderBy(b => BossActFor(b.EncounterId).SortKey)
            .ThenByDescending(b => b.Encounters)
            .ThenBy(b => Util.NameLookup.Encounter(b.EncounterId))
            .ToList();

        var dropdown = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = L.Get("career.boss_select"),
        };
        // Round 9 round 49: cap the popup height so a long boss list doesn't
        // cover the whole screen. PopupMenu inherits from Window, so MaxSize
        // controls the absolute upper bound; items beyond it auto-scroll.
        try { dropdown.GetPopup().MaxSize = new Vector2I(0, 560); } catch { }
        // (font size inherits from screen theme)
        for (int i = 0; i < ranked.Count; i++)
        {
            var b = ranked[i];
            var actInfo = BossActFor(b.EncounterId);
            var actLabel = string.IsNullOrEmpty(actInfo.Label) ? "" : actInfo.Label + "  ";
            var name = Util.NameLookup.Encounter(b.EncounterId);
            var label = $"{actLabel}{name}  ({b.Encounters} {L.Get("career.encounters")})";
            dropdown.AddItem(label, i);

            var icon = Util.AncientPoolMap.GetEncounterIcon(b.EncounterId);
            if (icon != null)
            {
                try { dropdown.SetItemIcon(i, icon); } catch { }
            }
        }
        v.AddChild(dropdown);

        var detail = new VBoxContainer();
        detail.AddThemeConstantOverride("separation", 2);
        v.AddChild(detail);

        Action<int> showBoss = idx =>
        {
            for (int i = detail.GetChildCount() - 1; i >= 0; i--)
                detail.GetChild(i).QueueFree();
            if (idx < 0 || idx >= ranked.Count) return;
            var b = ranked[idx];

            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", 8);
            var bossIcon = Util.AncientPoolMap.GetEncounterIcon(b.EncounterId);
            if (bossIcon != null)
            {
                titleRow.AddChild(new TextureRect
                {
                    Texture = bossIcon,
                    CustomMinimumSize = new Vector2(36, 36),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspect,
                });
            }
            var actInfo2 = BossActFor(b.EncounterId);
            if (!string.IsNullOrEmpty(actInfo2.Label))
                titleRow.AddChild(MakeLabel(actInfo2.Label, Gold, LabelSize + 1));
            var nameLbl = MakeLabel(Util.NameLookup.Encounter(b.EncounterId), Cream, LabelSize + 2);
            nameLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            titleRow.AddChild(nameLbl);
            detail.AddChild(titleRow);

            AddDetailRow(detail, L.Get("career.avg_damage"), $"{b.AverageDamageTaken:F1}", Gold);
            var dColor = b.DeathRate >= 0.15f ? Red : (b.DeathRate >= 0.05f ? Cream : Green);
            AddDetailRow(detail, L.Get("career.death_rate"), $"{b.DeathRate * 100:F1}%", dColor);
            AddDetailRow(detail, L.Get("career.encounters"), $"{b.Encounters}", Gray);
        };

        dropdown.ItemSelected += i => showBoss((int)i);
        dropdown.Selected = 0;
        showBoss(0);

        return panel;
    }

    private void AddDetailRow(VBoxContainer parent, string label, string value, Color valueColor)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeLabel(label, Cream, LabelSize));
        var valLbl = MakeLabel(value, valueColor, LabelSize);
        valLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        valLbl.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(valLbl);
        parent.AddChild(row);
    }

    /// <summary>
    /// Round 9 round 49: ActModel order in code is Overgrowth(0)/Hive(1)/
    /// Glory(2)/Underdocks(3), but Underdocks is actually an alternate Act 1
    /// (暗港), not Act 4. This struct exposes a sort key + display label so
    /// the boss dropdown groups Overgrowth and Underdocks together as "第1幕".
    /// </summary>
    private readonly record struct BossActInfo(int SortKey, string Label);

    private static BossActInfo BossActInfoForActIndex(int actIdx0)
    {
        // actIdx0 is the 0-based index into ModelDb.Acts.
        // Sort keys interleave Overgrowth/Underdocks before Hive/Glory.
        return actIdx0 switch
        {
            0 => new BossActInfo(10, string.Format(L.Get("career.act_n"), 1) + L.Get("career.act_overgrowth")),
            3 => new BossActInfo(15, string.Format(L.Get("career.act_n"), 1) + L.Get("career.act_underdocks")),
            1 => new BossActInfo(20, string.Format(L.Get("career.act_n"), 2)),
            2 => new BossActInfo(30, string.Format(L.Get("career.act_n"), 3)),
            _ => new BossActInfo(99, ""),
        };
    }

    /// <summary>
    /// Locate the lowest act (by code index) that contains this boss encounter,
    /// then map to the display info. Falls back to (0, "") on miss.
    /// </summary>
    private static BossActInfo BossActFor(string encounterId)
    {
        try
        {
            var acts = MegaCrit.Sts2.Core.Models.ModelDb.Acts.ToList();
            for (int i = 0; i < acts.Count; i++)
            {
                foreach (var enc in acts[i].AllEncounters)
                {
                    if (enc.Id.Entry == encounterId)
                        return BossActInfoForActIndex(i);
                }
            }
        }
        catch { }
        return new BossActInfo(0, "");
    }

    /// <summary>
    /// Round 9 round 49: enumerate every boss encounter known to the game
    /// (across all 4 acts in code) so the dropdown can show 0-encounter rows
    /// alongside the ones the player has actually fought.
    /// </summary>
    private static List<string> AllKnownBossEncounterIds()
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        try
        {
            foreach (var act in MegaCrit.Sts2.Core.Models.ModelDb.Acts)
            {
                foreach (var enc in act.AllBossEncounters)
                {
                    var id = enc.Id.Entry;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (seen.Add(id)) result.Add(id);
                }
            }
        }
        catch { }
        return result;
    }

    // ── UI helpers ──────────────────────────────────────────

    /// <summary>
    /// Best-effort load an image at `ui/...` and wrap in a sized TextureRect.
    /// Returns null when the path doesn't resolve so callers can skip the row.
    /// </summary>
    private static TextureRect? TryLoadIconRect(string innerPath, int size)
    {
        try
        {
            var path = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath(innerPath);
            if (!Godot.ResourceLoader.Exists(path)) return null;
            var tex = Godot.ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            if (tex == null) return null;
            return new TextureRect
            {
                Texture = tex,
                CustomMinimumSize = new Vector2(size, size),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
        }
        catch { return null; }
    }

    private static Label MakeLabel(string text, Color color, int size)
    {
        // Round 9 round 44: re-enable explicit font_size override. The screen
        // theme's default Label font is much smaller than the native MegaLabel
        // sizes used in the 总体数据 panel; without this override our content
        // looks tiny next to the surrounding game UI.
        var l = new Label();
        l.Text = text;
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static PanelContainer WrapInPanel()
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = SectionBg,
            BorderColor = Border,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return panel;
    }
}
