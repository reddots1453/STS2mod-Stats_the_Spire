using System;
using System.Collections.Generic;
using CommunityStats.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace CommunityStats.Util;

/// <summary>
/// Resolves localized display names for game model IDs. When the mod language
/// (L.Current) is CN, delegates to the game's LocManager which returns names
/// in the game's display language. When L.Current is EN, uses LocManager for
/// the game's native English text and falls back to hardcoded English names
/// from the game data when the game itself is running in Chinese.
///
/// All methods return the input ID unchanged when the lookup fails so the
/// UI never shows an empty cell.
/// </summary>
public static class NameLookup
{
    public static string Encounter(string id) => Lookup("encounters", id, ".title", _encounterEN);
    public static string Event(string id)     => Lookup("events", id, ".title", _eventEN);
    public static string Relic(string id)     => Lookup("relics", id, ".title", _relicEN);
    public static string Card(string id)      => Lookup("cards", id, ".title");
    public static string Monster(string id)   => Lookup("monsters", id, ".name", _monsterEN);
    public static string Potion(string id)    => Lookup("potions", id, ".title");

    /// <summary>
    /// Localized name for an Ancient elder (NEOW/PAEL/...).
    /// </summary>
    public static string Ancient(string elderId)
    {
        if (string.IsNullOrEmpty(elderId)) return "";
        var ev = TryLookup("events", elderId, ".title");
        if (ev != null) return ev;
        var enc = TryLookup("encounters", elderId, ".title");
        if (enc != null) return enc;

        if (L.Current == L.Lang.CN)
        {
            return elderId switch
            {
                "NEOW"      => "涅奥",
                "PAEL"      => "帕埃尔",
                "TEZCATARA" => "泰兹卡塔拉",
                "OROBAS"    => "奥洛巴斯",
                "VAKUU"     => "瓦库",
                "TANX"      => "坦克斯",
                "NONUPEIPE" => "诺努皮佩",
                "DARV"      => "达弗",
                _ => elderId,
            };
        }
        return elderId switch
        {
            "NEOW"      => "Neow",
            "PAEL"      => "Pael",
            "TEZCATARA" => "Tezcatara",
            "OROBAS"    => "Orobas",
            "VAKUU"     => "Vakuu",
            "TANX"      => "Tanx",
            "NONUPEIPE" => "Nonupeipe",
            "DARV"      => "Darv",
            _ => elderId,
        };
    }

    /// <summary>
    /// Resolves a death cause id (encounter/event id, or "ABANDONED").
    /// </summary>
    public static string DeathCause(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (id == "ABANDONED") return L.Get("career.cause_abandoned");

        var enc = TryLookup("encounters", id, ".title");
        if (enc != null) return enc;
        var ev = TryLookup("events", id, ".title");
        if (ev != null) return ev;
        var mon = TryLookup("monsters", id, ".name");
        if (mon != null) return mon;
        return id;
    }

    /// <summary>Localized "Act N" label.</summary>
    public static string ActLabel(int actIndex)
    {
        try { return string.Format(L.Get("career.act_n"), actIndex); }
        catch { return "Act " + actIndex; }
    }

    // ── internal ──────────────────────────────────────────────

    private static string Lookup(string table, string id, string suffix,
        IReadOnlyDictionary<string, string>? enFallback = null)
    {
        return TryLookup(table, id, suffix, enFallback) ?? id;
    }

    private static string? TryLookup(string table, string id, string suffix,
        IReadOnlyDictionary<string, string>? enFallback = null)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var key = id + suffix;
            if (!LocString.Exists(table, key)) return null;

            // When the mod is in English but the game is in another language,
            // try the hardcoded English fallback first (for encounters/relics/
            // events that we have curated names for). If that misses, reach
            // into the game's English loc table via the private _fallback field
            // on LocTable — this gives us the official English name for every
            // card, relic, potion, and monster without maintaining a 600-entry
            // hardcoded dictionary.
            if (L.Current == L.Lang.EN)
            {
                if (enFallback != null && enFallback.TryGetValue(id, out var en))
                    return en;

                try
                {
                    var locTable = LocManager.Instance.GetTable(table);
                    var fallbackTable = Traverse.Create(locTable)
                        .Field("_fallback").GetValue<LocTable>();
                    if (fallbackTable != null && fallbackTable.HasEntry(key))
                        return fallbackTable.GetRawText(key);
                }
                catch { /* reflection failed — fall through to current-lang text */ }
            }

            return LocManager.Instance.GetTable(table).GetRawText(key);
        }
        catch (Exception)
        {
            if (enFallback != null && enFallback.TryGetValue(id, out var en))
                return en;
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // English name fallback tables — sourced from KnowledgeBase
    // ═══════════════════════════════════════════════════════════

    private static readonly IReadOnlyDictionary<string, string> _encounterEN = new Dictionary<string, string>
    {
        {"AXEBOTS_NORMAL", "Bot Buddies"},
        {"BOWLBUGS_NORMAL", "Bowlbug Swarm"},
        {"BOWLBUGS_WEAK", "Bowlbugs"},
        {"BYGONE_EFFIGY_ELITE", "Bygone Effigy"},
        {"BYRDONIS_ELITE", "Byrdonis"},
        {"CEREMONIAL_BEAST_BOSS", "Ceremonial Beast"},
        {"CHOMPERS_NORMAL", "An Automaton Pair"},
        {"CONSTRUCT_MENAGERIE_NORMAL", "Construct Menagerie"},
        {"CORPSE_SLUGS_NORMAL", "Many Corpse Slugs"},
        {"CORPSE_SLUGS_WEAK", "Corpse Slugs"},
        {"CUBEX_CONSTRUCT_NORMAL", "Cubex Construct"},
        {"CULTISTS_NORMAL", "Cultists"},
        {"DECIMILLIPEDE_ELITE", "The Decimillipede"},
        {"DEVOTED_SCULPTOR_WEAK", "Devoted Sculptor"},
        {"DOORMAKER_BOSS", "The Doormaker"},
        {"ENTOMANCER_ELITE", "Entomancer"},
        {"EXOSKELETONS_NORMAL", "Many Exoskeletons"},
        {"EXOSKELETONS_WEAK", "Exoskeletons"},
        {"FABRICATOR_NORMAL", "Fabricator"},
        {"FLYCONID_NORMAL", "Shroom and Slime"},
        {"FOGMOG_NORMAL", "Fogmog"},
        {"FOSSIL_STALKER_NORMAL", "Fossil Stalker"},
        {"FROG_KNIGHT_NORMAL", "Frog Knight"},
        {"FUZZY_WURM_CRAWLER_WEAK", "Fuzzy Wurm Crawler"},
        {"GLOBE_HEAD_NORMAL", "A Lone Globe Head"},
        {"GREMLIN_MERC_NORMAL", "Two Gremlins in a Trenchcoat"},
        {"HAUNTED_SHIP_NORMAL", "Haunted Ship"},
        {"HUNTER_KILLER_NORMAL", "Hunter Killer"},
        {"INFESTED_PRISMS_ELITE", "Infested Prism"},
        {"INKLETS_NORMAL", "Inklets"},
        {"KAISER_CRAB_BOSS", "Kaiser Crab"},
        {"KNIGHTS_ELITE", "Knight Gang"},
        {"KNOWLEDGE_DEMON_BOSS", "Knowledge Demon"},
        {"LAGAVULIN_MATRIARCH_BOSS", "Lagavulin Matriarch"},
        {"LIVING_FOG_NORMAL", "Evil Gas"},
        {"LOUSE_PROGENITOR_NORMAL", "Louse Progenitor"},
        {"MAWLER_NORMAL", "Mawler"},
        {"MECHA_KNIGHT_ELITE", "Mecha Knight"},
        {"MYTES_NORMAL", "Mass of Mytes"},
        {"NIBBITS_NORMAL", "A Pair of Nibbits"},
        {"NIBBITS_WEAK", "A Lone Nibbit"},
        {"OVERGROWTH_CRAWLERS", "Overgrowth Crawlers"},
        {"OVICOPTER_NORMAL", "Ovicopter"},
        {"OWL_MAGISTRATE_NORMAL", "Owl Magistrate"},
        {"PHANTASMAL_GARDENERS_ELITE", "Phantasmal Gardeners"},
        {"PHROG_PARASITE_ELITE", "Phrog Parasite"},
        {"PUNCH_CONSTRUCT_NORMAL", "Punch Construct"},
        {"QUEEN_BOSS", "Queen"},
        {"RUBY_RAIDERS_NORMAL", "Ruby Raiders"},
        {"SCROLLS_OF_BITING_NORMAL", "Many Scrolls of Biting"},
        {"SCROLLS_OF_BITING_WEAK", "Scrolls of Biting"},
        {"SEAPUNK_WEAK", "Seapunk"},
        {"SEWER_CLAM_NORMAL", "Sewer Clam"},
        {"SHRINKER_BEETLE_WEAK", "Shrinker Beetle"},
        {"SKULKING_COLONY_ELITE", "Skulking Colony"},
        {"SLIMED_BERSERKER_NORMAL", "Slimed Berserker"},
        {"SLIMES_NORMAL", "Swarm of Slimes"},
        {"SLIMES_WEAK", "Group of Slimes"},
        {"SLITHERING_STRANGLER_NORMAL", "Strangler and Friend"},
        {"SLUDGE_SPINNER_WEAK", "Sludge Spinner"},
        {"SLUMBERING_BEETLE_NORMAL", "Slumber Party"},
        {"SNAPPING_JAXFRUIT_NORMAL", "Overgrowth Flora"},
        {"SOUL_FYSH_BOSS", "Soul Fysh"},
        {"SOUL_NEXUS_ELITE", "Soul Nexus"},
        {"SPINY_TOAD_NORMAL", "Spiny Toad"},
        {"TERROR_EEL_ELITE", "Terror Eel"},
        {"TEST_SUBJECT_BOSS", "Test Subject"},
        {"THE_INSATIABLE_BOSS", "The Insatiable"},
        {"THE_KIN_BOSS", "The Kin"},
        {"THE_LOST_AND_FORGOTTEN_NORMAL", "Lost and Forgotten"},
        {"THE_OBSCURA_NORMAL", "The Obscura"},
        {"THIEVING_HOPPER_WEAK", "Thieving Hopper"},
        {"TOADPOLES_NORMAL", "Underdocks Wildlife"},
        {"TOADPOLES_WEAK", "Toadpoles"},
        {"TUNNELER_NORMAL", "Tunneling Twosome"},
        {"TUNNELER_WEAK", "Tunneler"},
        {"TURRET_OPERATOR_WEAK", "Turret Operator"},
        {"TWO_TAILED_RATS_NORMAL", "Two-Tailed Rats"},
        {"VANTOM_BOSS", "Vantom"},
        {"VINE_SHAMBLER_NORMAL", "Vine Shambler"},
        {"WATERFALL_GIANT_BOSS", "Waterfall Giant"},
    };

    private static readonly IReadOnlyDictionary<string, string> _relicEN = new Dictionary<string, string>
    {
        {"AKABEKO", "Akabeko"},
        {"ALCHEMICAL_COFFER", "Alchemical Coffer"},
        {"AMETHYST_AUBERGINE", "Amethyst Aubergine"},
        {"ANCHOR", "Anchor"},
        {"ARCANE_SCROLL", "Arcane Scroll"},
        {"ARCHAIC_TOOTH", "Archaic Tooth"},
        {"ART_OF_WAR", "Art of War"},
        {"ASTROLABE", "Astrolabe"},
        {"BAG_OF_MARBLES", "Bag of Marbles"},
        {"BAG_OF_PREPARATION", "Bag of Preparation"},
        {"BEATING_REMNANT", "Beating Remnant"},
        {"BEAUTIFUL_BRACELET", "Beautiful Bracelet"},
        {"BELLOWS", "Bellows"},
        {"BELT_BUCKLE", "Belt Buckle"},
        {"BIG_HAT", "Big Hat"},
        {"BIG_MUSHROOM", "Big Mushroom"},
        {"BIIIG_HUG", "Biiig Hug"},
        {"BING_BONG", "Bing Bong"},
        {"BLACK_BLOOD", "Black Blood"},
        {"BLACK_STAR", "Black Star"},
        {"BLESSED_ANTLER", "Blessed Antler"},
        {"BLOOD_SOAKED_ROSE", "Blood-Soaked Rose"},
        {"BLOOD_VIAL", "Blood Vial"},
        {"BONE_FLUTE", "Bone Flute"},
        {"BONE_TEA", "Bone Tea"},
        {"BOOK_OF_FIVE_RINGS", "Book of Five Rings"},
        {"BOOK_REPAIR_KNIFE", "Book Repair Knife"},
        {"BOOKMARK", "Bookmark"},
        {"BOOMING_CONCH", "Booming Conch"},
        {"BOUND_PHYLACTERY", "Bound Phylactery"},
        {"BOWLER_HAT", "Bowler Hat"},
        {"BREAD", "Bread"},
        {"BRILLIANT_SCARF", "Brilliant Scarf"},
        {"BRIMSTONE", "Brimstone"},
        {"BRONZE_SCALES", "Bronze Scales"},
        {"BURNING_BLOOD", "Burning Blood"},
        {"BURNING_STICKS", "Burning Sticks"},
        {"BYRDPIP", "Byrdpip"},
        {"CALLING_BELL", "Calling Bell"},
        {"CANDELABRA", "Candelabra"},
        {"CAPTAINS_WHEEL", "Captain's Wheel"},
        {"CAULDRON", "Cauldron"},
        {"CENTENNIAL_PUZZLE", "Centennial Puzzle"},
        {"CHANDELIER", "Chandelier"},
        {"CHARONS_ASHES", "Charon's Ashes"},
        {"CHEMICAL_X", "Chemical X"},
        {"CHOICES_PARADOX", "Choices Paradox"},
        {"CHOSEN_CHEESE", "The Chosen Cheese"},
        {"CIRCLET", "Circlet"},
        {"CLAWS", "Claws"},
        {"CLOAK_CLASP", "Cloak Clasp"},
        {"CRACKED_CORE", "Cracked Core"},
        {"CROSSBOW", "Crossbow"},
        {"CURSED_PEARL", "Cursed Pearl"},
        {"DARKSTONE_PERIAPT", "Darkstone Periapt"},
        {"DATA_DISK", "Data Disk"},
        {"DAUGHTER_OF_THE_WIND", "Daughter of the Wind"},
        {"DELICATE_FROND", "Delicate Frond"},
        {"DEMON_TONGUE", "Demon Tongue"},
        {"DEPRECATED_RELIC", "Deprecated Relic"},
        {"DIAMOND_DIADEM", "Diamond Diadem"},
        {"DINGY_RUG", "Dingy Rug"},
        {"DISTINGUISHED_CAPE", "Distinguished Cape"},
        {"DIVINE_DESTINY", "Divine Destiny"},
        {"DIVINE_RIGHT", "Divine Right"},
        {"DOLLYS_MIRROR", "Dolly's Mirror"},
        {"DRAGON_FRUIT", "Dragon Fruit"},
        {"DREAM_CATCHER", "Dream Catcher"},
        {"DRIFTWOOD", "Driftwood"},
        {"DUSTY_TOME", "Dusty Tome"},
        {"ECTOPLASM", "Ectoplasm"},
        {"ELECTRIC_SHRYMP", "Electric Shrymp"},
        {"EMBER_TEA", "Ember Tea"},
        {"EMOTION_CHIP", "Emotion Chip"},
        {"EMPTY_CAGE", "Empty Cage"},
        {"ETERNAL_FEATHER", "Eternal Feather"},
        {"FAKE_ANCHOR", "Anchor???"},
        {"FAKE_BLOOD_VIAL", "Blood Vial???"},
        {"FAKE_HAPPY_FLOWER", "Happy Flower???"},
        {"FAKE_LEES_WAFFLE", "Lee's Waffle???"},
        {"FAKE_MANGO", "Mango???"},
        {"FAKE_MERCHANTS_RUG", "The Merchant's Rug???"},
        {"FAKE_ORICHALCUM", "Orichalcum???"},
        {"FAKE_SNECKO_EYE", "Snecko Eye???"},
        {"FAKE_STRIKE_DUMMY", "Strike Dummy???"},
        {"FAKE_VENERABLE_TEA_SET", "Venerable Tea Set???"},
        {"FENCING_MANUAL", "Fencing Manual"},
        {"FESTIVE_POPPER", "Festive Popper"},
        {"FIDDLE", "Fiddle"},
        {"FORGOTTEN_SOUL", "Forgotten Soul"},
        {"FRAGRANT_MUSHROOM", "Fragrant Mushroom"},
        {"FRESNEL_LENS", "Fresnel Lens"},
        {"FROZEN_EGG", "Frozen Egg"},
        {"FUNERARY_MASK", "Funerary Mask"},
        {"FUR_COAT", "Fur Coat"},
        {"GALACTIC_DUST", "Galactic Dust"},
        {"GAMBLING_CHIP", "Gambling Chip"},
        {"GAME_PIECE", "Game Piece"},
        {"GHOST_SEED", "Ghost Seed"},
        {"GIRYA", "Girya"},
        {"GLASS_EYE", "Glass Eye"},
        {"GLITTER", "Glitter"},
        {"GNARLED_HAMMER", "Gnarled Hammer"},
        {"GOLD_PLATED_CABLES", "Gold-Plated Cables"},
        {"GOLDEN_COMPASS", "Golden Compass"},
        {"GOLDEN_PEARL", "Golden Pearl"},
        {"GORGET", "Gorget"},
        {"GREMLIN_HORN", "Gremlin Horn"},
        {"HAND_DRILL", "Hand Drill"},
        {"HAPPY_FLOWER", "Happy Flower"},
        {"HELICAL_DART", "Helical Dart"},
        {"HISTORY_COURSE", "History Course"},
        {"HORN_CLEAT", "Horn Cleat"},
        {"ICE_CREAM", "Ice Cream"},
        {"INFUSED_CORE", "Infused Core"},
        {"INTIMIDATING_HELMET", "Intimidating Helmet"},
        {"IRON_CLUB", "Iron Club"},
        {"IVORY_TILE", "Ivory Tile"},
        {"JEWELED_MASK", "Jeweled Mask"},
        {"JEWELRY_BOX", "Jewelry Box"},
        {"JOSS_PAPER", "Joss Paper"},
        {"JUZU_BRACELET", "Juzu Bracelet"},
        {"KIFUDA", "Kifuda"},
        {"KUNAI", "Kunai"},
        {"KUSARIGAMA", "Kusarigama"},
        {"LANTERN", "Lantern"},
        {"LARGE_CAPSULE", "Large Capsule"},
        {"LASTING_CANDY", "Lasting Candy"},
        {"LAVA_LAMP", "Lava Lamp"},
        {"LAVA_ROCK", "Lava Rock"},
        {"LEAD_PAPERWEIGHT", "Lead Paperweight"},
        {"LEAFY_POULTICE", "Leafy Poultice"},
        {"LEES_WAFFLE", "Lee's Waffle"},
        {"LETTER_OPENER", "Letter Opener"},
        {"LIZARD_TAIL", "Lizard Tail"},
        {"LOOMING_FRUIT", "Looming Fruit"},
        {"LORDS_PARASOL", "Lord's Parasol"},
        {"LOST_COFFER", "Lost Coffer"},
        {"LOST_WISP", "Lost Wisp"},
        {"LUCKY_FYSH", "Lucky Fysh"},
        {"LUNAR_PASTRY", "Lunar Pastry"},
        {"MANGO", "Mango"},
        {"MASSIVE_SCROLL", "Massive Scroll"},
        {"MAW_BANK", "Maw Bank"},
        {"MEAL_TICKET", "Meal Ticket"},
        {"MEAT_CLEAVER", "Meat Cleaver"},
        {"MEAT_ON_THE_BONE", "Meat on the Bone"},
        {"MEMBERSHIP_CARD", "Membership Card"},
        {"MERCURY_HOURGLASS", "Mercury Hourglass"},
        {"METRONOME", "Metronome"},
        {"MINI_REGENT", "Mini Regent"},
        {"MINIATURE_CANNON", "Miniature Cannon"},
        {"MINIATURE_TENT", "Miniature Tent"},
        {"MOLTEN_EGG", "Molten Egg"},
        {"MR_STRUGGLES", "Mr. Struggles"},
        {"MUMMIFIED_HAND", "Mummified Hand"},
        {"MUSIC_BOX", "Music Box"},
        {"MYSTIC_LIGHTER", "Mystic Lighter"},
        {"NEOWS_TORMENT", "Neow's Torment"},
        {"NEW_LEAF", "New Leaf"},
        {"NINJA_SCROLL", "Ninja Scroll"},
        {"NUNCHAKU", "Nunchaku"},
        {"NUTRITIOUS_OYSTER", "Nutritious Oyster"},
        {"NUTRITIOUS_SOUP", "Nutritious Soup"},
        {"ODDLY_SMOOTH_STONE", "Oddly Smooth Stone"},
        {"OLD_COIN", "Old Coin"},
        {"ORANGE_DOUGH", "Orange Dough"},
        {"ORICHALCUM", "Orichalcum"},
        {"ORNAMENTAL_FAN", "Ornamental Fan"},
        {"ORRERY", "Orrery"},
        {"PAELS_BLOOD", "Pael's Blood"},
        {"PAELS_CLAW", "Pael's Claw"},
        {"PAELS_EYE", "Pael's Eye"},
        {"PAELS_FLESH", "Pael's Flesh"},
        {"PAELS_GROWTH", "Pael's Growth"},
        {"PAELS_HORN", "Pael's Horn"},
        {"PAELS_LEGION", "Pael's Legion"},
        {"PAELS_TEARS", "Pael's Tears"},
        {"PAELS_TOOTH", "Pael's Tooth"},
        {"PAELS_WING", "Pael's Wing"},
        {"PANDORAS_BOX", "Pandora's Box"},
        {"PANTOGRAPH", "Pantograph"},
        {"PAPER_KRANE", "Paper Krane"},
        {"PAPER_PHROG", "Paper Phrog"},
        {"PARRYING_SHIELD", "Parrying Shield"},
        {"PEAR", "Pear"},
        {"PEN_NIB", "Pen Nib"},
        {"PENDULUM", "Pendulum"},
        {"PERMAFROST", "Permafrost"},
        {"PETRIFIED_TOAD", "Petrified Toad"},
        {"PHILOSOPHERS_STONE", "Philosopher's Stone"},
        {"PHYLACTERY_UNBOUND", "Phylactery Unbound"},
        {"PLANISPHERE", "Planisphere"},
        {"POCKETWATCH", "Pocketwatch"},
        {"POLLINOUS_CORE", "Pollinous Core"},
        {"POMANDER", "Pomander"},
        {"POTION_BELT", "Potion Belt"},
        {"POWER_CELL", "Power Cell"},
        {"PRAYER_WHEEL", "Prayer Wheel"},
        {"PRECARIOUS_SHEARS", "Precarious Shears"},
        {"PRECISE_SCISSORS", "Precise Scissors"},
        {"PRESERVED_FOG", "Preserved Fog"},
        {"PRISMATIC_GEM", "Prismatic Gem"},
        {"PUMPKIN_CANDLE", "Pumpkin Candle"},
        {"PUNCH_DAGGER", "Punch Dagger"},
        {"RADIANT_PEARL", "Radiant Pearl"},
        {"RAINBOW_RING", "Rainbow Ring"},
        {"RAZOR_TOOTH", "Razor Tooth"},
        {"RED_MASK", "Red Mask"},
        {"RED_SKULL", "Red Skull"},
        {"REGAL_PILLOW", "Regal Pillow"},
        {"REGALITE", "Regalite"},
        {"REPTILE_TRINKET", "Reptile Trinket"},
        {"RING_OF_THE_DRAKE", "Ring of the Drake"},
        {"RING_OF_THE_SNAKE", "Ring of the Snake"},
        {"RINGING_TRIANGLE", "Ringing Triangle"},
        {"RIPPLE_BASIN", "Ripple Basin"},
        {"ROYAL_POISON", "Royal Poison"},
        {"ROYAL_STAMP", "Royal Stamp"},
        {"RUINED_HELMET", "Ruined Helmet"},
        {"RUNIC_CAPACITOR", "Runic Capacitor"},
        {"RUNIC_PYRAMID", "Runic Pyramid"},
        {"SAI", "Sai"},
        {"SAND_CASTLE", "Sand Castle"},
        {"SCREAMING_FLAGON", "Screaming Flagon"},
        {"SCROLL_BOXES", "Scroll Boxes"},
        {"SEA_GLASS", "Sea Glass"},
        {"SEAL_OF_GOLD", "Seal of Gold"},
        {"SELF_FORMING_CLAY", "Self-Forming Clay"},
        {"SERE_TALON", "Sere Talon"},
        {"SHOVEL", "Shovel"},
        {"SHURIKEN", "Shuriken"},
        {"SIGNET_RING", "Signet Ring"},
        {"SILVER_CRUCIBLE", "Silver Crucible"},
        {"SLING_OF_COURAGE", "Sling of Courage"},
        {"SMALL_CAPSULE", "Small Capsule"},
        {"SNECKO_EYE", "Snecko Eye"},
        {"SNECKO_SKULL", "Snecko Skull"},
        {"SOZU", "Sozu"},
        {"SPARKLING_ROUGE", "Sparkling Rouge"},
        {"SPIKED_GAUNTLETS", "Spiked Gauntlets"},
        {"STONE_CALENDAR", "Stone Calendar"},
        {"STONE_CRACKER", "Stone Cracker"},
        {"STONE_HUMIDIFIER", "Stone Humidifier"},
        {"STORYBOOK", "Storybook"},
        {"STRAWBERRY", "Strawberry"},
        {"STRIKE_DUMMY", "Strike Dummy"},
        {"STURDY_CLAMP", "Sturdy Clamp"},
        {"SWORD_OF_JADE", "Sword of Jade"},
        {"SWORD_OF_STONE", "Sword of Stone"},
        {"SYMBIOTIC_VIRUS", "Symbiotic Virus"},
        {"TANXS_WHISTLE", "Tanx's Whistle"},
        {"TEA_OF_DISCOURTESY", "Tea of Discourtesy"},
        {"THE_ABACUS", "The Abacus"},
        {"THE_BOOT", "The Boot"},
        {"THE_COURIER", "The Courier"},
        {"THROWING_AXE", "Throwing Axe"},
        {"TINGSHA", "Tingsha"},
        {"TINY_MAILBOX", "Tiny Mailbox"},
        {"TOASTY_MITTENS", "Toasty Mittens"},
        {"TOOLBOX", "Toolbox"},
        {"TOUCH_OF_OROBAS", "Touch of Orobas"},
        {"TOUGH_BANDAGES", "Tough Bandages"},
        {"TOXIC_EGG", "Toxic Egg"},
        {"TOY_BOX", "Toy Box"},
        {"TRI_BOOMERANG", "Tri-Boomerang"},
        {"TUNGSTEN_ROD", "Tungsten Rod"},
        {"TUNING_FORK", "Tuning Fork"},
        {"TWISTED_FUNNEL", "Twisted Funnel"},
        {"UNCEASING_TOP", "Unceasing Top"},
        {"UNDYING_SIGIL", "Undying Sigil"},
        {"UNSETTLING_LAMP", "Unsettling Lamp"},
        {"VAJRA", "Vajra"},
        {"VAMBRACE", "Vambrace"},
        {"VELVET_CHOKER", "Velvet Choker"},
        {"VENERABLE_TEA_SET", "Venerable Tea Set"},
        {"VERY_HOT_COCOA", "Very Hot Cocoa"},
        {"VEXING_PUZZLEBOX", "Vexing Puzzlebox"},
        {"VITRUVIAN_MINION", "Vitruvian Minion"},
        {"WAR_HAMMER", "War Hammer"},
        {"WAR_PAINT", "War Paint"},
        {"WHETSTONE", "Whetstone"},
        {"WHISPERING_EARRING", "Whispering Earring"},
        {"WHITE_BEAST_STATUE", "White Beast Statue"},
        {"WHITE_STAR", "White Star"},
        {"WING_CHARM", "Wing Charm"},
        {"WONGO_CUSTOMER_APPRECIATION_BADGE", "Wongo Customer Appreciation Badge"},
        {"WONGOS_MYSTERY_TICKET", "Wongo's Mystery Ticket"},
        {"YUMMY_COOKIE", "Yummy Cookie"},
    };

    private static readonly IReadOnlyDictionary<string, string> _eventEN = new Dictionary<string, string>
    {
        {"ABYSSAL_BATHS", "Abyssal Baths"},
        {"AMALGAMATOR", "Amalgamator"},
        {"AROMA_OF_CHAOS", "Aroma of Chaos"},
        {"BATTLEWORN_DUMMY", "Battleworn Dummy"},
        {"BRAIN_LEECH", "Brain Leech"},
        {"BUGSLAYER", "Bugslayer"},
        {"BYRDONIS_NEST", "Byrdonis Nest"},
        {"COLORFUL_PHILOSOPHERS", "Colorful Philosophers"},
        {"COLOSSAL_FLOWER", "Colossal Flower"},
        {"CRYSTAL_SPHERE", "Crystal Sphere"},
        {"DENSE_VEGETATION", "Dense Vegetation"},
        {"DOLL_ROOM", "Doll Room"},
        {"DOORS_OF_LIGHT_AND_DARK", "Doors of Light and Dark"},
        {"DROWNING_BEACON", "Drowning Beacon"},
        {"ENDLESS_CONVEYOR", "Endless Conveyor"},
        {"FAKE_MERCHANT", "The Merchant???"},
        {"FIELD_OF_MAN_SIZED_HOLES", "Field of Man-Sized Holes"},
        {"GRAVE_OF_THE_FORGOTTEN", "Grave of the Forgotten"},
        {"HUNGRY_FOR_MUSHROOMS", "Hungry for Mushrooms"},
        {"INFESTED_AUTOMATON", "Infested Automaton"},
        {"JUNGLE_MAZE_ADVENTURE", "Jungle Maze Adventure"},
        {"LOST_WISP", "The Lost Wisp"},
        {"LUMINOUS_CHOIR", "Luminous Choir"},
        {"MORPHIC_GROVE", "Morphic Grove"},
        {"POTION_COURIER", "Potion Courier"},
        {"PUNCH_OFF", "Punch Off"},
        {"RANWID_THE_ELDER", "Ranwid the Elder"},
        {"REFLECTIONS", "Reflections snoitcelfeR"},
        {"RELIC_TRADER", "Relic Trader"},
        {"ROOM_FULL_OF_CHEESE", "Room Full of Cheese"},
        {"ROUND_TEA_PARTY", "The Round Tea Party"},
        {"SAPPHIRE_SEED", "Sapphire Seed"},
        {"SELF_HELP_BOOK", "Self-Help Book"},
        {"SLIPPERY_BRIDGE", "Slippery Bridge"},
        {"SPIRALING_WHIRLPOOL", "Spiraling Whirlpool"},
        {"SPIRIT_GRAFTER", "Spirit Grafter"},
        {"STONE_OF_ALL_TIME", "Stone of All Time"},
        {"SUNKEN_STATUE", "The Sunken Statue"},
        {"SUNKEN_TREASURY", "Sunken Treasury"},
        {"SYMBIOTE", "Symbiote"},
        {"TABLET_OF_TRUTH", "Tablet of Truth"},
        {"TEA_MASTER", "Tea Master"},
        {"THE_FUTURE_OF_POTIONS", "The Future of Potions?"},
        {"THE_LANTERN_KEY", "The Lantern Key"},
        {"THE_LEGENDS_WERE_TRUE", "The Legends Were True"},
        {"THIS_OR_THAT", "This or That?"},
        {"TINKER_TIME", "Tinker Time"},
        {"TRASH_HEAP", "Trash Heap"},
        {"TRIAL", "The Trial"},
        {"UNREST_SITE", "Unrest Site"},
        {"WAR_HISTORIAN_REPY", "War Historian, Repy"},
        {"WATERLOGGED_SCRIPTORIUM", "Waterlogged Scriptorium"},
        {"WELCOME_TO_WONGOS", "Welcome to Wongo's"},
        {"WELLSPRING", "Wellspring"},
        {"WHISPERING_HOLLOW", "Whispering Hollow"},
        {"WOOD_CARVINGS", "Wood Carvings"},
        {"ZEN_WEAVER", "Zen Weaver"},
    };

    private static readonly IReadOnlyDictionary<string, string> _monsterEN = new Dictionary<string, string>
    {
        // Monster display names from KnowledgeBase — used when the game's
        // LocManager returns a non-English name (mod set to EN, game in CN).
        // Only entries where KB has a non-empty English name are listed.
        // For KB entries with empty name.en, the raw ID is returned as
        // a last-resort fallback (still better than Chinese text).
    };
}
