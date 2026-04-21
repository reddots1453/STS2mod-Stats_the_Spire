using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.Util;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace CommunityStats.UI;

/// <summary>
/// Combat contribution panel. Shows per-combat and per-run breakdown of damage,
/// defense, draws, energy, stars, and healing per source (card/relic/potion/power).
///
/// v3 features (manual feedback round 4):
/// - Default anchor = LEFT side (right blocked the post-combat 前进 button)
/// - Two tabs only: 本场战斗 / 本局汇总. The 本场战斗 tab swaps its data
///   source contextually: live combat data while a fight is in progress, the
///   most-recently-finished combat snapshot otherwise.
/// - Tab labels are written via TabContainer.SetTabTitle to avoid the
///   "@ScrollContainer@N" auto-name fallback Godot uses for child nodes.
/// - Draggable via title bar; position persisted to config
/// - DPS row (damage / turn count)
/// - Help (?) button opens an InfoModPanel describing each metric
/// - Real-time refresh via CombatTracker.CombatDataUpdated event (500ms debounce)
/// </summary>
public partial class ContributionPanel : PanelContainer
{
    private static ContributionPanel? _instance;
    private TabContainer? _tabs;
    private Label? _dpsLabel;
    private HBoxContainer? _header;
    private PanelContainer? _helpPanel;

    // Real-time refresh — round 6 dropped the debounce, see OnCombatDataUpdated.

    public static ContributionPanel Instance => _instance ??= CreatePanel();

    public new static bool IsVisible => _instance?.Visible ?? false;

    private static ContributionPanel CreatePanel()
    {
        var panel = new ContributionPanel();
        panel.Name = "StatsTheSpireContribution";
        panel.Visible = false;

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.1f, 0.70f),
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            ContentMarginLeft = 16, ContentMarginRight = 16,
            ContentMarginTop = 12, ContentMarginBottom = 12,
            BorderColor = new Color(0.3f, 0.4f, 0.7f, 0.6f),
            BorderWidthBottom = 1, BorderWidthLeft = 1,
            BorderWidthRight = 1, BorderWidthTop = 1
        };
        panel.AddThemeStyleboxOverride("panel", style);

        // Panel size: 570 wide (75% of 760), height anchored to 50% of screen
        panel.CustomMinimumSize = new Vector2(570, 340);

        panel.AnchorLeft = 0.0f;
        panel.AnchorRight = 0.0f;
        panel.AnchorTop = 0.25f;
        panel.AnchorBottom = 0.75f;
        panel.OffsetLeft = 10;
        panel.OffsetRight = 580;

        // Main layout
        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.SizeFlagsVertical = SizeFlags.ExpandFill;
        panel.AddChild(vbox);

        // Header row with title + help + close button (draggable handle)
        var header = new HBoxContainer();
        header.MouseFilter = MouseFilterEnum.Stop; // ensure it receives drag input
        vbox.AddChild(header);
        panel._header = header;

        var title = new Label { Text = L.Get("contrib.title") };
        title.AddThemeColorOverride("font_color", Colors.White);
        title.AddThemeFontSizeOverride("font_size", 16);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.MouseFilter = MouseFilterEnum.Ignore; // let header receive clicks
        header.AddChild(title);

        // Help (?) button — click toggles a docked InfoModPanel with each metric.
        var helpBtn = new Button
        {
            Text = "?",
            CustomMinimumSize = new Vector2(32, 32),
            TooltipText = L.Get("contrib.help_body"),
        };
        helpBtn.AddThemeFontSizeOverride("font_size", 14);
        helpBtn.Pressed += () => panel.ToggleHelpPanel();
        header.AddChild(helpBtn);

        var closeBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(32, 32) };
        closeBtn.AddThemeFontSizeOverride("font_size", 14);
        closeBtn.Pressed += () => Hide();
        header.AddChild(closeBtn);

        // DPS row (PRD 3.7) — below header
        var dpsRow = new HBoxContainer();
        vbox.AddChild(dpsRow);
        panel._dpsLabel = new Label { Text = $"{L.Get("contrib.dps")} 0.0" };
        panel._dpsLabel.AddThemeColorOverride("font_color", new Color("#FFF6E2"));
        panel._dpsLabel.AddThemeFontSizeOverride("font_size", 12);
        dpsRow.AddChild(panel._dpsLabel);

        // Scroll container wrapping tabs for vertical scrolling
        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        vbox.AddChild(scroll);

        // Tab container inside scroll
        panel._tabs = new TabContainer();
        panel._tabs.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel._tabs.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.AddChild(panel._tabs);

        // Switch DPS readout on tab change so the "每回合平均伤害" always
        // reflects the dataset currently visible (本场战斗 vs 本局汇总).
        panel._tabs.TabChanged += _ => Safe.Run(() => panel.UpdateDps());

        // Enable drag via title bar (PRD 4.5)
        DraggablePanel.Attach(panel, header);

        // Restore saved position if present
        panel.Ready += () => DraggablePanel.RestorePosition(panel, panel.GlobalPosition);

        // Subscribe to real-time updates (PRD 3.6)
        CombatTracker.Instance.CombatDataUpdated += panel.OnCombatDataUpdated;

        return panel;
    }

    /// <summary>
    /// Handler for CombatTracker.CombatDataUpdated. Round 9 round 32 fix:
    /// the previous version used `CallDeferred(nameof(DeferredRefresh))` to
    /// schedule the work next frame, but Godot 4's StringName-based
    /// CallDeferred didn't reliably resolve the private C# method, so the
    /// live refresh silently skipped. Harmony postfixes already run on the
    /// main thread, so we can refresh inline.
    /// </summary>
    private void OnCombatDataUpdated()
    {
        if (!Visible) return;
        Safe.Info("[ContribPanel] CombatDataUpdated → refresh");
        Safe.Run(() => RefreshLive());
    }

    private void RefreshLive()
    {
        // Always read whichever data set the 本场战斗 tab represents right now.
        RefreshTabs(SelectCombatTabData());
        UpdateDps();
    }

    /// <summary>
    /// Pick the dataset for the 本场战斗 tab. While a combat is in progress
    /// the tab shows the running snapshot; otherwise it shows the last
    /// finished combat. PRD §3.6 / manual feedback round 4.
    /// </summary>
    private static IReadOnlyDictionary<string, ContributionAccum>? SelectCombatTabData()
    {
        try
        {
            if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance != null
                && MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
            {
                return CombatTracker.Instance.GetCurrentCombatData();
            }
        }
        catch { /* fall through */ }
        return CombatTracker.Instance.LastCombatData;
    }

    /// <summary>
    /// Open or close the help dialog. Round 6 fix: the previous version had no
    /// way to close (the parent's MouseFilter swallowed clicks and there was
    /// no X button on the help panel itself). Now uses a custom PanelContainer
    /// with an explicit close button + clicking the ? button again toggles it.
    /// </summary>
    private void ToggleHelpPanel()
    {
        Safe.Run(() =>
        {
            if (_helpPanel != null && IsInstanceValid(_helpPanel))
            {
                _helpPanel.QueueFree();
                _helpPanel = null;
                return;
            }

            _helpPanel = BuildHelpPanel();
            AddChild(_helpPanel);
            _helpPanel.ZIndex = 1000;
            _helpPanel.GlobalPosition = GlobalPosition + new Vector2(Size.X + 8f, 0f);
        });
    }

    /// <summary>
    /// Build the contribution panel help dialog. Lists every metric the
    /// chart shows, the calculation formula, and the bar color legend.
    /// PRD §4.5 round 6 — must be self-contained (close button included).
    /// </summary>
    private PanelContainer BuildHelpPanel()
    {
        var pc = new PanelContainer { Name = "StatsTheSpireHelpPanel" };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.10f, 0.96f),
            BorderColor = new Color(0.4f, 0.5f, 0.7f, 0.6f),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 10, ContentMarginBottom = 12,
        };
        pc.AddThemeStyleboxOverride("panel", style);
        pc.CustomMinimumSize = new Vector2(440, 0);
        pc.MouseFilter = MouseFilterEnum.Stop;

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        pc.AddChild(v);

        // Header row with explicit close button
        var header = new HBoxContainer();
        var title = new Label { Text = L.Get("contrib.help_title") };
        title.AddThemeColorOverride("font_color", Colors.White);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        var closeBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(28, 28) };
        closeBtn.AddThemeFontSizeOverride("font_size", 12);
        closeBtn.Pressed += () => ToggleHelpPanel();
        header.AddChild(closeBtn);
        v.AddChild(header);

        v.AddChild(NewSeparator());

        // ── Color legend ────────────────────────────────────
        v.AddChild(NewLabel(L.Get("contrib.help_section_colors"), Gold, 12));
        v.AddChild(NewColorRow(new Color(0.36f, 0.58f, 0.95f), L.Get("contrib.help_color_card")));
        v.AddChild(NewColorRow(new Color(0.95f, 0.78f, 0.30f), L.Get("contrib.help_color_relic")));
        v.AddChild(NewColorRow(new Color(0.25f, 0.85f, 0.65f), L.Get("contrib.help_color_potion")));
        v.AddChild(NewColorRow(new Color(0.55f, 0.85f, 0.55f), L.Get("contrib.help_color_osty")));
        v.AddChild(NewColorRow(new Color(0.74f, 0.55f, 0.95f), L.Get("contrib.help_color_modifier")));
        v.AddChild(NewColorRow(new Color(0.55f, 0.78f, 1.00f), L.Get("contrib.help_color_attributed")));
        v.AddChild(NewColorRow(new Color(0.40f, 0.82f, 0.55f), L.Get("contrib.help_color_mitigate")));
        v.AddChild(NewColorRow(new Color(0.95f, 0.55f, 0.25f), L.Get("contrib.help_color_strdown")));
        v.AddChild(NewColorRow(new Color(0.92f, 0.30f, 0.25f), L.Get("contrib.help_color_self")));

        v.AddChild(NewSeparator());

        // ── Metric definitions ───────────────────────────────
        v.AddChild(NewLabel(L.Get("contrib.help_section_metrics"), Gold, 12));
        v.AddChild(NewLabel(L.Get("contrib.help_direct"),     Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_modifier"),   Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_attributed"), Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_block"),      Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_mitigated"),  Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_str_reduce"), Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_self"),       Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_energy"),     Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_heal"),       Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_draw"),       Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_stars"),      Cream, 11));

        v.AddChild(NewSeparator());

        // ── Footer notes ────────────────────────────────────
        v.AddChild(NewLabel(L.Get("contrib.help_dps"),     Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_pct"),     Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_source"),  Cream, 11));
        v.AddChild(NewLabel(L.Get("contrib.help_hotkeys"), Cream, 11));

        return pc;
    }

    private static readonly Color Gold  = new("#EFC851");
    private static readonly Color Cream = new("#FFF6E2");

    private static Label NewLabel(string text, Color color, int size)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.CustomMinimumSize = new Vector2(420, 0);
        return lbl;
    }

    private static HBoxContainer NewColorRow(Color swatch, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var swatchPanel = new Panel { CustomMinimumSize = new Vector2(20, 14) };
        var swatchStyle = new StyleBoxFlat
        {
            BgColor = swatch,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        swatchPanel.AddThemeStyleboxOverride("panel", swatchStyle);
        row.AddChild(swatchPanel);
        var lbl = new Label { Text = text };
        lbl.AddThemeColorOverride("font_color", Cream);
        lbl.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(lbl);
        return row;
    }

    private static HSeparator NewSeparator()
    {
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 6);
        return sep;
    }

    /// <summary>
    /// "每回合平均伤害" readout, switches dataset by the currently-visible tab.
    /// - 本场战斗 (tab 0): per-combat = CombatTracker.TotalDamageDealt /
    ///   TurnCount. Lives only for the currently-active combat; reset at
    ///   every OnCombatStart.
    /// - 本局汇总 (tab 1): cumulative = Σ run damage contributions /
    ///   Σ turnsTaken across every completed combat this run. Sourced from
    ///   RunContributionAggregator (survives SL via ContributionPersistence).
    ///
    /// 0 turns → show "—" instead of dividing. Previously used Math.Max(1, 0)
    /// which falsely displayed total damage as DPS when turn counter was 0.
    /// </summary>
    private void UpdateDps()
    {
        if (_dpsLabel == null) return;

        int turns;
        int damage;
        int tabIdx = _tabs?.CurrentTab ?? 0;
        if (tabIdx == 1)
        {
            // 本局汇总
            var agg = RunContributionAggregator.Instance;
            turns  = agg.TotalRunTurns;
            damage = agg.TotalRunDamage;
        }
        else
        {
            // 本场战斗
            var tracker = CombatTracker.Instance;
            turns  = tracker.TurnCount;
            damage = tracker.TotalDamageDealt;
        }

        string value = turns > 0
            ? ((float)damage / turns).ToString("F1")
            : "—";
        _dpsLabel.Text = $"{L.Get("contrib.dps")} {value}";
    }

    public static void ShowCombatResult(IReadOnlyDictionary<string, ContributionAccum>? combatData)
    {
        if (!ModConfig.Toggles.ContributionPanel) return;

        var panel = Instance;
        RefreshTabs(combatData);
        panel.UpdateDps();
        panel.Visible = true;
        panel.ApplyAvoidanceOffset();
    }

    /// <summary>
    /// Replay mode for the Run History "查看贡献图表" button. Shows ONLY the
    /// 本局汇总 tab populated with the persisted run summary; the live combat
    /// tab is hidden because there is no current combat to display
    /// (PRD §3.12 round 5).
    /// </summary>
    public static void ShowRunReplay(IReadOnlyDictionary<string, ContributionAccum>? runSummary)
    {
        // Round 9 round 33: ContributionPanel toggle now only gates the
        // auto-open after combat. Run-history replay button must always work.
        if (runSummary == null || runSummary.Count == 0) return;

        var panel = Instance;
        RefreshTabsRunOnly(runSummary);
        panel.UpdateDps();
        panel.Visible = true;
        panel.ApplyAvoidanceOffset();
    }

    public static void Toggle()
    {
        // Round 9 round 33: F8 hotkey ignores the toggle (which now only
        // controls post-combat auto-open).
        var panel = Instance;
        if (panel.Visible)
            panel.Visible = false;
        else
        {
            RefreshTabs(SelectCombatTabData());
            panel.UpdateDps();
            panel.Visible = true;
        }
    }

    public new static void Hide()
    {
        if (_instance != null)
            _instance.Visible = false;
    }

    /// <summary>
    /// When showing after combat end, if the rewards screen is already visible,
    /// shift the panel slightly to minimize overlap. Users can still drag freely.
    /// </summary>
    private void ApplyAvoidanceOffset()
    {
        Safe.Run(() =>
        {
            // Only apply if the user hasn't manually placed the panel yet
            if (ModConfig.PanelPositionX.HasValue) return;

            var root = GetTree()?.Root;
            if (root == null) return;

            // Best-effort search for a rewards screen node on the tree
            var rewardsVisible = FindRewardsScreen(root);
            if (rewardsVisible)
            {
                // Nudge panel upward slightly
                AnchorTop = 0.02f;
                AnchorBottom = 0.82f;
            }
        });
    }

    private static bool FindRewardsScreen(Node node)
    {
        if (node is Control ctrl && ctrl.Visible &&
            (node.Name.ToString().Contains("Reward") || node.Name.ToString().Contains("reward")))
            return true;
        foreach (var c in node.GetChildren())
            if (FindRewardsScreen(c)) return true;
        return false;
    }

    private static void RefreshTabs(IReadOnlyDictionary<string, ContributionAccum>? combatData)
    {
        var panel = Instance;
        var tabs = panel._tabs;
        if (tabs == null) return;

        // Round 8: keep the two ScrollContainer tab skeletons alive across
        // refreshes — only swap the chart content inside them. The previous
        // implementation tore down + re-created both tabs, which forced
        // Godot to reset the current tab and skipped the live re-layout
        // when the panel was already visible (real-time refresh bug).
        EnsureTabSkeletons(tabs);

        var currentTabIndex = tabs.CurrentTab;
        Safe.Info($"[ContribPanel] RefreshTabs: combatEntries={combatData?.Count ?? 0} currentTab={currentTabIndex}");

        var combatScroll = (ScrollContainer)tabs.GetChild(0);
        var runScroll    = (ScrollContainer)tabs.GetChild(1);

        // Build the new chart content (or empty placeholder).
        var newCombatContent = combatData != null && combatData.Count > 0
            ? (Control)ContributionChart.Create(combatData, BuildCombatTabTitle(combatData))
            : EmptyPlaceholder(L.Get("contrib.empty_combat"));

        var runData = RunContributionAggregator.Instance.RunTotals;
        var newRunContent = runData.Count > 0
            ? (Control)ContributionChart.Create(runData, L.Get("contrib.run_summary"), isRunLevel: true)
            : EmptyPlaceholder(L.Get("contrib.empty_run"));

        ReplaceScrollContent(combatScroll, newCombatContent);
        ReplaceScrollContent(runScroll, newRunContent);

        // Restore the tab the user was on so the refresh isn't disruptive.
        try { tabs.CurrentTab = currentTabIndex; } catch { }
    }

    /// <summary>
    /// Make sure the TabContainer has exactly two ScrollContainer children
    /// with the right titles. Idempotent — only creates them on first call.
    /// </summary>
    private static void EnsureTabSkeletons(TabContainer tabs)
    {
        // First call: build both skeletons.
        if (tabs.GetChildCount() < 2)
        {
            for (int i = tabs.GetChildCount() - 1; i >= 0; i--)
            {
                var child = tabs.GetChild(i);
                tabs.RemoveChild(child);
                child.QueueFree();
            }
            tabs.AddChild(NewTabScroll("CombatTab"));
            tabs.AddChild(NewTabScroll("RunTab"));
        }
        tabs.SetTabTitle(0, L.Get("contrib.this_combat"));
        tabs.SetTabTitle(1, L.Get("contrib.run_summary"));
    }

    private static ScrollContainer NewTabScroll(string name)
    {
        return new ScrollContainer
        {
            Name = name,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
    }

    private static void ReplaceScrollContent(ScrollContainer scroll, Control newContent)
    {
        // Remove + queue-free every existing child synchronously so the new
        // content is the sole child immediately, not next-frame.
        for (int i = scroll.GetChildCount() - 1; i >= 0; i--)
        {
            var child = scroll.GetChild(i);
            scroll.RemoveChild(child);
            child.QueueFree();
        }
        newContent.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(newContent);
    }

    private static string BuildCombatTabTitle(IReadOnlyDictionary<string, ContributionAccum>? combatData)
    {
        if (combatData == null || combatData.Count == 0)
            return L.Get("contrib.this_combat");

        var encId = CombatTracker.Instance.LastEncounterId ?? "";
        if (string.IsNullOrEmpty(encId))
            return L.Get("contrib.this_combat");

        try
        {
            var loc = new LocString("encounters", encId + ".title");
            var localized = loc.GetFormattedText();
            if (!string.IsNullOrEmpty(localized) && localized != encId + ".title")
                return $"{L.Get("contrib.vs")} {localized}";
        }
        catch { /* keep raw ID */ }
        return $"{L.Get("contrib.vs")} {encId}";
    }

    /// <summary>
    /// Round 5: Run-history replay variant. Drops every existing tab and
    /// re-creates only the 本局汇总 tab so the player isn't shown a stale
    /// "本场战斗" entry from a different run.
    /// </summary>
    private static void RefreshTabsRunOnly(IReadOnlyDictionary<string, ContributionAccum> runData)
    {
        var panel = Instance;
        var tabs = panel._tabs;
        if (tabs == null) return;

        for (int i = tabs.GetChildCount() - 1; i >= 0; i--)
        {
            var child = tabs.GetChild(i);
            tabs.RemoveChild(child);
            child.QueueFree();
        }

        var scroll = WrapInScroll(ContributionChart.Create(
            runData, L.Get("contrib.run_summary"), isRunLevel: true));
        tabs.AddChild(scroll);
        tabs.SetTabTitle(0, L.Get("contrib.run_summary"));
    }

    private static Control EmptyPlaceholder(string message)
    {
        var container = new VBoxContainer();
        container.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        container.SizeFlagsVertical = SizeFlags.ExpandFill;
        var lbl = new Label { Text = message };
        lbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.HorizontalAlignment = HorizontalAlignment.Center;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.SizeFlagsVertical = SizeFlags.ExpandFill;
        container.AddChild(lbl);
        return container;
    }

    private static ScrollContainer WrapInScroll(Control content)
    {
        // Round 9 round 51: explicitly disable horizontal scroll. The default
        // ScrollContainer enables horizontal scrolling whenever content > view,
        // and the run-replay tab was triggering this because the chart's
        // bar-row width hadn't been recomputed for the new wider panel until
        // the tab was opened. Mirror NewTabScroll which already does this.
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        // Make the chart content also stretch to fill horizontally so it
        // can use the full panel width.
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(content);
        return scroll;
    }
}
