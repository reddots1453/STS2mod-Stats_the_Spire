using System.Collections.Generic;
using System.Linq;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CommunityStats.UI;

/// <summary>
/// Single-run statistics block injected into the
/// 百科大全 → 历史记录 detail screen (PRD §3.12 round 5).
///
/// Layout (from top to bottom):
///   1. Title row (character / ascension / floors / win-loss)
///   2. **Deck Construction** table — 4 metrics × Acts
///   3. **Path Stats** table — 4 metrics × Acts
///   4. **Ancient picks** chronological list ("第N幕  先古之民  遗物")
///   5. **Boss damage** list (one row per boss with HP lost)
///   6. **View contribution** button (opens ContributionPanel showing
///      ONLY the run-summary tab — never the live combat tab)
///
/// All section panels are color-coded so the user can scan quickly. Path
/// stats and Deck construction were previously merged in a single ugly
/// grid; round 5 splits them per user feedback.
/// </summary>
public sealed partial class RunHistoryStatsSection : VBoxContainer
{
    // ── Style palette ───────────────────────────────────────
    private static readonly Color Gold      = new("#EFC851");
    private static readonly Color Cream     = new("#FFF6E2");
    private static readonly Color Gray      = new(0.6f, 0.6f, 0.65f);
    private static readonly Color Green     = new(0.30f, 0.85f, 0.40f);
    private static readonly Color Red       = new(0.90f, 0.30f, 0.30f);
    private static readonly Color Aqua      = new(0.16f, 0.92f, 0.75f);
    private static readonly Color Lavender  = new(0.74f, 0.55f, 0.95f);
    private static readonly Color Orange    = new(0.95f, 0.55f, 0.25f);
    private static readonly Color SectionBg = new(0.05f, 0.06f, 0.10f, 0.85f);
    private static readonly Color Border    = new(0.3f, 0.4f, 0.6f, 0.45f);

    // Round 9 round 51: per-row colors mirror CareerStatsSection.
    private static readonly Color[] DeckColors = new[]
    {
        new Color("#EFC851"),               // gold
        new Color(0.16f, 0.92f, 0.75f),     // aqua
        new Color(0.90f, 0.30f, 0.30f),     // red
        new Color(0.30f, 0.85f, 0.40f),     // green
    };
    private static readonly Color[] PathColors = new[]
    {
        new Color("#EFC851"),               // yellow — monsters
        new Color(0.95f, 0.55f, 0.25f),     // orange — elite
        new Color(0.74f, 0.55f, 0.95f),     // lavender — ?
        new Color(0.36f, 0.66f, 0.98f),     // blue — shop
        new Color(0.90f, 0.30f, 0.30f),     // red — campfire
    };

    // Round 9 round 51: bumped from 16/13/12 to match the popup viewing
    // distance and the existing CareerStatsSection sizes.
    private const int TitleSize    = 30;
    private const int SubtitleSize = 24;
    private const int LabelSize    = 22;

    private SingleRunStatsData? _data;
    private RunHistory? _history;

    private RunHistoryStatsSection() { }

    public static RunHistoryStatsSection Create(RunHistory history)
    {
        var s = new RunHistoryStatsSection
        {
            Name = "ModRunHistoryStatsSection",
        };
        s.AddThemeConstantOverride("separation", 14);
        s.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        s.MouseFilter = MouseFilterEnum.Pass;

        s._history = history;
        s._data = RunHistoryAnalyzer.Instance.BuildSingleRunStats(history);
        s.Build();
        return s;
    }

    private void Build()
    {
        AddChild(BuildHeader());
        AddChild(BuildDeckTable());
        AddChild(BuildPathTable());
        AddChild(BuildAncientPicks());
        AddChild(BuildBossDamage());
        AddChild(BuildReplayButton());
    }

    // ── Header ──────────────────────────────────────────────

    private Control BuildHeader()
    {
        var panel = WrapInPanel();
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        panel.AddChild(v);

        v.AddChild(MakeLabel(L.Get("runhist.section_title"), Gold, TitleSize));

        if (_data != null)
        {
            string winText = _data.Win ? L.Get("runhist.victory") : L.Get("runhist.defeat");
            var color = _data.Win ? Green : Red;
            var charName = ResolveCharacterName(_data.Character);
            var ascText = string.Format(L.Get("career.ascension_n"), _data.Ascension);
            var floorText = string.Format(L.Get("runhist.floors_n"), _data.FloorReached);
            var sub = MakeLabel(
                $"{charName} · {ascText} · {floorText} · {winText}",
                color, SubtitleSize);
            v.AddChild(sub);
        }

        return panel;
    }

    private static string ResolveCharacterName(string characterId)
    {
        if (string.IsNullOrEmpty(characterId)) return "";
        try
        {
            foreach (var c in MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters)
            {
                if (c.Id.Entry == characterId)
                {
                    var t = c.Title.GetFormattedText();
                    if (!string.IsNullOrEmpty(t)) return t!;
                }
            }
        }
        catch { }
        return characterId;
    }

    // ── Deck construction table ─────────────────────────────

    private Control BuildDeckTable()
    {
        var panel = WrapInPanel();
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        panel.AddChild(v);

        v.AddChild(MakeLabel(L.Get("career.deck_section"), Gold, SubtitleSize));

        if (_data == null || _data.PathStatsByAct.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        var acts = _data.PathStatsByAct.Keys.OrderBy(k => k).ToList();
        var grid = BuildActHeaderGrid(acts);
        v.AddChild(grid);

        AddRow(grid, acts, "career.cards_gained",   DeckColors[0], s => s.CardsGained,
            iconPath: "ui/reward_screen/reward_icon_card.png");
        AddRow(grid, acts, "career.cards_bought",   DeckColors[1], s => s.CardsBought,
            iconPath: "ui/run_history/shop.png");
        AddRow(grid, acts, "career.cards_removed",  DeckColors[2], s => s.CardsRemoved,
            iconPath: "ui/reward_screen/reward_icon_card_removal.png");
        AddRow(grid, acts, "career.cards_upgraded", DeckColors[3], s => s.CardsUpgraded,
            iconPath: "ui/rest_site/option_smith.png");

        return panel;
    }

    // ── Path stats table ────────────────────────────────────

    private Control BuildPathTable()
    {
        var panel = WrapInPanel();
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        panel.AddChild(v);

        v.AddChild(MakeLabel(L.Get("career.path_section"), Gold, SubtitleSize));

        if (_data == null || _data.PathStatsByAct.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        var acts = _data.PathStatsByAct.Keys.OrderBy(k => k).ToList();
        var grid = BuildActHeaderGrid(acts);
        v.AddChild(grid);

        AddRow(grid, acts, "career.monster_rooms",  PathColors[0], s => s.MonsterRooms,
            iconPath: "ui/run_history/monster.png");
        AddRow(grid, acts, "career.elite_rooms",    PathColors[1], s => s.EliteRooms,
            iconPath: "ui/run_history/elite.png");
        AddRow(grid, acts, "career.unknown_rooms",  PathColors[2], s => s.UnknownRooms,
            iconPath: "ui/run_history/event.png");
        AddRow(grid, acts, "career.shop_rooms",     PathColors[3], s => s.ShopRooms,
            iconPath: "ui/run_history/shop.png");
        AddRow(grid, acts, "career.campfire_rooms", PathColors[4], s => s.CampfireRooms,
            iconPath: "ui/run_history/rest_site.png");

        return panel;
    }

    private GridContainer BuildActHeaderGrid(List<int> acts)
    {
        // Round 9 alignment fix: header cells must mirror the data cells'
        // alignment (right-aligned, ExpandFill) so each column lines up
        // visually. The leading "row label" column uses ExpandFill so the
        // metric labels claim the leftover horizontal space.
        var grid = new GridContainer { Columns = acts.Count + 1 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 2);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var corner = MakeLabel("", Gray, LabelSize);
        corner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(corner);

        foreach (var a in acts)
        {
            var lbl = MakeLabel(NameLookup.ActLabel(a), Gold, LabelSize);
            lbl.HorizontalAlignment = HorizontalAlignment.Right;
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            lbl.CustomMinimumSize = new Vector2(48, 0);
            grid.AddChild(lbl);
        }
        return grid;
    }

    private void AddRow(GridContainer grid, List<int> acts, string labelKey, Color rowColor,
        System.Func<ActPathStats, float> selector, string? iconPath = null)
    {
        // Round 9 round 51: row label column now wraps an HBox containing
        // optional icon + text label, mirroring CareerStatsSection.
        var headerHb = new HBoxContainer();
        headerHb.AddThemeConstantOverride("separation", 8);
        headerHb.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        if (iconPath != null)
        {
            var iconRect = TryLoadIconRect(iconPath, LabelSize + 6);
            if (iconRect != null) headerHb.AddChild(iconRect);
        }

        var label = MakeLabel(L.Get(labelKey), rowColor, LabelSize);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        headerHb.AddChild(label);
        grid.AddChild(headerHb);

        foreach (var act in acts)
        {
            float v = _data!.PathStatsByAct.TryGetValue(act, out var stats) ? selector(stats) : 0f;
            var cell = MakeLabel($"{(int)v}", Cream, LabelSize);
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cell.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cell.CustomMinimumSize = new Vector2(56, 0);
            grid.AddChild(cell);
        }
    }

    private static TextureRect MakeIconRect(Texture2D tex, int size) => new TextureRect
    {
        Texture = tex,
        CustomMinimumSize = new Vector2(size, size),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        SizeFlagsVertical = SizeFlags.ShrinkCenter,
    };

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
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
        }
        catch { return null; }
    }

    // ── Ancient picks chronological list ────────────────────

    private Control BuildAncientPicks()
    {
        var panel = WrapInPanel();
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        panel.AddChild(v);

        v.AddChild(MakeLabel(L.Get("runhist.ancient_picks"), Gold, SubtitleSize));

        var picks = ResolveAncientPicks();
        if (picks.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        foreach (var pick in picks)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            row.AddChild(MakeLabel(NameLookup.ActLabel(pick.Act), Gold, LabelSize));

            // Round 9 round 51: elder portrait icon + chinese name.
            var elderIcon = Util.AncientPoolMap.GetElderIcon(pick.ElderId);
            if (elderIcon != null)
                row.AddChild(MakeIconRect(elderIcon, LabelSize + 6));
            row.AddChild(MakeLabel(NameLookup.Ancient(pick.ElderId), Aqua, LabelSize));

            // Relic icon + name. ExpandFill on the name pushes it to take the
            // remaining horizontal space.
            var relicIcon = Util.AncientPoolMap.GetRelicIcon(pick.RelicId);
            if (relicIcon != null)
                row.AddChild(MakeIconRect(relicIcon, LabelSize + 6));
            var relic = MakeLabel(NameLookup.Relic(pick.RelicId), Cream, LabelSize);
            relic.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            relic.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(relic);

            v.AddChild(row);
        }

        return panel;
    }

    private record AncientPick(int Act, string ElderId, string RelicId);

    /// <summary>
    /// Walk MapPointHistory once and produce the per-encounter list of
    /// "first chosen relic" entries — used by the ancient picks display.
    /// </summary>
    private List<AncientPick> ResolveAncientPicks()
    {
        var result = new List<AncientPick>();
        if (_history?.MapPointHistory == null) return result;

        for (int actIdx = 0; actIdx < _history.MapPointHistory.Count; actIdx++)
        {
            var floors = _history.MapPointHistory[actIdx];
            if (floors == null) continue;
            int displayAct = actIdx + 1;

            foreach (var floor in floors)
            {
                if (floor == null) continue;
                if (floor.MapPointType != MapPointType.Ancient) continue;
                if (floor.Rooms == null || floor.Rooms.Count == 0) continue;

                var elderId = floor.Rooms[0].ModelId.Entry;
                if (string.IsNullOrEmpty(elderId)) continue;

                if (floor.PlayerStats == null) continue;
                foreach (var ps in floor.PlayerStats)
                {
                    if (ps.AncientChoices == null) continue;
                    foreach (var choice in ps.AncientChoices)
                    {
                        if (!choice.WasChosen) continue;
                        var key = choice.Title?.LocEntryKey;
                        if (string.IsNullOrEmpty(key)) continue;
                        // Strip trailing ".title" → relic id.
                        int dot = key!.LastIndexOf('.');
                        var relicId = dot > 0 ? key.Substring(0, dot) : key;
                        result.Add(new AncientPick(displayAct, elderId, relicId));
                    }
                }
            }
        }
        return result;
    }

    // ── Boss damage list ────────────────────────────────────

    private Control BuildBossDamage()
    {
        var panel = WrapInPanel();
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        panel.AddChild(v);

        v.AddChild(MakeLabel(L.Get("career.boss_title"), Gold, SubtitleSize));

        if (_data == null || _data.BossDamageTaken.Count == 0)
        {
            v.AddChild(MakeLabel(L.Get("career.no_data_short"), Gray, LabelSize));
            return panel;
        }

        foreach (var (boss, dmg) in _data.BossDamageTaken)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            // Round 9 round 51: boss encounter icon before the name.
            var bossIcon = Util.AncientPoolMap.GetEncounterIcon(boss);
            if (bossIcon != null)
                row.AddChild(MakeIconRect(bossIcon, LabelSize + 6));
            var name = MakeLabel(NameLookup.Encounter(boss), Cream, LabelSize);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var dmgLabel = MakeLabel($"{dmg} HP", Red, LabelSize);
            dmgLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(name);
            row.AddChild(dmgLabel);
            v.AddChild(row);
        }

        return panel;
    }

    // ── Replay button ───────────────────────────────────────

    private Control BuildReplayButton()
    {
        var panel = WrapInPanel();
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        panel.AddChild(h);

        var btn = new Button { Text = L.Get("runhist.view_contrib") };
        btn.AddThemeFontSizeOverride("font_size", LabelSize);
        h.AddChild(btn);

        var seed = _data?.Seed ?? "";
        btn.Pressed += () => OnReplayPressed(seed);

        if (string.IsNullOrEmpty(seed))
        {
            btn.Disabled = true;
        }
        else
        {
            var summary = ContributionPersistence.LoadRunSummary(seed);
            if (summary == null) btn.Disabled = true;
        }

        return panel;
    }

    private static void OnReplayPressed(string seed)
    {
        Safe.Run(() =>
        {
            var summary = ContributionPersistence.LoadRunSummary(seed);
            if (summary == null) return;
            // Round 9 round 51: close our popup overlay before opening the
            // contribution panel. ContributionPanel is parented to the scene
            // root and would otherwise render BEHIND our popup (which is also
            // root-level but added later). Closing avoids the z-order fight.
            CommunityStats.Patches.RunHistoryPatch.CloseOpenPopup();
            // Round 5 fix: only the 本局汇总 tab should be shown when replaying
            // a historical run — there's no live combat to display.
            ContributionPanel.ShowRunReplay(summary);
        });
    }

    // ── UI helpers ──────────────────────────────────────────

    private static Label MakeLabel(string text, Color color, int size)
    {
        var l = new Label();
        l.Text = text;
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static PanelContainer WrapInPanel()
    {
        // Round 9 round 51: brighter section panel matching CareerStatsSection
        // (rounded blue-tinted dark panel with 18/14 padding).
        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.16f, 0.23f, 0.96f),
            BorderColor = new Color(0.55f, 0.70f, 0.95f, 0.75f),
            BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 18, ContentMarginRight = 18,
            ContentMarginTop = 14, ContentMarginBottom = 14,
            ShadowColor = new Color(0f, 0f, 0f, 0.4f),
            ShadowSize = 4,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return panel;
    }
}
