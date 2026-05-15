using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;

namespace DevTools;

public partial class DevToolsPanel : PanelContainer
{
    private Label? _statusLabel;

    public DevToolsPanel()
    {
        Name = "DevToolsPanel";
        Visible = false;

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.1f, 0.15f, 0.95f),
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            ContentMarginLeft = 20, ContentMarginRight = 20,
            ContentMarginTop = 16, ContentMarginBottom = 16,
            BorderColor = new Color(0.4f, 0.6f, 1.0f, 0.5f),
            BorderWidthBottom = 2, BorderWidthLeft = 2,
            BorderWidthRight = 2, BorderWidthTop = 2
        };
        AddThemeStyleboxOverride("panel", style);

        // Center on screen
        AnchorLeft = 0.5f; AnchorRight = 0.5f;
        AnchorTop = 0.5f; AnchorBottom = 0.5f;
        OffsetLeft = -200; OffsetRight = 200;
        OffsetTop = -180; OffsetBottom = 180;
        CustomMinimumSize = new Vector2(400, 360);

        var vbox = new VBoxContainer();
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddThemeConstantOverride("separation", 10);
        AddChild(vbox);

        // Title
        var title = new Label { Text = "Dev Tools (F7)" };
        title.AddThemeColorOverride("font_color", new Color(0.5f, 0.8f, 1.0f));
        title.AddThemeFontSizeOverride("font_size", 18);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var sep = new HSeparator();
        vbox.AddChild(sep);

        // Buttons
        AddButton(vbox, "Unlock All Characters", OnUnlockCharacters);
        AddButton(vbox, "Unlock All Cards / Relics / Potions", OnUnlockContent);
        AddButton(vbox, "Unlock All Ascensions (A10)", OnUnlockAscensions);
        AddButton(vbox, "Unlock All Timelines", OnUnlockTimelines);

        var sep2 = new HSeparator();
        vbox.AddChild(sep2);

        AddButton(vbox, "Unlock Everything", OnUnlockAll, highlight: true);

        // Status
        _statusLabel = new Label { Text = "" };
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.5f, 1.0f, 0.5f));
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
        vbox.AddChild(_statusLabel);

        // Close button
        var closeBtn = new Button { Text = "Close" };
        closeBtn.Pressed += () => Visible = false;
        vbox.AddChild(closeBtn);
    }

    private void AddButton(VBoxContainer parent, string text, Action onClick, bool highlight = false)
    {
        var btn = new Button { Text = text };
        btn.CustomMinimumSize = new Vector2(0, 36);
        if (highlight)
        {
            var stylebox = new StyleBoxFlat
            {
                BgColor = new Color(0.2f, 0.4f, 0.8f, 0.8f),
                CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
                ContentMarginLeft = 8, ContentMarginRight = 8,
                ContentMarginTop = 4, ContentMarginBottom = 4
            };
            btn.AddThemeStyleboxOverride("normal", stylebox);
        }
        btn.Pressed += onClick;
        parent.AddChild(btn);
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible && _statusLabel != null)
            _statusLabel.Text = "";
    }

    private void SetStatus(string msg)
    {
        if (_statusLabel != null)
            _statusLabel.Text = msg;
        GD.Print($"[DevTools] {msg}");
    }

    private void OnUnlockCharacters()
    {
        try
        {
            var progress = SaveManager.Instance.Progress;
            // Character epochs: Silent, Regent, Necrobinder, Defect
            string[] charEpochs = { "SILENT1_EPOCH", "REGENT1_EPOCH", "NECROBINDER1_EPOCH", "DEFECT1_EPOCH" };
            int count = 0;
            foreach (var epochId in charEpochs)
            {
                var epoch = progress.Epochs.FirstOrDefault(e => e.Id == epochId);
                if (epoch == null || epoch.State != EpochState.Revealed)
                {
                    SaveManager.Instance.ObtainEpochOverride(epochId, EpochState.Revealed);
                    count++;
                }
            }
            SaveManager.Instance.SaveProgressFile();
            SetStatus(count > 0
                ? $"Unlocked {count} characters. Restart to apply."
                : "All characters already unlocked.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void OnUnlockContent()
    {
        try
        {
            // Reveal all epochs (this covers card, relic, potion unlock epochs)
            int count = 0;
            foreach (var epochId in EpochModel.AllEpochIds)
            {
                var epoch = SaveManager.Instance.Progress.Epochs.FirstOrDefault(e => e.Id == epochId);
                if (epoch == null || epoch.State != EpochState.Revealed)
                {
                    SaveManager.Instance.ObtainEpochOverride(epochId, EpochState.Revealed);
                    count++;
                }
            }

            // Also mark all cards/relics/potions as seen in compendium
            foreach (var card in ModelDb.AllCards)
                SaveManager.Instance.Progress.MarkCardAsSeen(card.Id);
            foreach (var relic in ModelDb.AllRelics)
                SaveManager.Instance.Progress.MarkRelicAsSeen(relic.Id);
            foreach (var potion in ModelDb.AllPotions)
                SaveManager.Instance.Progress.MarkPotionAsSeen(potion.Id);

            SaveManager.Instance.SaveProgressFile();
            SetStatus(count > 0
                ? $"Unlocked {count} epochs + marked all seen. Restart to apply."
                : "All content already unlocked.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void OnUnlockAscensions()
    {
        try
        {
            var progress = SaveManager.Instance.Progress;
            progress.MaxMultiplayerAscension = 10;
            foreach (var character in ModelDb.AllCharacters)
            {
                var stats = progress.GetOrCreateCharacterStats(character.Id);
                stats.MaxAscension = 10;
            }
            SaveManager.Instance.SaveProgressFile();
            SetStatus("All ascensions set to A10. Restart to apply.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void OnUnlockTimelines()
    {
        try
        {
            int count = 0;
            foreach (var epochId in EpochModel.AllEpochIds)
            {
                var epoch = SaveManager.Instance.Progress.Epochs.FirstOrDefault(e => e.Id == epochId);
                if (epoch == null || epoch.State != EpochState.Revealed)
                {
                    SaveManager.Instance.ObtainEpochOverride(epochId, EpochState.Revealed);
                    count++;
                }
            }
            SaveManager.Instance.SaveProgressFile();
            SetStatus(count > 0
                ? $"Revealed {count} timeline epochs. Restart to apply."
                : "All timelines already unlocked.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void OnUnlockAll()
    {
        try
        {
            // 1. All epochs
            int epochCount = 0;
            foreach (var epochId in EpochModel.AllEpochIds)
            {
                var epoch = SaveManager.Instance.Progress.Epochs.FirstOrDefault(e => e.Id == epochId);
                if (epoch == null || epoch.State != EpochState.Revealed)
                {
                    SaveManager.Instance.ObtainEpochOverride(epochId, EpochState.Revealed);
                    epochCount++;
                }
            }

            // 2. All ascensions
            var progress = SaveManager.Instance.Progress;
            progress.MaxMultiplayerAscension = 10;
            foreach (var character in ModelDb.AllCharacters)
            {
                var stats = progress.GetOrCreateCharacterStats(character.Id);
                stats.MaxAscension = 10;
            }

            // 3. Mark all compendium entries as seen
            foreach (var card in ModelDb.AllCards)
                progress.MarkCardAsSeen(card.Id);
            foreach (var relic in ModelDb.AllRelics)
                progress.MarkRelicAsSeen(relic.Id);
            foreach (var potion in ModelDb.AllPotions)
                progress.MarkPotionAsSeen(potion.Id);
            foreach (var monster in ModelDb.Monsters)
            {
                var enemyStats = progress.GetOrCreateEnemyStats(monster.Id);
                if (enemyStats.FightStats.Count == 0)
                    enemyStats.FightStats.Add(new FightStats
                    {
                        Character = new ModelId("CHARACTER", "IRONCLAD"),
                        Wins = 1
                    });
            }
            foreach (var evt in ModelDb.AllEvents)
                progress.MarkEventAsSeen(evt.Id);

            SaveManager.Instance.SaveProgressFile();
            SetStatus($"Unlocked everything ({epochCount} epochs + A10 + compendium). Restart to apply.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }
}
