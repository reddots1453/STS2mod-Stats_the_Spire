using System.Security.Cryptography;
using System.Text;
using CommunityStats.Collection;
using CommunityStats.Config;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;

namespace CommunityStats.Api;

/// <summary>
/// PRD §3.19: One-time import of local RunHistory files into the server.
/// On first launch shows a consent dialog; runs entirely in the background.
/// After import, triggers RunHistoryAnalyzer refresh to populate CareerStats.
/// </summary>
public static class HistoryImporter
{
    private const string ModVersionTag = "history_import";

    // Progress label instance, attached to scene root
    private static ImportProgressLabel? _progressLabel;

    /// <summary>
    /// True while the history import worker is actively running. UploadNotice
    /// uses this to suppress its own top-right toast so the two labels do not
    /// overlap at the same screen coordinates.
    /// </summary>
    public static bool IsRunning { get; private set; }

    /// <summary>
    /// Checks whether import has already been completed. If not, waits for
    /// the scene tree, shows a consent dialog, and kicks off background import.
    /// </summary>
    public static void TryImportAsync()
    {
        Safe.Info($"[HistoryImporter] TryImportAsync called, HistoryImportCompleted={ModConfig.HistoryImportCompleted}");
        if (ModConfig.HistoryImportCompleted)
        {
            Safe.Info("[HistoryImporter] Already completed, skipping");
            return;
        }

        Safe.Info("[HistoryImporter] Starting async wait for scene tree...");
        Safe.RunAsync(async () =>
        {
            try
            {
                // Wait for scene tree + SaveManager
                SceneTree? tree = null;
                for (int i = 0; i < 150; i++)
                {
                    tree = Engine.GetMainLoop() as SceneTree;
                    if (tree?.Root != null && SaveManager.Instance != null)
                    {
                        Safe.Info($"[HistoryImporter] Scene tree ready after {i} polls");
                        break;
                    }
                    await Task.Delay(200);
                }

                if (tree?.Root == null)
                {
                    Safe.Warn("[HistoryImporter] Scene tree never ready, aborting");
                    return;
                }
                if (SaveManager.Instance == null)
                {
                    Safe.Warn("[HistoryImporter] SaveManager never initialized, aborting");
                    return;
                }

                // Show consent dialog on main thread and await user choice
                Safe.Info("[HistoryImporter] About to show consent dialog...");
                bool userAccepted = await ShowConsentDialogAsync(tree.Root);
                Safe.Info($"[HistoryImporter] User choice: {(userAccepted ? "upload" : "skip")}");

                if (!userAccepted)
                {
                    Safe.Info("[HistoryImporter] User skipped history import");
                    ModConfig.HistoryImportCompleted = true;
                    ModConfig.SaveSettings();
                    return;
                }

                // Attach progress label
                AttachProgressLabel(tree.Root);

                IsRunning = true;
                try
                {
                    await ImportAllAsync();
                }
                finally
                {
                    IsRunning = false;
                }

                // Mark completed
                ModConfig.HistoryImportCompleted = true;
                ModConfig.SaveSettings();
                Safe.Info("[HistoryImporter] Import completed, flag saved");

                // Refresh CareerStats so the data loads without redundant file IO
                Safe.Info("[HistoryImporter] Triggering RunHistoryAnalyzer refresh...");
                RunHistoryAnalyzer.Instance.InvalidateAll();
                await RunHistoryAnalyzer.Instance.LoadAllAsync(null, force: true);
                Safe.Info("[HistoryImporter] CareerStats refresh done");
            }
            catch (Exception ex)
            {
                Safe.Warn($"[HistoryImporter] Import failed: {ex.Message}");
            }
        });
    }

    private static Task<bool> ShowConsentDialogAsync(Node root)
    {
        var dialog = HistoryImportDialog.Create();
        // Add directly to root — same as FilterPanel
        root.CallDeferred(Node.MethodName.AddChild, dialog);
        return dialog.ResultTask;
    }

    private static void AttachProgressLabel(Node root)
    {
        _progressLabel = ImportProgressLabel.Create();
        root.CallDeferred(Node.MethodName.AddChild, _progressLabel);
    }

    private static void UpdateProgress(int current, int total)
    {
        var label = _progressLabel;
        if (label == null) return;
        // Use SetDeferred for the Text property + Visible (both are Godot properties)
        label.SetDeferred("text", string.Format(L.Get("import.progress"), current, total));
        label.SetDeferred("visible", true);
    }

    private static void ShowDone(int count)
    {
        var label = _progressLabel;
        if (label == null) return;
        label.SetDeferred("text", string.Format(L.Get("import.progress_done"), count));
        label.SetDeferred("visible", true);
        // Custom C# methods aren't in Godot's script method table,
        // so CallDeferred(name) won't resolve them. Marshal via
        // Callable.From so the arm fires on the main thread.
        var armLabel = label;
        Callable.From(() => Safe.Run(armLabel.ArmHideTimer)).CallDeferred();
    }

    private static async Task ImportAllAsync()
    {
        // Load all run history file names
        List<string> names;
        try
        {
            names = SaveManager.Instance!.GetAllRunHistoryNames() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Safe.Warn($"[HistoryImporter] GetAllRunHistoryNames failed: {ex.Message}");
            return;
        }

        Safe.Info($"[HistoryImporter] Found {names.Count} history files, converting...");
        if (names.Count == 0)
        {
            ShowDone(0);
            return;
        }

        int success = 0;
        int skipped = 0;
        int errors = 0;
        int processed = 0;

        // Pre-load + filter all histories so we can sort by StartTime and walk
        // them in chronological order. This lets us compute a retrospective
        // player_win_rate per run = wins_so_far / total_so_far, matching the
        // cumulative win rate the live BuildPayload would have emitted.
        var staged = new List<RunHistory>(names.Count);
        foreach (var name in names)
        {
            RunHistory history;
            try
            {
                var result = SaveManager.Instance!.LoadRunHistory(name);
                if (result == null || !result.Success || result.SaveData == null)
                {
                    Safe.Info($"[HistoryImporter] Skip '{name}': load failed (result={result != null}, success={result?.Success}, data={result?.SaveData != null})");
                    skipped++;
                    continue;
                }
                history = result.SaveData;
            }
            catch (Exception ex)
            {
                Safe.Warn($"[HistoryImporter] Failed to load '{name}': {ex.Message}");
                errors++;
                continue;
            }

            if (history.GameMode != GameMode.Standard)
            {
                Safe.Info($"[HistoryImporter] Skip '{name}': GameMode={history.GameMode} (not Standard)");
                skipped++;
                continue;
            }
            if (history.Players == null || history.Players.Count != 1)
            {
                Safe.Info($"[HistoryImporter] Skip '{name}': Players={history.Players?.Count ?? 0} (not single-player)");
                skipped++;
                continue;
            }

            staged.Add(history);
        }

        staged.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        Safe.Info($"[HistoryImporter] Staged {staged.Count} runs for upload (chronological order)");

        int winsSoFar = 0;
        int totalSoFar = 0;

        foreach (var history in staged)
        {
            totalSoFar++;
            if (history.Win) winsSoFar++;
            float retroWinRate = totalSoFar > 0 ? (float)winsSoFar / totalSoFar : 0f;

            RunUploadPayload? payload;
            try
            {
                payload = ConvertRun(history, retroWinRate);
            }
            catch (Exception ex)
            {
                Safe.Warn($"[HistoryImporter] Convert error: {ex.Message}");
                errors++;
                continue;
            }

            if (payload == null)
            {
                Safe.Info("[HistoryImporter] Skip: ConvertRun returned null");
                skipped++;
                continue;
            }

            try
            {
                await ApiClient.Instance.UploadRunAsync(payload);
                success++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                    Safe.Warn($"[HistoryImporter] Upload error: {ex.Message}");
            }

            processed++;
            UpdateProgress(processed, staged.Count);

            // Server rate limit: 10 uploads/min/IP (nginx + slowapi).
            // Burst=5 is absorbed by the first few requests; after that
            // we must pace at roughly one per 6.5 s sustained.
            if (processed % 10 == 0)
                Safe.Info($"[HistoryImporter] Progress: {success} uploaded, {skipped} skipped, {errors} errors, wr_so_far={retroWinRate:F2}");

            if (processed < staged.Count)
                await Task.Delay(6500);
        }

        Safe.Info($"[HistoryImporter] Done: {success} uploaded, {skipped} skipped, {errors} errors (total={names.Count})");
        ShowDone(success);
    }

    /// <summary>
    /// Convert a RunHistory object to a RunUploadPayload.
    /// <paramref name="retroWinRate"/> is the player's cumulative win rate
    /// across all runs that preceded this one chronologically — computed by
    /// <see cref="ImportAllAsync"/> after sorting by StartTime.
    /// Returns null if the run cannot be converted.
    /// </summary>
    private static RunUploadPayload? ConvertRun(RunHistory history, float retroWinRate = 0f)
    {
        var player = history.Players[0];
        var characterId = player.Character?.Entry;
        if (string.IsNullOrEmpty(characterId)) return null;

        var payload = new RunUploadPayload
        {
            ModVersion = ModVersionTag,
            GameVersion = history.BuildId ?? "unknown",
            Character = characterId!,
            Ascension = history.Ascension,
            Win = history.Win,
            PlayerWinRate = retroWinRate,
            NumPlayers = 1,
            RunHash = ComputeHash(history),
            Branch = "unknown",
        };

        // ── Shop purchases (prefer our persistence over MapHistory) ──
        // If the run was recorded live by an earlier session of this mod,
        // we have exact card IDs (including colored cards, which game-side
        // BoughtColorless misses) and real costs. Only fall back to
        // MapHistory for runs predating the mod or where the file was
        // pruned by the 90-day sweep.
        var persistedPurchases = ShopPurchasePersistence.Load(history.Seed);
        bool havePersisted = persistedPurchases != null && persistedPurchases.Count > 0;
        if (havePersisted)
            payload.ShopPurchases.AddRange(persistedPurchases!);

        // ── Walk map_point_history ─────────────────────────
        int floor = PopulateFromMapHistory(
            history.MapPointHistory,
            history.KilledByEncounter?.Entry,
            history.Win,
            payload,
            includeShopPurchases: !havePersisted);

        payload.FloorReached = floor;

        // ── Final deck ─────────────────────────────────────
        if (player.Deck != null)
        {
            foreach (var card in player.Deck)
            {
                var cardId = card.Id?.Entry;
                if (!string.IsNullOrEmpty(cardId))
                    payload.FinalDeck.Add(new DeckCardUpload
                    {
                        CardId = cardId!,
                        UpgradeLevel = card.CurrentUpgradeLevel,
                    });
            }
        }

        // ── Final relics ───────────────────────────────────
        if (player.Relics != null)
        {
            foreach (var relic in player.Relics)
            {
                var relicId = relic.Id?.Entry;
                if (!string.IsNullOrEmpty(relicId))
                    payload.FinalRelics.Add(relicId!);
            }
        }

        return payload;
    }

    /// <summary>
    /// Walk a `List&lt;List&lt;MapPointHistoryEntry&gt;&gt;` and populate the
    /// CardChoices / EventChoices / CardUpgrades / CardRemovals /
    /// ShopPurchases / Encounters sections of the supplied payload. Returns
    /// the total floor count (sum of visited map points across all acts).
    ///
    /// Round 14 v5+: shared between <see cref="ConvertRun"/> (historical
    /// imports) and <see cref="CommunityStats.Collection.RunDataCollector"/>
    /// (live run upload). Live runs previously relied on in-memory lists
    /// populated by Harmony Record*() patches, but those lists are lost on
    /// save+quit reload; the game's own MapPointHistory survives reload and
    /// is the authoritative source.
    /// </summary>
    public static int PopulateFromMapHistory(
        List<List<MapPointHistoryEntry>>? mapHistory,
        string? killedByEncounter,
        bool wasWin,
        RunUploadPayload payload,
        bool includeShopPurchases = true)
    {
        int floor = 0;
        int diagNodes = 0, diagPlayerStats = 0, diagBoughtRelics = 0,
            diagBoughtPotions = 0, diagBoughtColorless = 0;
        if (mapHistory == null) return 0;

        foreach (var act in mapHistory)
        {
            if (act == null) continue;
            foreach (var node in act)
            {
                if (node == null) continue;
                floor++;
                diagNodes++;

                var ps = node.PlayerStats?.Count > 0 ? node.PlayerStats[0] : null;
                if (ps != null) diagPlayerStats++;
                if (ps?.BoughtRelics != null) diagBoughtRelics += ps.BoughtRelics.Count;
                if (ps?.BoughtPotions != null) diagBoughtPotions += ps.BoughtPotions.Count;
                if (ps?.BoughtColorless != null) diagBoughtColorless += ps.BoughtColorless.Count;

                // ── Card choices ────────────────────
                if (ps?.CardChoices != null)
                {
                    foreach (var cc in ps.CardChoices)
                    {
                        var cardId = cc.Card.Id?.Entry;
                        if (string.IsNullOrEmpty(cardId)) continue;
                        payload.CardChoices.Add(new CardChoiceUpload
                        {
                            CardId = cardId!,
                            UpgradeLevel = cc.Card.CurrentUpgradeLevel,
                            WasPicked = cc.wasPicked,
                            Floor = Math.Min(cc.Card.FloorAddedToDeck ?? floor, 200),
                        });
                    }
                }

                // ── Event choices (regular events) ──
                if (ps?.EventChoices != null)
                {
                    foreach (var ec in ps.EventChoices)
                    {
                        var locKey = ec.Title?.LocEntryKey;
                        if (string.IsNullOrEmpty(locKey)) continue;

                        var eventId = ExtractEventId(locKey);
                        if (string.IsNullOrEmpty(eventId)) continue;

                        if (eventId == "COLORFUL_PHILOSOPHERS")
                        {
                            var color = ExtractOptionName(locKey!);
                            if (!string.IsNullOrEmpty(color))
                            {
                                payload.EventChoices.Add(new EventChoiceUpload
                                {
                                    EventId = eventId,
                                    OptionIndex = -1,
                                    TotalOptions = 0,
                                    ChosenOptionId = color!,
                                });
                            }
                            continue;
                        }

                        if (SkippedEvents.Contains(eventId))
                            continue;

                        var pageName = ExtractPageName(locKey!);
                        var optionName = ExtractOptionName(locKey!);
                        if (string.IsNullOrEmpty(pageName) || string.IsNullOrEmpty(optionName))
                            continue;

                        var resolved = ResolveEventOption(eventId!, pageName!, optionName!);
                        if (resolved != null)
                        {
                            payload.EventChoices.Add(new EventChoiceUpload
                            {
                                EventId = eventId!,
                                OptionIndex = resolved.Value.index,
                                TotalOptions = resolved.Value.total,
                            });
                        }
                    }
                }

                // ── Ancient event choices (combo-based) ──
                if (ps?.AncientChoices != null && ps.AncientChoices.Count >= 2)
                {
                    ProcessAncientChoices(ps.AncientChoices, payload);
                }

                // ── Card upgrades ──────────────────
                if (ps?.UpgradedCards != null)
                {
                    foreach (var upgraded in ps.UpgradedCards)
                    {
                        var cardId = upgraded?.Entry;
                        if (string.IsNullOrEmpty(cardId)) continue;

                        var mpt = node.MapPointType;
                        string source = mpt == MapPointType.RestSite ? "campfire"
                                      : mpt == MapPointType.Unknown ? "event"
                                      : "other";

                        payload.CardUpgrades.Add(new CardUpgradeUpload
                        {
                            CardId = cardId!,
                            Source = source,
                        });
                    }
                }

                // ── Card removals ──────────────────
                if (ps?.CardsRemoved != null)
                {
                    foreach (var removed in ps.CardsRemoved)
                    {
                        var cardId = removed.Id?.Entry;
                        if (string.IsNullOrEmpty(cardId)) continue;

                        var mpt = node.MapPointType;
                        string source = mpt == MapPointType.Shop ? "shop" : "event";

                        payload.CardRemovals.Add(new CardRemovalUpload
                        {
                            CardId = cardId!,
                            Source = source,
                            Floor = floor,
                        });
                    }
                }

                // ── Shop purchases ─────────────────
                // Gate behind includeShopPurchases: callers that pre-populate
                // payload.ShopPurchases from an authoritative source (our
                // ShopPatch-driven _shopPurchases list) pass false to avoid
                // a) duplicating entries, and b) overriding their real Cost
                // with 0. The MapHistory path is lossy:
                //  - BoughtColorless misses colored card buys (they're
                //    tracked only via the merchant entry hook)
                //  - Cost is never captured, so values are all 0.
                if (includeShopPurchases)
                {
                    if (ps?.BoughtRelics != null)
                    {
                        foreach (var relicId in ps.BoughtRelics)
                        {
                            var rid = relicId?.Entry;
                            if (!string.IsNullOrEmpty(rid))
                                payload.ShopPurchases.Add(new ShopPurchaseUpload
                                    { ItemId = rid!, ItemType = "relic", Cost = 0, Floor = floor });
                        }
                    }
                    if (ps?.BoughtPotions != null)
                    {
                        foreach (var potionId in ps.BoughtPotions)
                        {
                            var pid = potionId?.Entry;
                            if (!string.IsNullOrEmpty(pid))
                                payload.ShopPurchases.Add(new ShopPurchaseUpload
                                    { ItemId = pid!, ItemType = "potion", Cost = 0, Floor = floor });
                        }
                    }
                    if (ps?.BoughtColorless != null)
                    {
                        foreach (var cardId in ps.BoughtColorless)
                        {
                            var cid = cardId?.Entry;
                            if (!string.IsNullOrEmpty(cid))
                                payload.ShopPurchases.Add(new ShopPurchaseUpload
                                    { ItemId = cid!, ItemType = "card", Cost = 0, Floor = floor });
                        }
                    }
                }

                // ── Encounters ─────────────────────
                if (node.Rooms != null)
                {
                    foreach (var room in node.Rooms)
                    {
                        var encType = RoomTypeToEncounterType(room.RoomType);
                        if (encType == null) continue;

                        var encId = room.ModelId?.Entry;
                        if (string.IsNullOrEmpty(encId)) continue;

                        bool died = !wasWin
                                    && !string.IsNullOrEmpty(killedByEncounter)
                                    && killedByEncounter == encId;

                        payload.Encounters.Add(new EncounterUpload
                        {
                            EncounterId = encId!,
                            EncounterType = encType,
                            DamageTaken = Math.Min(ps?.DamageTaken ?? 0, 999999),
                            TurnsTaken = Math.Min(room.TurnsTaken, 999),
                            PlayerDied = died,
                            Floor = floor,
                        });
                    }
                }
            }
        }

        Safe.Info($"[PopulateFromMapHistory] nodes={diagNodes} playerStats={diagPlayerStats} " +
                  $"boughtRelics={diagBoughtRelics} boughtPotions={diagBoughtPotions} boughtColorless={diagBoughtColorless} " +
                  $"→ payload.ShopPurchases={payload.ShopPurchases.Count}");

        return floor;
    }

    // ── Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Extract event ID from localization key.
    /// e.g. "BYRDONIS_NEST.pages.INITIAL.options.TAKE.title" → "BYRDONIS_NEST"
    /// </summary>
    private static string? ExtractEventId(string locKey)
    {
        if (string.IsNullOrEmpty(locKey)) return null;
        if (!locKey.Contains(".pages.") || !locKey.Contains(".options.")) return null;
        var idx = locKey.IndexOf(".pages.", StringComparison.Ordinal);
        return idx > 0 ? locKey[..idx] : null;
    }

    /// <summary>
    /// Extract page name from localization key.
    /// e.g. "TRIAL.pages.MERCHANT.options.GUILTY.title" → "MERCHANT"
    /// </summary>
    private static string? ExtractPageName(string locKey)
    {
        var pagesIdx = locKey.IndexOf(".pages.", StringComparison.Ordinal);
        if (pagesIdx < 0) return null;
        var afterPages = locKey[(pagesIdx + 7)..]; // skip ".pages."
        var dotIdx = afterPages.IndexOf('.');
        return dotIdx > 0 ? afterPages[..dotIdx] : afterPages;
    }

    /// <summary>
    /// Extract option name from localization key.
    /// e.g. "BYRDONIS_NEST.pages.INITIAL.options.TAKE.title" → "TAKE"
    /// </summary>
    private static string? ExtractOptionName(string locKey)
    {
        var optIdx = locKey.IndexOf(".options.", StringComparison.Ordinal);
        if (optIdx < 0) return null;
        var afterOptions = locKey[(optIdx + 9)..]; // skip ".options."
        var dotIdx = afterOptions.IndexOf('.');
        return dotIdx > 0 ? afterOptions[..dotIdx] : afterOptions;
    }

    // Events to skip entirely — no meaningful option distinction
    private static readonly HashSet<string> SkippedEvents = new()
    {
        "THE_FUTURE_OF_POTIONS",
    };

    /// <summary>
    /// Process ancient event choices (方案B: combo-based stats).
    /// AncientChoiceHistoryEntry records ALL presented options with WasChosen flag.
    /// We build a combo_key from sorted option IDs and track the chosen one.
    /// </summary>
    private static void ProcessAncientChoices(
        System.Collections.Generic.List<AncientChoiceHistoryEntry> choices,
        RunUploadPayload payload)
    {
        // Extract event ID from the first choice's loc key
        string? eventId = null;
        var optionIds = new List<string>(choices.Count);
        string? chosenOptionId = null;

        foreach (var choice in choices)
        {
            var locKey = choice.Title?.LocEntryKey;
            if (string.IsNullOrEmpty(locKey)) continue;

            if (eventId == null)
                eventId = ExtractEventId(locKey);

            var optionName = ExtractOptionName(locKey!);
            if (string.IsNullOrEmpty(optionName)) continue;

            optionIds.Add(optionName!);
            if (choice.WasChosen)
                chosenOptionId = optionName;
        }

        if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(chosenOptionId) || optionIds.Count < 2)
            return;

        // Sort option IDs alphabetically and join with | to form combo_key
        optionIds.Sort(StringComparer.Ordinal);
        var comboKey = string.Join("|", optionIds);

        payload.EventChoices.Add(new EventChoiceUpload
        {
            EventId = eventId!,
            OptionIndex = -1,
            TotalOptions = optionIds.Count,
            ComboKey = comboKey,
            ChosenOptionId = chosenOptionId!,
        });
    }

    // ── Event option index mapping ─────────────────────────────
    // Built from decompiled GenerateInitialOptions() for each event.
    // Key: "EVENT_ID|PAGE" → string[] of option names in index order.
    private static readonly Dictionary<string, string[]> EventOptionMap = new()
    {
        ["ABYSSAL_BATHS|INITIAL"] = ["IMMERSE", "ABSTAIN"],
        ["ABYSSAL_BATHS|ALL"] = ["LINGER", "EXIT_BATHS"],
        ["AROMA_OF_CHAOS|INITIAL"] = ["LET_GO", "MAINTAIN_CONTROL"],
        ["BATTLEWORN_DUMMY|INITIAL"] = ["SETTING_1", "SETTING_2", "SETTING_3"],
        ["BRAIN_LEECH|INITIAL"] = ["SHARE_KNOWLEDGE", "RIP"],
        ["BUGSLAYER|INITIAL"] = ["EXTERMINATION", "SQUASH"],
        ["BYRDONIS_NEST|INITIAL"] = ["EAT", "TAKE"],
        ["COLOSSAL_FLOWER|INITIAL"] = ["EXTRACT_CURRENT_PRIZE_1", "REACH_DEEPER_1"],
        ["CRYSTAL_SPHERE|INITIAL"] = ["UNCOVER_FUTURE", "PAYMENT_PLAN"],
        ["DENSE_VEGETATION|INITIAL"] = ["TRUDGE_ON", "REST"],
        ["DENSE_VEGETATION|REST"] = ["FIGHT"],
        ["DOLL_ROOM|INITIAL"] = ["RANDOM", "TAKE_SOME_TIME", "EXAMINE"],
        ["DOORS_OF_LIGHT_AND_DARK|INITIAL"] = ["LIGHT", "DARK"],
        ["DROWNING_BEACON|INITIAL"] = ["BOTTLE", "CLIMB"],
        ["FIELD_OF_MAN_SIZED_HOLES|INITIAL"] = ["RESIST", "ENTER_YOUR_HOLE"],
        ["GRAVE_OF_THE_FORGOTTEN|INITIAL"] = ["CONFRONT", "ACCEPT"],
        ["HUNGRY_FOR_MUSHROOMS|INITIAL"] = ["BIG_MUSHROOM", "FRAGRANT_MUSHROOM"],
        ["INFESTED_AUTOMATON|INITIAL"] = ["STUDY", "TOUCH_CORE"],
        ["JUNGLE_MAZE_ADVENTURE|INITIAL"] = ["SOLO_QUEST", "JOIN_FORCES"],
        ["LOST_WISP|INITIAL"] = ["CLAIM", "SEARCH"],
        ["LUMINOUS_CHOIR|INITIAL"] = ["REACH_INTO_THE_FLESH", "OFFER_TRIBUTE"],
        ["MORPHIC_GROVE|INITIAL"] = ["GROUP", "LONER"],
        ["POTION_COURIER|INITIAL"] = ["GRAB_POTIONS", "RANSACK"],
        ["PUNCH_OFF|INITIAL"] = ["NAB", "I_CAN_TAKE_THEM"],
        ["PUNCH_OFF|I_CAN_TAKE_THEM"] = ["FIGHT"],
        ["RANWID_THE_ELDER|INITIAL"] = ["POTION", "GOLD", "RELIC"],
        ["REFLECTIONS|INITIAL"] = ["TOUCH_A_MIRROR", "SHATTER"],
        ["RELIC_TRADER|INITIAL"] = ["TOP", "MIDDLE", "BOTTOM"],
        ["ROOM_FULL_OF_CHEESE|INITIAL"] = ["GORGE", "SEARCH"],
        ["ROUND_TEA_PARTY|INITIAL"] = ["ENJOY_TEA", "PICK_FIGHT"],
        ["SAPPHIRE_SEED|INITIAL"] = ["EAT", "PLANT"],
        ["SELF_HELP_BOOK|INITIAL"] = ["READ_THE_BACK", "READ_PASSAGE", "READ_ENTIRE_BOOK"],
        ["SPIRALING_WHIRLPOOL|INITIAL"] = ["OBSERVE", "DRINK"],
        ["SPIRIT_GRAFTER|INITIAL"] = ["LET_IT_IN", "REJECTION"],
        ["STONE_OF_ALL_TIME|INITIAL"] = ["LIFT", "PUSH"],
        ["SUNKEN_STATUE|INITIAL"] = ["GRAB_SWORD", "DIVE_INTO_WATER"],
        ["SUNKEN_TREASURY|INITIAL"] = ["FIRST_CHEST", "SECOND_CHEST"],
        ["SYMBIOTE|INITIAL"] = ["APPROACH", "KILL_WITH_FIRE"],
        ["TEA_MASTER|INITIAL"] = ["BONE_TEA", "EMBER_TEA", "TEA_OF_DISCOURTESY"],
        ["THE_LANTERN_KEY|INITIAL"] = ["RETURN_THE_KEY", "KEEP_THE_KEY"],
        ["THE_LANTERN_KEY|KEEP_THE_KEY"] = ["FIGHT"],
        ["THE_LEGENDS_WERE_TRUE|INITIAL"] = ["NAB_THE_MAP", "SLOWLY_FIND_AN_EXIT"],
        ["THIS_OR_THAT|INITIAL"] = ["PLAIN", "ORNATE"],
        ["TINKER_TIME|INITIAL"] = ["CHOOSE_CARD_TYPE"],
        ["TINKER_TIME|CHOOSE_CARD_TYPE"] = ["ATTACK", "SKILL", "POWER"],
        ["TRASH_HEAP|INITIAL"] = ["DIVE_IN", "GRAB"],
        ["TRIAL|INITIAL"] = ["ACCEPT", "REJECT"],
        ["TRIAL|MERCHANT"] = ["GUILTY", "INNOCENT"],
        ["TRIAL|NOBLE"] = ["GUILTY", "INNOCENT"],
        ["TRIAL|NONDESCRIPT"] = ["GUILTY", "INNOCENT"],
        ["TRIAL|REJECT"] = ["ACCEPT", "DOUBLE_DOWN"],
        ["UNREST_SITE|INITIAL"] = ["REST", "KILL"],
        ["WAR_HISTORIAN_REPY|INITIAL"] = ["UNLOCK_CAGE", "UNLOCK_CHEST"],
        ["WATERLOGGED_SCRIPTORIUM|INITIAL"] = ["BLOODY_INK", "TENTACLE_QUILL", "PRICKLY_SPONGE"],
        ["WELCOME_TO_WONGOS|INITIAL"] = ["BARGAIN_BIN", "FEATURED_ITEM", "MYSTERY_BOX", "LEAVE"],
        ["WELLSPRING|INITIAL"] = ["BOTTLE", "BATHE"],
        ["WHISPERING_HOLLOW|INITIAL"] = ["GOLD", "HUG"],
        ["WOOD_CARVINGS|INITIAL"] = ["SNAKE", "BIRD", "TORUS"],
        ["ZEN_WEAVER|INITIAL"] = ["BREATHING_TECHNIQUES", "EMOTIONAL_AWARENESS", "ARACHNID_ACUPUNCTURE"],
    };

    /// <summary>
    /// Resolve event option to (index, total) using the static mapping.
    /// Returns null if the event/option can't be resolved (dynamic events).
    /// </summary>
    private static (int index, int total)? ResolveEventOption(string eventId, string page, string optionName)
    {
        // Try exact page match
        var key = $"{eventId}|{page}";
        if (EventOptionMap.TryGetValue(key, out var options))
        {
            var idx = Array.IndexOf(options, optionName);
            if (idx >= 0) return (idx, options.Length);
        }

        // Pattern matching for events with numbered/dynamic pages
        switch (eventId)
        {
            case "SLIPPERY_BRIDGE":
                if (optionName == "OVERCOME") return (0, 2);
                if (optionName.StartsWith("HOLD_ON")) return (1, 2);
                break;
            case "TABLET_OF_TRUTH":
                if (page == "INITIAL")
                {
                    if (optionName.StartsWith("DECIPHER")) return (0, 2);
                    if (optionName == "SMASH") return (1, 2);
                }
                else if (page.StartsWith("DECIPHER"))
                {
                    if (optionName == "DECIPHER") return (0, 2);
                    if (optionName == "GIVE_UP") return (1, 2);
                }
                break;
            case "COLOSSAL_FLOWER":
                if (optionName.StartsWith("EXTRACT")) return (0, 2);
                if (optionName.StartsWith("REACH_DEEPER") || optionName == "POLLINOUS_CORE") return (1, 2);
                break;
            case "ENDLESS_CONVEYOR":
                if (optionName == "OBSERVE_CHEF" || optionName == "LEAVE") return (1, 2);
                return (0, 2); // any dish is always index 0
        }

        return null;
    }

    private static string? RoomTypeToEncounterType(RoomType rt) => rt switch
    {
        RoomType.Monster => "normal",
        RoomType.Elite => "elite",
        RoomType.Boss => "boss",
        _ => null,
    };

    /// <summary>
    /// Compute a deterministic hash for dedup: game_version + character + ascension + seed + start_time.
    /// </summary>
    private static string ComputeHash(RunHistory h)
    {
        var raw = $"{h.BuildId}|{h.Players[0].Character?.Entry}|{h.Ascension}|{h.Seed}|{h.StartTime}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..32]; // 16 bytes = 32 hex chars
    }
}
