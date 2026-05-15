using System.Linq;
using CommunityStats.Config;
using Godot;

namespace CommunityStats.UI;

/// <summary>
/// Compact corner indicator showing current potion drop odds.
/// Hovering expands an InfoModPanel with cumulative N-combat odds. PRD 3.9.
/// </summary>
public partial class PotionOddsIndicator : Control
{
    private static readonly Color GoldColor = new("#EFC851");
    private static readonly Color CreamColor = new("#FFF6E2");

    private Label _percentLabel = null!;
    private InfoModPanel? _hoverPanel;
    private float _currentOdds;

    public static PotionOddsIndicator Create()
    {
        var node = new PotionOddsIndicator
        {
            Name = "StatsTheSpirePotionOdds",
            MouseFilter = MouseFilterEnum.Stop,
            // Round 9 round 2: enlarged to match native top-bar buttons
            // (~56×56 icon + 18 px text + spacing → ~120×64 total).
            CustomMinimumSize = new Vector2(120, 64),
        };
        node.BuildUi();
        return node;
    }

    private void BuildUi()
    {
        // Round 9: HBox layout — icon on the left, percentage label to the
        // right (user feedback: previously percent was below the icon).
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        hbox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        AddChild(hbox);

        // Round 9: Use AttackPotion icon (was Distilled Chaos).
        Control iconNode;
        var icon = LoadNativeTexture(
            "atlases/potion_atlas.sprites/attack_potion.tres",
            "atlases/potion_atlas.sprites/attack_potion.png");
        if (icon != null)
        {
            iconNode = new TextureRect
            {
                Texture = icon,
                // Round 9 round 2: 40 → 56 to match native button size.
                CustomMinimumSize = new Vector2(56, 56),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore,
            };
        }
        else
        {
            var lbl = new Label { Text = "🧪" };
            lbl.AddThemeFontSizeOverride("font_size", 36);
            lbl.AddThemeColorOverride("font_color", GoldColor);
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            iconNode = lbl;
        }
        hbox.AddChild(iconNode);

        _percentLabel = new Label { Text = "—" };
        // Round 9 round 2: 12 → 18 to match native top-bar text scale.
        _percentLabel.AddThemeFontSizeOverride("font_size", 18);
        _percentLabel.AddThemeColorOverride("font_color", CreamColor);
        _percentLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _percentLabel.VerticalAlignment = VerticalAlignment.Center;
        _percentLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        hbox.AddChild(_percentLabel);

        MouseEntered += ShowHoverPanel;
        MouseExited += HideHoverPanel;
    }

    private static Texture2D? LoadNativeTexture(params string[] candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                var resolved = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetImagePath(path);
                if (Godot.ResourceLoader.Exists(resolved))
                    return Godot.ResourceLoader.Load<Texture2D>(resolved, null, ResourceLoader.CacheMode.Reuse);
            }
            catch { }
        }
        // Final fallback: try resources cached by PreloadManager.
        try
        {
            // Round 9: AttackPotion (was DISTILLED_CHAOS).
            var attack = MegaCrit.Sts2.Core.Models.ModelDb.AllPotions
                .FirstOrDefault(p => p.Id.Entry == "ATTACK_POTION");
            return attack?.Image;
        }
        catch { return null; }
    }

    public void UpdateOdds(float currentValue)
    {
        _currentOdds = Mathf.Clamp(currentValue, 0f, 1f);
        // PRD AC-15: percentages displayed with 1 decimal place.
        _percentLabel.Text = (_currentOdds * 100f).ToString("F1") + "%";
    }

    /// <summary>
    /// Cumulative probability of at least one drop over the next N combats,
    /// assuming each subsequent combat the pity adjusts +0.1 (miss path).
    /// </summary>
    public static float CumulativeOdds(float baseOdds, int combats, bool anyElite)
    {
        // Miss-path simulation: assume each combat failed, so pity grows by +0.1.
        // Elite rooms add +0.125 effective (eliteBonus 0.25 * 0.5).
        float missProduct = 1f;
        for (int i = 0; i < combats; i++)
        {
            float p = baseOdds + 0.1f * i + (anyElite ? 0.125f : 0f);
            p = Mathf.Clamp(p, 0f, 1f);
            missProduct *= 1f - p;
        }
        return 1f - missProduct;
    }

    private void ShowHoverPanel()
    {
        if (_hoverPanel != null) return;

        _hoverPanel = InfoModPanel.Create(L.Get("potion.title"), L.Get("potion.subtitle"));
        _hoverPanel.AddSeparator();

        // Round 9 round 2 PRD §3.9 #3: first row in the hover panel is the
        // CURRENT elite-fight drop probability. PotionRewardOdds.Roll uses
        // `currentOdds + eliteBonus * 0.5` where eliteBonus = 0.25, so the
        // effective elite probability is `currentOdds + 0.125`.
        float eliteOdds = Mathf.Clamp(_currentOdds + 0.125f, 0f, 1f);
        _hoverPanel.AddRow(L.Get("potion.elite"),
            (eliteOdds * 100f).ToString("F1") + "%");

        _hoverPanel.AddSeparator();

        // Gradient: light → deep blue across the 2..5 fight rows.
        var gradient = new[]
        {
            new Color(0.62f, 0.82f, 1.00f),
            new Color(0.42f, 0.68f, 0.98f),
            new Color(0.26f, 0.54f, 0.92f),
            new Color(0.14f, 0.38f, 0.82f),
        };
        for (int n = 2; n <= 5; n++)
        {
            var p = CumulativeOdds(_currentOdds, n, anyElite: false);
            var c = gradient[n - 2];
            _hoverPanel.AddRow(string.Format(L.Get("potion.within"), n),
                (p * 100f).ToString("F1") + "%", c, c);
        }

        AddChild(_hoverPanel);
        _hoverPanel.ZIndex = 500;
        _hoverPanel.GlobalPosition = GlobalPosition + new Vector2(0, Size.Y + 4f);
    }

    private void HideHoverPanel()
    {
        if (_hoverPanel == null) return;
        _hoverPanel.QueueFree();
        _hoverPanel = null;
    }
}
