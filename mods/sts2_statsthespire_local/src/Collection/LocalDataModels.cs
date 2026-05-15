namespace CommunityStats.Collection;

/// <summary>
/// Local-only data models replacing the server ApiModels types.
/// Used by RunDataCollector, ShopPurchasePersistence, and RunHistoryAnalyzer.
/// </summary>

public class LocalShopPurchase
{
    public string ItemId { get; set; } = "";
    public string ItemType { get; set; } = "";  // "card"|"relic"|"potion"
    public int Cost { get; set; }
    public int Floor { get; set; }
}

public class LocalShopCardOffering
{
    public string CardId { get; set; } = "";
    public int Floor { get; set; }
}
