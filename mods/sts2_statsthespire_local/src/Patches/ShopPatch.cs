using CommunityStats.Collection;
using CommunityStats.Util;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;

namespace CommunityStats.Patches;

/// <summary>
/// Patches shop purchase flow to record purchases.
/// Community buy rate labels removed (local edition).
/// </summary>
[HarmonyPatch]
public static class ShopPatch
{
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

}
