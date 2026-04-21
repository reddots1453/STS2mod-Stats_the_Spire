using CommunityStats.Api;
using CommunityStats.Collection;
using CommunityStats.UI;
using CommunityStats.Util;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;

namespace CommunityStats.Patches;

/// <summary>
/// Patches shop purchase flow to record purchases and display buy rates.
/// Covers cards, relics, and potions.
/// </summary>
[HarmonyPatch]
public static class ShopPatch
{
    private const string StatsLabelMeta = "cs_shop_stats";

    // PRD §3.13: track live slot nodes so we can refresh their labels
    // when StatsProvider.DataRefreshed fires (e.g. after F9 apply).
    private static readonly List<WeakReference<NMerchantCard>> _liveCards = new();
    private static readonly List<WeakReference<NMerchantRelic>> _liveRelics = new();

    public static void SubscribeRefresh()
    {
        StatsProvider.DataRefreshed += OnDataRefreshed;
    }

    private static void OnDataRefreshed()
    {
        Safe.Run(() =>
        {
            for (int i = _liveCards.Count - 1; i >= 0; i--)
            {
                if (_liveCards[i].TryGetTarget(out var card) &&
                    GodotObject.IsInstanceValid(card) && card.IsInsideTree())
                    AttachCardBuyRate(card);
                else
                    _liveCards.RemoveAt(i);
            }
            for (int i = _liveRelics.Count - 1; i >= 0; i--)
            {
                if (_liveRelics[i].TryGetTarget(out var relic) &&
                    GodotObject.IsInstanceValid(relic) && relic.IsInsideTree())
                    AttachRelicBuyRate(relic);
                else
                    _liveRelics.RemoveAt(i);
            }
        });
    }

    private static void TrackCard(NMerchantCard card)
    {
        for (int i = _liveCards.Count - 1; i >= 0; i--)
        {
            if (!_liveCards[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveCards.RemoveAt(i);
            else if (t == card) return;
        }
        _liveCards.Add(new WeakReference<NMerchantCard>(card));
    }

    private static void TrackRelic(NMerchantRelic relic)
    {
        for (int i = _liveRelics.Count - 1; i >= 0; i--)
        {
            if (!_liveRelics[i].TryGetTarget(out var t) || !GodotObject.IsInstanceValid(t))
                _liveRelics.RemoveAt(i);
            else if (t == relic) return;
        }
        _liveRelics.Add(new WeakReference<NMerchantRelic>(relic));
    }

    // ── Shop Card Offerings Recording (on shop enter) ───────

    // Patch EnterInternal (declared on MerchantRoom itself) rather than
    // the inherited AbstractRoom.Enter. Earlier version patched Enter, but
    // that method's MethodInfo has DeclaringType = AbstractRoom — Harmony
    // attempts to match the __instance parameter type to the declaring
    // type and refuses the patch when a more-derived __instance is
    // declared. Result: postfix silently never bound on v0.103.2 →
    // shop_card_offerings was empty for every run past the game version
    // where MerchantRoom.Enter existed as a non-inherited override.
    //
    // EnterInternal IS declared on MerchantRoom. MerchantRoom.Inventory is
    // assigned synchronously at EnterInternal's first line (before any
    // await), so the postfix — which fires when the async method
    // synchronously returns its Task at the first await — sees a fully
    // populated Inventory.
    [HarmonyPatch(typeof(MerchantRoom), nameof(MerchantRoom.EnterInternal))]
    [HarmonyPostfix]
    public static void AfterMerchantRoomEnter(MerchantRoom __instance)
    {
        Safe.Run(() =>
        {
            var inventory = __instance.Inventory;
            if (inventory == null) return;

            var floor = RunDataCollector.CurrentFloor;

            foreach (var entry in inventory.CharacterCardEntries)
            {
                if (!entry.IsStocked || entry.CreationResult == null) continue;
                var card = entry.CreationResult.Card;
                var cardId = card?.Id?.Entry;
                if (cardId != null)
                    RunDataCollector.RecordShopCardOffering(cardId, floor);
            }

            foreach (var entry in inventory.ColorlessCardEntries)
            {
                if (!entry.IsStocked || entry.CreationResult == null) continue;
                var card = entry.CreationResult.Card;
                var cardId = card?.Id?.Entry;
                if (cardId != null)
                    RunDataCollector.RecordShopCardOffering(cardId, floor);
            }
        });
    }

    // ── Purchase Recording (Card / Relic / Potion) ──────────
    //
    // Patch the ENTRY model layer, not the NMerchant* UI layer.
    // MerchantEntry.OnTryPurchaseWrapper runs ClearAfterPurchase / RestockAfterPurchase
    // BEFORE firing InvokePurchaseCompleted (→ NMerchantCard.OnSuccessfulPurchase),
    // so by the time a UI-layer prefix sees the entry, CreationResult / Model
    // has already been nulled or replaced by Populate(). Prefixes on
    // ClearAfterPurchase / RestockAfterPurchase run BEFORE the field is
    // cleared/replaced, capturing the still-live model. Both methods only
    // run on successful purchases (see MerchantEntry.cs OnTryPurchaseWrapper
    // success branch), so no spurious records.

    [HarmonyPatch(typeof(MerchantCardEntry), "ClearAfterPurchase")]
    [HarmonyPrefix]
    public static void BeforeCardClear(MerchantCardEntry __instance) => RecordCardPurchase(__instance);

    [HarmonyPatch(typeof(MerchantCardEntry), "RestockAfterPurchase")]
    [HarmonyPrefix]
    public static void BeforeCardRestock(MerchantCardEntry __instance) => RecordCardPurchase(__instance);

    [HarmonyPatch(typeof(MerchantRelicEntry), "ClearAfterPurchase")]
    [HarmonyPrefix]
    public static void BeforeRelicClear(MerchantRelicEntry __instance) => RecordRelicPurchase(__instance);

    [HarmonyPatch(typeof(MerchantRelicEntry), "RestockAfterPurchase")]
    [HarmonyPrefix]
    public static void BeforeRelicRestock(MerchantRelicEntry __instance) => RecordRelicPurchase(__instance);

    [HarmonyPatch(typeof(MerchantPotionEntry), "ClearAfterPurchase")]
    [HarmonyPrefix]
    public static void BeforePotionClear(MerchantPotionEntry __instance) => RecordPotionPurchase(__instance);

    [HarmonyPatch(typeof(MerchantPotionEntry), "RestockAfterPurchase")]
    [HarmonyPrefix]
    public static void BeforePotionRestock(MerchantPotionEntry __instance) => RecordPotionPurchase(__instance);

    private static void RecordCardPurchase(MerchantCardEntry entry)
    {
        Safe.Run(() =>
        {
            var cardId = entry.CreationResult?.Card?.Id.Entry;
            if (string.IsNullOrEmpty(cardId)) return;
            int cost = entry.Cost;
            RunDataCollector.RecordShopPurchase(cardId!, "card", cost, RunDataCollector.CurrentFloor);
            Safe.Info($"[ShopPurchase] card={cardId} cost={cost} floor={RunDataCollector.CurrentFloor}");
        });
    }

    private static void RecordRelicPurchase(MerchantRelicEntry entry)
    {
        Safe.Run(() =>
        {
            var relicId = entry.Model?.Id.Entry;
            if (string.IsNullOrEmpty(relicId)) return;
            int cost = entry.Cost;
            RunDataCollector.RecordShopPurchase(relicId!, "relic", cost, RunDataCollector.CurrentFloor);
            Safe.Info($"[ShopPurchase] relic={relicId} cost={cost} floor={RunDataCollector.CurrentFloor}");
        });
    }

    private static void RecordPotionPurchase(MerchantPotionEntry entry)
    {
        Safe.Run(() =>
        {
            var potionId = entry.Model?.Id.Entry;
            if (string.IsNullOrEmpty(potionId)) return;
            int cost = entry.Cost;
            RunDataCollector.RecordShopPurchase(potionId!, "potion", cost, RunDataCollector.CurrentFloor);
            Safe.Info($"[ShopPurchase] potion={potionId} cost={cost} floor={RunDataCollector.CurrentFloor}");
        });
    }

    // ── Shop Card / Relic Buy Rate Display ──────────────────
    //
    // Patch both FillSlot (one-shot at shop enter) AND UpdateVisual (fires on
    // FillSlot + gold change + post-purchase + EntryUpdated events). Previous
    // implementation only hooked FillSlot AND used a chained Traverse reflection
    // (`_cardNode` → Model → Id → Entry) that silently returned null when
    // `_cardNode` wasn't populated yet — which happens if our postfix runs
    // before UpdateVisual finishes creating the card node. Result: label never
    // attached, nothing shown.
    //
    // Fix: read the entry directly from the slot's public `Entry` property
    // (NMerchantSlot.Entry returns MerchantEntry base; cast to the concrete
    // subtype). CreationResult.Card / Model are authoritative and set in the
    // game's MerchantInventory.Populate step before any slot sees the entry,
    // so no timing race.

    [HarmonyPatch(typeof(NMerchantCard), nameof(NMerchantCard.FillSlot))]
    [HarmonyPostfix]
    public static void AfterCardFillSlot(NMerchantCard __instance) => AttachCardBuyRate(__instance);

    [HarmonyPatch(typeof(NMerchantCard), "UpdateVisual")]
    [HarmonyPostfix]
    public static void AfterCardUpdateVisual(NMerchantCard __instance) => AttachCardBuyRate(__instance);

    private static void AttachCardBuyRate(NMerchantCard slot)
    {
        Safe.Run(() =>
        {
            TrackCard(slot);

            if (slot.Entry is not MerchantCardEntry cardEntry)
            {
                Safe.Info("[ShopBuyRate] card: entry is not MerchantCardEntry → skip");
                return;
            }
            var card = cardEntry.CreationResult?.Card;
            if (card == null)
            {
                // Empty / sold-out slot: wipe any stale label.
                RemoveExistingLabel(slot);
                return;
            }
            var cardId = card.Id.Entry;
            if (string.IsNullOrEmpty(cardId)) return;

            RemoveExistingLabel(slot);

            var stats = StatsProvider.Instance.GetCardStats(cardId);
            Label label;
            string pathTag;
            if (stats != null)
            {
                label = StatsLabel.ForShopBuyRate(stats.ShopBuyRate);
                pathTag = $"stats({stats.ShopBuyRate:F3})";
            }
            else if (!StatsProvider.Instance.HasBundle)
            {
                label = StatsLabel.ForLoading();
                pathTag = "loading";
            }
            else
            {
                label = StatsLabel.ForUnavailable();
                pathTag = "unavailable";
            }

            label.SetMeta(StatsLabelMeta, true);
            // Absolute position below the card art. HorizontalAlignment.Right
            // (set by StatsLabel.ForShopBuyRate) pins the text to the box's
            // right edge. Size.X must match the shop card visual width —
            // CardRewardScreenPatch uses 350 because NGridCardHolder slots
            // have outer padding, but NMerchantCard is the card control
            // itself and is narrower (~160-180px), so a 300-wide box pushes
            // the right-aligned text past the card's right edge. 160 lands
            // the text inside the card's bottom-right.
            label.Position = new Vector2(0, 200);
            label.Size = new Vector2(160, 30);
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            label.ZIndex = 100;
            slot.AddChild(label);
            label.Visible = true;
            Safe.Info($"[ShopBuyRate] card={cardId} attached ({pathTag}) slotSize={slot.Size}");
        });
    }

    [HarmonyPatch(typeof(NMerchantRelic), nameof(NMerchantRelic.FillSlot))]
    [HarmonyPostfix]
    public static void AfterRelicFillSlot(NMerchantRelic __instance) => AttachRelicBuyRate(__instance);

    [HarmonyPatch(typeof(NMerchantRelic), "UpdateVisual")]
    [HarmonyPostfix]
    public static void AfterRelicUpdateVisual(NMerchantRelic __instance) => AttachRelicBuyRate(__instance);

    // Relic label font size — matches card label (16 was too small at 10).
    // Relic icons in shop are not tiny; 14 reads cleanly at default UI scale.
    private const int RelicBuyRateFontSize = 14;

    private static void AttachRelicBuyRate(NMerchantRelic slot)
    {
        Safe.Run(() =>
        {
            TrackRelic(slot);

            if (slot.Entry is not MerchantRelicEntry relicEntry)
            {
                Safe.Info("[ShopBuyRate] relic: entry is not MerchantRelicEntry → skip");
                return;
            }
            var relic = relicEntry.Model;
            if (relic == null)
            {
                RemoveExistingLabel(slot);
                return;
            }
            var relicId = relic.Id.Entry;
            if (string.IsNullOrEmpty(relicId)) return;

            RemoveExistingLabel(slot);

            var stats = StatsProvider.Instance.GetRelicStats(relicId);
            Label label;
            string pathTag;
            if (stats != null)
            {
                label = StatsLabel.ForShopBuyRate(stats.ShopBuyRate, fontSize: RelicBuyRateFontSize);
                pathTag = $"stats({stats.ShopBuyRate:F3})";
            }
            else if (!StatsProvider.Instance.HasBundle)
            {
                label = StatsLabel.ForLoading();
                pathTag = "loading";
            }
            else
            {
                label = StatsLabel.ForUnavailable();
                pathTag = "unavailable";
            }

            label.SetMeta(StatsLabelMeta, true);
            slot.AddChild(label);
            label.ZIndex = 100;
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            // Relic box: wider to fit fontSize=14 text "购买 12.3%".
            label.AnchorLeft = 1f;
            label.AnchorTop = 1f;
            label.AnchorRight = 1f;
            label.AnchorBottom = 1f;
            label.OffsetLeft = -80;
            label.OffsetTop = -24;
            label.OffsetRight = -4;
            label.OffsetBottom = -4;
            label.Visible = true;
            Safe.Info($"[ShopBuyRate] relic={relicId} attached ({pathTag}) slotSize={slot.Size}");
        });
    }

    // ── Helpers ──────────────────────────────────────────────

    private static void RemoveExistingLabel(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Label lbl && lbl.HasMeta(StatsLabelMeta))
                lbl.QueueFree();
        }
    }
}
