using System.Text.Json.Serialization;

namespace CommunityStats.Api;

// ============================================================
//  Upload Models (Client → Server)
// ============================================================

public class RunUploadPayload
{
    [JsonPropertyName("mod_version")]    public string ModVersion { get; set; } = "";
    [JsonPropertyName("game_version")]   public string GameVersion { get; set; } = "";
    [JsonPropertyName("character")]      public string Character { get; set; } = "";
    [JsonPropertyName("ascension")]      public int Ascension { get; set; }
    [JsonPropertyName("win")]            public bool Win { get; set; }
    [JsonPropertyName("player_win_rate")]public float PlayerWinRate { get; set; }
    [JsonPropertyName("num_players")]    public int NumPlayers { get; set; }
    [JsonPropertyName("floor_reached")]  public int FloorReached { get; set; }
    [JsonPropertyName("branch")]         public string Branch { get; set; } = "";

    [JsonPropertyName("card_choices")]       public List<CardChoiceUpload> CardChoices { get; set; } = [];
    [JsonPropertyName("event_choices")]      public List<EventChoiceUpload> EventChoices { get; set; } = [];
    [JsonPropertyName("final_deck")]         public List<DeckCardUpload> FinalDeck { get; set; } = [];
    [JsonPropertyName("final_relics")]       public List<string> FinalRelics { get; set; } = [];
    [JsonPropertyName("shop_purchases")]     public List<ShopPurchaseUpload> ShopPurchases { get; set; } = [];
    [JsonPropertyName("card_removals")]      public List<CardRemovalUpload> CardRemovals { get; set; } = [];
    [JsonPropertyName("card_upgrades")]      public List<CardUpgradeUpload> CardUpgrades { get; set; } = [];
    [JsonPropertyName("shop_card_offerings")]public List<ShopCardOfferingUpload> ShopCardOfferings { get; set; } = [];
    [JsonPropertyName("encounters")]         public List<EncounterUpload> Encounters { get; set; } = [];
    [JsonPropertyName("contributions")]      public List<ContributionUpload> Contributions { get; set; } = [];

    /// <summary>Optional dedup hash for history import (PRD §3.19.3).</summary>
    [JsonPropertyName("run_hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunHash { get; set; }
}

public class CardChoiceUpload
{
    [JsonPropertyName("card_id")]        public string CardId { get; set; } = "";
    [JsonPropertyName("upgrade_level")]  public int UpgradeLevel { get; set; }
    [JsonPropertyName("was_picked")]     public bool WasPicked { get; set; }
    [JsonPropertyName("floor")]          public int Floor { get; set; }
}

public class EventChoiceUpload
{
    [JsonPropertyName("event_id")]       public string EventId { get; set; } = "";
    [JsonPropertyName("option_index")]   public int OptionIndex { get; set; }
    [JsonPropertyName("total_options")]  public int TotalOptions { get; set; }

    /// <summary>Sorted option IDs joined by '|' for combo-based events (ancients).</summary>
    [JsonPropertyName("combo_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComboKey { get; set; }

    /// <summary>The specific option chosen, for combo/dynamic events.</summary>
    [JsonPropertyName("chosen_option_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChosenOptionId { get; set; }
}

public class DeckCardUpload
{
    [JsonPropertyName("card_id")]        public string CardId { get; set; } = "";
    [JsonPropertyName("upgrade_level")]  public int UpgradeLevel { get; set; }
}

public class ShopPurchaseUpload
{
    [JsonPropertyName("item_id")]        public string ItemId { get; set; } = "";
    [JsonPropertyName("item_type")]      public string ItemType { get; set; } = "";  // "card"|"relic"|"potion"
    [JsonPropertyName("cost")]           public int Cost { get; set; }
    [JsonPropertyName("floor")]          public int Floor { get; set; }
}

public class CardRemovalUpload
{
    [JsonPropertyName("card_id")]        public string CardId { get; set; } = "";
    [JsonPropertyName("source")]         public string Source { get; set; } = "";  // "shop"|"event"
    [JsonPropertyName("floor")]          public int Floor { get; set; }
}

public class CardUpgradeUpload
{
    [JsonPropertyName("card_id")]        public string CardId { get; set; } = "";
    [JsonPropertyName("source")]         public string Source { get; set; } = "";  // "campfire"|"event"|"other"
}

public class ShopCardOfferingUpload
{
    [JsonPropertyName("card_id")]        public string CardId { get; set; } = "";
    [JsonPropertyName("floor")]          public int Floor { get; set; }
}

public class EncounterUpload
{
    [JsonPropertyName("encounter_id")]   public string EncounterId { get; set; } = "";
    [JsonPropertyName("encounter_type")] public string EncounterType { get; set; } = "";  // "normal"|"elite"|"boss"
    [JsonPropertyName("damage_taken")]   public int DamageTaken { get; set; }
    [JsonPropertyName("turns_taken")]    public int TurnsTaken { get; set; }
    [JsonPropertyName("player_died")]    public bool PlayerDied { get; set; }
    [JsonPropertyName("floor")]          public int Floor { get; set; }
}

public class ContributionUpload
{
    [JsonPropertyName("source_id")]          public string SourceId { get; set; } = "";
    // Round 14 v5: full set of source_type values emitted by CombatTracker.ResolveSource.
    // Accepted by server migration 005 + models.py clamp_source_type.
    //   card | relic | potion | power | untracked | orb | event | rest | merchant | floor_regen
    [JsonPropertyName("source_type")]        public string SourceType { get; set; } = "";
    [JsonPropertyName("encounter_id")]       public string? EncounterId { get; set; }       // null = run aggregate
    [JsonPropertyName("times_played")]       public int TimesPlayed { get; set; }
    [JsonPropertyName("direct_damage")]      public int DirectDamage { get; set; }
    [JsonPropertyName("attributed_damage")]  public int AttributedDamage { get; set; }
    [JsonPropertyName("effective_block")]    public int EffectiveBlock { get; set; }
    [JsonPropertyName("mitigated_by_debuff")]public int MitigatedByDebuff { get; set; }
    [JsonPropertyName("mitigated_by_buff")]  public int MitigatedByBuff { get; set; }
    [JsonPropertyName("cards_drawn")]        public int CardsDrawn { get; set; }
    [JsonPropertyName("energy_gained")]      public int EnergyGained { get; set; }
    [JsonPropertyName("hp_healed")]          public int HpHealed { get; set; }
    [JsonPropertyName("stars_contribution")] public int StarsContribution { get; set; }
    [JsonPropertyName("mitigated_by_str")]   public int MitigatedByStrReduction { get; set; }
    [JsonPropertyName("modifier_damage")]    public int ModifierDamage { get; set; }
    [JsonPropertyName("modifier_block")]     public int ModifierBlock { get; set; }
    [JsonPropertyName("self_damage")]        public int SelfDamage { get; set; }
    [JsonPropertyName("upgrade_damage")]     public int UpgradeDamage { get; set; }
    [JsonPropertyName("upgrade_block")]      public int UpgradeBlock { get; set; }
    [JsonPropertyName("origin_source_id")]   public string? OriginSourceId { get; set; }
}

// ============================================================
//  Query Response Models (Server → Client)
// ============================================================

public class BulkStatsBundle
{
    [JsonPropertyName("generated_at")]   public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("total_runs")]     public int TotalRuns { get; set; }
    [JsonPropertyName("cards")]          public Dictionary<string, CardStats> Cards { get; set; } = [];
    [JsonPropertyName("relics")]         public Dictionary<string, RelicStats> Relics { get; set; } = [];
    [JsonPropertyName("events")]         public Dictionary<string, EventStats> Events { get; set; } = [];
    [JsonPropertyName("encounters")]     public Dictionary<string, EncounterStats> Encounters { get; set; } = [];
}

public class CardStats
{
    [JsonPropertyName("pick")]           public float PickRate { get; set; }
    [JsonPropertyName("win")]            public float WinRate { get; set; }
    [JsonPropertyName("removal")]        public float RemovalRate { get; set; }
    [JsonPropertyName("upgrade")]        public float UpgradeRate { get; set; }
    [JsonPropertyName("shop_buy")]       public float ShopBuyRate { get; set; }
    [JsonPropertyName("n")]              public int SampleSize { get; set; }
}

public class RelicStats
{
    [JsonPropertyName("win")]            public float WinRate { get; set; }
    [JsonPropertyName("pick")]           public float PickRate { get; set; }
    [JsonPropertyName("shop_buy")]       public float ShopBuyRate { get; set; }
    [JsonPropertyName("n")]              public int SampleSize { get; set; }
}

public class EventStats
{
    [JsonPropertyName("options")]        public List<EventOptionStats> Options { get; set; } = [];
    /// <summary>Per-combo stats for ancient/dynamic events. Key = combo_key.</summary>
    [JsonPropertyName("combos")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<ComboOptionStats>>? Combos { get; set; }
    /// <summary>Flat per-option stats (COLORFUL_PHILOSOPHERS). Sum != 100%.</summary>
    [JsonPropertyName("flat_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComboOptionStats>? FlatOptions { get; set; }
}

public class EventOptionStats
{
    [JsonPropertyName("idx")]            public int OptionIndex { get; set; }
    [JsonPropertyName("sel")]            public float SelectionRate { get; set; }
    [JsonPropertyName("win")]            public float WinRate { get; set; }
    [JsonPropertyName("n")]              public int SampleSize { get; set; }
}

public class ComboOptionStats
{
    [JsonPropertyName("id")]             public string OptionId { get; set; } = "";
    [JsonPropertyName("sel")]            public float SelectionRate { get; set; }
    [JsonPropertyName("win")]            public float WinRate { get; set; }
    [JsonPropertyName("n")]              public int SampleSize { get; set; }
}

public class EncounterStats
{
    [JsonPropertyName("type")]           public string EncounterType { get; set; } = "";
    [JsonPropertyName("avg_dmg")]        public float AvgDamageTaken { get; set; }
    [JsonPropertyName("death")]          public float DeathRate { get; set; }
    [JsonPropertyName("avg_turns")]      public float AvgTurns { get; set; }
    [JsonPropertyName("n")]              public int SampleSize { get; set; }
}
