using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;

namespace KbExtractor;

public static class Extractor
{
    // Output directory next to the repo root
    private static string _outDir = "";
    private static bool _isEnglish;
    // Cached EN strings collected in a separate pass
    private static readonly Dictionary<string, string> _enCache = new();

    public static void Run()
    {
        // Output path: use APPDATA-based fixed path to avoid DLL-location ambiguity
        // (DLL may run from game dir or repo dir depending on how mod is loaded)
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        _outDir = System.IO.Path.Combine(appData, "sts2_kb_extractor", "KnowledgeBase");
        // Also try repo path if running from dev environment
        var dllDir = System.IO.Path.GetDirectoryName(typeof(Extractor).Assembly.Location) ?? ".";
        var repoKb = System.IO.Path.GetFullPath(System.IO.Path.Combine(dllDir, "..", "..", "KnowledgeBase"));
        if (Directory.Exists(System.IO.Path.GetDirectoryName(repoKb)))
            _outDir = repoKb;
        Directory.CreateDirectory(_outDir);
        Directory.CreateDirectory(System.IO.Path.Combine(_outDir, "docs"));

        GD.Print($"[KBExtractor] Output dir: {_outDir}");
        GD.Print($"[KBExtractor] Current language: {LocManager.Instance.Language}");

        // Cache bilingual strings in two passes to avoid per-item language switching
        _isEnglish = LocManager.Instance.Language == "eng";
        GD.Print("[KBExtractor] Collecting bilingual strings...");
        CollectAllEnglishStrings();
        GD.Print("[KBExtractor] EN cache: " + _enCache.Count + " entries");

        GD.Print("[KBExtractor] Extracting cards...");
        var cards = ExtractCards();
        GD.Print($"[KBExtractor] Cards: {cards.Count}");
        var relics = ExtractRelics();
        GD.Print($"[KBExtractor] Relics: {relics.Count}");
        var potions = ExtractPotions();
        GD.Print($"[KBExtractor] Potions: {potions.Count}");
        var powers = ExtractPowers();
        GD.Print($"[KBExtractor] Powers: {powers.Count}");
        var monsters = ExtractMonsters();
        GD.Print($"[KBExtractor] Monsters: {monsters.Count}");
        var encounters = ExtractEncounters();
        GD.Print($"[KBExtractor] Encounters: {encounters.Count}");
        var events = ExtractEvents();
        GD.Print($"[KBExtractor] Events: {events.Count}");

        // Write JSON
        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        WriteJson("cards.json", cards, jsonOpts);
        WriteJson("relics.json", relics, jsonOpts);
        WriteJson("potions.json", potions, jsonOpts);
        WriteJson("powers.json", powers, jsonOpts);
        WriteJson("monsters.json", monsters, jsonOpts);
        WriteJson("encounters.json", encounters, jsonOpts);
        WriteJson("events.json", events, jsonOpts);

        // Write Markdown docs
        WriteGlossary();
        WriteCardDocs(cards);
        WriteRelicDoc(relics);
        WritePotionDoc(potions);
        WriteMonsterDoc(monsters);
        WriteEncounterDoc(encounters);
        WriteEventDoc(events);
        WritePowerDoc(powers);

        GD.Print($"[KBExtractor] Done! {cards.Count} cards, {relics.Count} relics, {potions.Count} potions, {powers.Count} powers, {monsters.Count} monsters, {encounters.Count} encounters, {events.Count} events");
    }

    // ═══════════════════════════════════════════════════════════
    // Localization helpers
    // ═══════════════════════════════════════════════════════════

    private static string Loc(string table, string key)
    {
        try
        {
            var loc = new LocString(table, key);
            var text = loc.GetRawText();
            return string.IsNullOrEmpty(text) || text == key ? "" : text;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns (en, cn) text. In pass 1 (normal language), reads current text.
    /// EN text comes from _enCache if available (populated by CollectEnglishStrings).
    /// </summary>
    private static (string en, string cn) LocBilingual(string table, string key)
    {
        var current = Loc(table, key);
        var fullKey = table + "/" + key;

        if (_isEnglish)
            return (current, _enCache.TryGetValue(fullKey, out var c) ? c : current);

        // Current language is CN; EN comes from cache
        var en = _enCache.TryGetValue(fullKey, out var cached) ? cached : current;
        return (en, current);
    }

    /// <summary>
    /// Batch-collect all English strings for a set of keys.
    /// Call once with override, then stop — avoids per-item toggling.
    /// </summary>
    private static void CollectEnglishStrings(IEnumerable<(string table, string key)> keys)
    {
        if (_isEnglish)
        {
            // Already English, cache current values
            foreach (var (t, k) in keys)
                _enCache[t + "/" + k] = Loc(t, k);
            return;
        }

        try
        {
            LocManager.Instance.StartOverridingLanguageAsEnglish();
            foreach (var (t, k) in keys)
                _enCache[t + "/" + k] = Loc(t, k);
            LocManager.Instance.StopOverridingLanguageAsEnglish();
        }
        catch
        {
            try { LocManager.Instance.StopOverridingLanguageAsEnglish(); } catch { }
        }
    }

    /// <summary>
    /// One-time batch: collect all English loc strings for every entity type.
    /// Does a single StartOverridingLanguageAsEnglish / Stop cycle.
    /// </summary>
    private static void CollectAllEnglishStrings()
    {
        var keys = new List<(string table, string key)>();

        foreach (var card in ModelDb.AllCards)
        {
            var id = card.Id.Entry;
            keys.Add(("cards", id + ".title"));
            keys.Add(("cards", id + ".description"));
        }
        foreach (var relic in ModelDb.AllRelics)
        {
            var id = relic.Id.Entry;
            keys.Add(("relics", id + ".title"));
            keys.Add(("relics", id + ".description"));
            keys.Add(("relics", id + ".flavor"));
        }
        foreach (var potion in ModelDb.AllPotions)
        {
            var id = potion.Id.Entry;
            keys.Add(("potions", id + ".title"));
            keys.Add(("potions", id + ".description"));
        }
        foreach (var monster in ModelDb.Monsters)
        {
            keys.Add(("monsters", monster.Id.Entry + ".title"));
        }
        foreach (var enc in ModelDb.AllEncounters)
        {
            keys.Add(("encounters", enc.Id.Entry + ".title"));
        }
        foreach (var evt in ModelDb.AllEvents)
        {
            var id = evt.Id.Entry;
            keys.Add(("events", id + ".title"));
            keys.Add(("events", id + ".pages.INITIAL.description"));
        }

        // Powers — derive IDs from class names (no instantiation needed)
        var powerType = typeof(PowerModel);
        foreach (var type in powerType.Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(powerType) && !t.IsAbstract))
        {
            var id = SlugifyClassName(type.Name);
            keys.Add(("powers", id + ".title"));
            keys.Add(("powers", id + ".description"));
        }

        CollectEnglishStrings(keys);
    }

    // ═══════════════════════════════════════════════════════════
    // Cards
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractCards()
    {
        var result = new Dictionary<string, object>();
        foreach (var card in ModelDb.AllCards)
        {
            try
            {
                var id = card.Id.Entry;
                var (nameEn, nameCn) = LocBilingual("cards", id + ".title");
                var (descEn, descCn) = LocBilingual("cards", id + ".description");

                // Extract base vars
                var vars = new Dictionary<string, object>();
                foreach (var v in card.DynamicVars.Values)
                {
                    vars[v.Name] = new[] { (int)v.BaseValue };
                }

                // Try to capture upgrade values
                try
                {
                    // Create a fresh instance for upgrade simulation
                    var fresh = (CardModel)Activator.CreateInstance(card.GetType())!;
                    var preVars = new Dictionary<string, int>();
                    foreach (var v in fresh.DynamicVars.Values)
                        preVars[v.Name] = (int)v.BaseValue;

                    if (fresh.IsUpgradable)
                    {
                        fresh.UpgradeInternal();
                        foreach (var v in fresh.DynamicVars.Values)
                        {
                            int pre = preVars.TryGetValue(v.Name, out var p) ? p : 0;
                            int post = (int)v.BaseValue;
                            vars[v.Name] = new[] { pre, post };
                        }
                    }
                }
                catch { /* upgrade sim failed, keep base only */ }

                // Determine character from pool
                string character = "Colorless";
                try
                {
                    var pool = card.Pool;
                    if (pool != null)
                    {
                        foreach (var ch in ModelDb.AllCharacters)
                        {
                            if (ch.CardPool == pool) { character = ch.Id.Entry; break; }
                        }
                    }
                }
                catch { }

                var keywords = new List<string>();
                try { foreach (var kw in card.Keywords) keywords.Add(kw.ToString()); } catch { }

                var tags = new List<string>();
                try { foreach (var t in card.Tags) tags.Add(t.ToString()); } catch { }

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["description"] = new Dictionary<string, string> { ["en"] = descEn, ["cn"] = descCn },
                    ["type"] = card.Type.ToString(),
                    ["rarity"] = card.Rarity.ToString(),
                    ["cost"] = card.EnergyCost.Canonical,
                    ["target"] = card.TargetType.ToString(),
                    ["character"] = character,
                    ["keywords"] = keywords,
                    ["tags"] = tags,
                    ["vars"] = vars
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Card {card.Id.Entry} failed: {ex.Message}");
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // Relics
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractRelics()
    {
        var result = new Dictionary<string, object>();
        foreach (var relic in ModelDb.AllRelics)
        {
            try
            {
                var id = relic.Id.Entry;
                var (nameEn, nameCn) = LocBilingual("relics", id + ".title");
                var (descEn, descCn) = LocBilingual("relics", id + ".description");
                var (flavorEn, flavorCn) = LocBilingual("relics", id + ".flavor");

                var vars = new Dictionary<string, int>();
                try { foreach (var v in relic.DynamicVars.Values) vars[v.Name] = (int)v.BaseValue; } catch { }

                string character = "Shared";
                try
                {
                    foreach (var ch in ModelDb.AllCharacters)
                    {
                        if (ch.RelicPool.AllRelicIds.Contains(relic.Id))
                        { character = ch.Id.Entry; break; }
                    }
                }
                catch { }

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["description"] = new Dictionary<string, string> { ["en"] = descEn, ["cn"] = descCn },
                    ["flavor"] = new Dictionary<string, string> { ["en"] = flavorEn, ["cn"] = flavorCn },
                    ["rarity"] = relic.Rarity.ToString(),
                    ["character"] = character,
                    ["vars"] = vars
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Relic {relic.Id.Entry} failed: {ex.Message}");
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // Potions
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractPotions()
    {
        var result = new Dictionary<string, object>();
        foreach (var potion in ModelDb.AllPotions)
        {
            try
            {
                var id = potion.Id.Entry;
                var (nameEn, nameCn) = LocBilingual("potions", id + ".title");
                var (descEn, descCn) = LocBilingual("potions", id + ".description");

                var vars = new Dictionary<string, int>();
                try { foreach (var v in potion.DynamicVars.Values) vars[v.Name] = (int)v.BaseValue; } catch { }

                string character = "Shared";
                try
                {
                    foreach (var ch in ModelDb.AllCharacters)
                    {
                        if (ch.PotionPool.AllPotionIds.Contains(potion.Id))
                        { character = ch.Id.Entry; break; }
                    }
                }
                catch { }

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["description"] = new Dictionary<string, string> { ["en"] = descEn, ["cn"] = descCn },
                    ["rarity"] = potion.Rarity.ToString(),
                    ["usage"] = potion.Usage.ToString(),
                    ["target"] = potion.TargetType.ToString(),
                    ["character"] = character,
                    ["vars"] = vars
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Potion {potion.Id.Entry} failed: {ex.Message}");
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // Powers
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractPowers()
    {
        var result = new Dictionary<string, object>();
        var powerType = typeof(PowerModel);
        var types = powerType.Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(powerType) && !t.IsAbstract);

        foreach (var type in types)
        {
            try
            {
                // Derive ID from class name using the same slugify logic as ModelId
                var className = type.Name;
                var id = SlugifyClassName(className);

                // Try to instantiate for Type/StackType info
                string powerTypeStr = "Unknown", stackTypeStr = "Unknown";
                try
                {
                    // Try parameterless constructor first
                    var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (ctor != null)
                    {
                        var instance = (PowerModel)ctor.Invoke(null);
                        powerTypeStr = instance.Type.ToString();
                        stackTypeStr = instance.StackType.ToString();
                    }
                    else
                    {
                        // Read Type and StackType from property overrides via reflection
                        var typeProp = type.GetProperty("Type", BindingFlags.Instance | BindingFlags.Public);
                        var stackProp = type.GetProperty("StackType", BindingFlags.Instance | BindingFlags.Public);
                        // Can't read without instance — use defaults
                    }
                }
                catch { }

                var (nameEn, nameCn) = LocBilingual("powers", id + ".title");
                var (descEn, descCn) = LocBilingual("powers", id + ".description");

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["description"] = new Dictionary<string, string> { ["en"] = descEn, ["cn"] = descCn },
                    ["type"] = powerTypeStr,
                    ["stackType"] = stackTypeStr,
                    ["className"] = className
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Power {type.Name} failed: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Convert "StrengthPower" → "STRENGTH_POWER" (same as game's StringHelper.Slugify)
    /// </summary>
    private static string SlugifyClassName(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1]))
                sb.Append('_');
            else if (char.IsUpper(c) && i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && !char.IsUpper(name[i + 1]))
                sb.Append('_');
            sb.Append(char.ToUpper(c));
        }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    // Monsters
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractMonsters()
    {
        var result = new Dictionary<string, object>();
        foreach (var monster in ModelDb.Monsters)
        {
            try
            {
                var id = monster.Id.Entry;
                var (nameEn, nameCn) = LocBilingual("monsters", id + ".title");

                // HP
                int hpMin = 0, hpMax = 0;
                try { hpMin = monster.MinInitialHp; hpMax = monster.MaxInitialHp; } catch { }

                // AI state machine
                var moves = new Dictionary<string, object>();
                string aiSummary = "";
                try
                {
                    var smMethod = monster.GetType().GetMethod("GenerateMoveStateMachine",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var sm = smMethod?.Invoke(monster, null) as MonsterMoveStateMachine;
                    if (sm != null)
                        (moves, aiSummary) = ExtractStateMachine(sm);
                }
                catch { aiSummary = "(could not extract)"; }

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["hp"] = new Dictionary<string, int> { ["min"] = hpMin, ["max"] = hpMax },
                    ["moves"] = moves,
                    ["ai"] = aiSummary
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Monster {monster.Id.Entry} failed: {ex.Message}");
            }
        }
        return result;
    }

    private static (Dictionary<string, object> moves, string summary) ExtractStateMachine(MonsterMoveStateMachine sm)
    {
        var moves = new Dictionary<string, object>();
        var transitions = new List<string>();

        foreach (var (stateId, state) in sm.States)
        {
            if (state is MoveState ms)
            {
                var intentList = new List<string>();
                try
                {
                    foreach (var intent in ms.Intents)
                    {
                        string intentStr = intent.GetType().Name.Replace("Intent", "");
                        if (intent is SingleAttackIntent sai)
                        {
                            try { var dmg = sai.DamageCalc?.Invoke(); intentStr = $"Attack({dmg})"; } catch { }
                        }
                        else if (intent is MultiAttackIntent mai)
                        {
                            try { var dmg = mai.DamageCalc?.Invoke(); intentStr = $"Attack({dmg}x{mai.Repeats})"; } catch { }
                        }
                        intentList.Add(intentStr);
                    }
                }
                catch { }

                var moveInfo = new Dictionary<string, object> { ["intents"] = intentList };
                if (ms.FollowUpStateId != null)
                {
                    moveInfo["followUp"] = ms.FollowUpStateId;
                    transitions.Add($"{stateId} → {ms.FollowUpStateId}");
                }
                moves[stateId] = moveInfo;
            }
            else if (state is RandomBranchState rbs)
            {
                var branches = new List<string>();
                foreach (var sw in rbs.States)
                    branches.Add(sw.stateId);
                transitions.Add($"Random({string.Join(", ", branches)})");
            }
            else if (state is ConditionalBranchState)
            {
                transitions.Add($"Conditional({stateId})");
            }
        }

        return (moves, string.Join(" → ", transitions));
    }

    // ═══════════════════════════════════════════════════════════
    // Encounters
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractEncounters()
    {
        var result = new Dictionary<string, object>();

        // Build act mapping
        var actMapping = new Dictionary<string, List<string>>();
        foreach (var act in ModelDb.Acts)
        {
            var actId = act.Id.Entry;
            try
            {
                foreach (var enc in act.AllEncounters)
                    if (!actMapping.ContainsKey(enc.Id.Entry))
                        actMapping[enc.Id.Entry] = new List<string> { actId };
                    else if (!actMapping[enc.Id.Entry].Contains(actId))
                        actMapping[enc.Id.Entry].Add(actId);
            }
            catch { }
        }

        foreach (var enc in ModelDb.AllEncounters)
        {
            try
            {
                var id = enc.Id.Entry;
                var (nameEn, nameCn) = LocBilingual("encounters", id + ".title");

                var monsterIds = new List<string>();
                try
                {
                    foreach (var m in enc.AllPossibleMonsters)
                        monsterIds.Add(m.Id.Entry);
                }
                catch { }

                var acts = actMapping.TryGetValue(id, out var a) ? a : new List<string>();

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["roomType"] = enc.RoomType.ToString(),
                    ["isWeak"] = enc.IsWeak,
                    ["monsters"] = monsterIds,
                    ["acts"] = acts
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Encounter {enc.Id.Entry} failed: {ex.Message}");
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // Events
    // ═══════════════════════════════════════════════════════════

    private static Dictionary<string, object> ExtractEvents()
    {
        var result = new Dictionary<string, object>();
        foreach (var evt in ModelDb.AllEvents)
        {
            try
            {
                var id = evt.Id.Entry;
                var (nameEn, nameCn) = LocBilingual("events", id + ".title");
                var (descEn, descCn) = LocBilingual("events", id + ".pages.INITIAL.description");

                // Try to get options
                var options = new List<Dictionary<string, object>>();
                try
                {
                    var optMethod = evt.GetType().GetMethod("GenerateInitialOptions",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var optionList = optMethod?.Invoke(evt, null) as System.Collections.IList;
                    if (optionList != null)
                    {
                        int idx = 0;
                        foreach (var opt in optionList)
                        {
                            string optTextEn = "";
                            bool isLocked = false;
                            try
                            {
                                var optType = opt.GetType();
                                var titleProp = optType.GetProperty("Title");
                                var lockedProp = optType.GetProperty("IsLocked");
                                isLocked = (bool)(lockedProp?.GetValue(opt) ?? false);
                                var title = titleProp?.GetValue(opt);
                                if (title != null)
                                {
                                    var getRaw = title.GetType().GetMethod("GetRawText");
                                    optTextEn = getRaw?.Invoke(title, null) as string ?? "";
                                }
                            }
                            catch { }

                            options.Add(new Dictionary<string, object>
                            {
                                ["index"] = idx,
                                ["text_en"] = optTextEn,
                                ["isLocked"] = isLocked
                            });
                            idx++;
                        }
                    }
                }
                catch { /* GenerateInitialOptions may need Owner */ }

                result[id] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = new Dictionary<string, string> { ["en"] = nameEn, ["cn"] = nameCn },
                    ["description"] = new Dictionary<string, string> { ["en"] = descEn, ["cn"] = descCn },
                    ["options"] = options
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KBExtractor] Event {evt.Id.Entry} failed: {ex.Message}");
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // JSON writer
    // ═══════════════════════════════════════════════════════════

    private static void WriteJson(string filename, object data, JsonSerializerOptions opts)
    {
        var path = System.IO.Path.Combine(_outDir, filename);
        var json = JsonSerializer.Serialize(data, opts);
        File.WriteAllText(path, json, Encoding.UTF8);
        GD.Print($"[KBExtractor] Wrote {path} ({json.Length / 1024}KB)");
    }

    // ═══════════════════════════════════════════════════════════
    // Markdown generators
    // ═══════════════════════════════════════════════════════════

    private static void WriteGlossary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# STS2 Glossary / 术语表\n");

        sb.AppendLine("## Card Types / 卡牌类型");
        sb.AppendLine("| EN | CN | Value |");
        sb.AppendLine("|----|----|-------|");
        foreach (var v in Enum.GetValues<CardType>())
            sb.AppendLine($"| {v} | {CardTypeCn(v)} | {(int)v} |");

        sb.AppendLine("\n## Card Rarities / 卡牌稀有度");
        sb.AppendLine("| EN | Value |");
        sb.AppendLine("|----|-------|");
        foreach (var v in Enum.GetValues<CardRarity>())
            sb.AppendLine($"| {v} | {(int)v} |");

        sb.AppendLine("\n## Relic Rarities / 遗物稀有度");
        sb.AppendLine("| EN | Value |");
        sb.AppendLine("|----|-------|");
        foreach (var v in Enum.GetValues<RelicRarity>())
            sb.AppendLine($"| {v} | {(int)v} |");

        sb.AppendLine("\n## Target Types / 目标类型");
        sb.AppendLine("| EN | Value |");
        sb.AppendLine("|----|-------|");
        foreach (var v in Enum.GetValues<TargetType>())
            sb.AppendLine($"| {v} | {(int)v} |");

        sb.AppendLine("\n## Keywords / 关键词");
        sb.AppendLine("| EN | CN |");
        sb.AppendLine("|----|----|");
        foreach (var v in Enum.GetValues<CardKeyword>())
            if (v != CardKeyword.None) sb.AppendLine($"| {v} | {KeywordCn(v)} |");

        sb.AppendLine("\n## Characters / 角色");
        sb.AppendLine("| EN | ID |");
        sb.AppendLine("|----|----|");
        foreach (var ch in ModelDb.AllCharacters)
            sb.AppendLine($"| {ch.Id.Entry} | {ch.Id.Entry} |");

        sb.AppendLine("\n## Acts / 幕");
        sb.AppendLine("| ID | Index |");
        sb.AppendLine("|----|-------|");
        int idx = 0;
        foreach (var act in ModelDb.Acts)
            sb.AppendLine($"| {act.Id.Entry} | {idx++} |");

        File.WriteAllText(System.IO.Path.Combine(_outDir, "glossary.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteCardDocs(Dictionary<string, object> cards)
    {
        // Group by character
        var byChar = new Dictionary<string, List<Dictionary<string, object>>>();
        foreach (var kv in cards)
        {
            var card = (Dictionary<string, object>)kv.Value;
            var ch = (string)card["character"];
            if (!byChar.ContainsKey(ch)) byChar[ch] = new();
            byChar[ch].Add(card);
        }

        foreach (var (character, cardList) in byChar)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {character} Cards\n");

            // Group by rarity
            var byRarity = cardList.GroupBy(c => (string)c["rarity"]).OrderBy(g => g.Key);
            foreach (var group in byRarity)
            {
                sb.AppendLine($"## {group.Key}\n");
                foreach (var card in group.OrderBy(c => GetName(c, "en")))
                {
                    WriteCardEntry(sb, card);
                }
            }

            var filename = $"cards_{character.ToLowerInvariant()}.md";
            File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", filename), sb.ToString(), Encoding.UTF8);
        }
    }

    private static void WriteCardEntry(StringBuilder sb, Dictionary<string, object> card)
    {
        var nameEn = GetName(card, "en");
        var nameCn = GetName(card, "cn");
        sb.AppendLine($"### {nameEn} / {nameCn}");
        sb.AppendLine($"- **ID**: `{card["id"]}` | **Type**: {card["type"]} | **Rarity**: {card["rarity"]} | **Cost**: {card["cost"]} | **Target**: {card["target"]}");

        var kws = (List<string>)card["keywords"];
        if (kws.Count > 0) sb.AppendLine($"- **Keywords**: {string.Join(", ", kws)}");

        var vars = (Dictionary<string, object>)card["vars"];
        if (vars.Count > 0)
        {
            var parts = new List<string>();
            foreach (var (k, v) in vars)
            {
                var arr = (int[])v;
                parts.Add(arr.Length > 1 && arr[0] != arr[1] ? $"{k}: {arr[0]}({arr[1]})" : $"{k}: {arr[0]}");
            }
            sb.AppendLine($"- **Values**: {string.Join(" | ", parts)}");
        }

        var descEn = GetDesc(card, "en");
        var descCn = GetDesc(card, "cn");
        if (!string.IsNullOrEmpty(descEn)) sb.AppendLine($"- **EN**: {descEn}");
        if (!string.IsNullOrEmpty(descCn) && descCn != descEn) sb.AppendLine($"- **CN**: {descCn}");
        sb.AppendLine();
    }

    private static void WriteRelicDoc(Dictionary<string, object> relics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Relics / 遗物\n");

        var grouped = relics.Values.Cast<Dictionary<string, object>>()
            .GroupBy(r => (string)r["rarity"]).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            sb.AppendLine($"## {group.Key}\n");
            foreach (var relic in group.OrderBy(r => GetName(r, "en")))
            {
                sb.AppendLine($"### {GetName(relic, "en")} / {GetName(relic, "cn")}");
                sb.AppendLine($"- **ID**: `{relic["id"]}` | **Rarity**: {relic["rarity"]} | **Character**: {relic["character"]}");
                var descEn = GetDesc(relic, "en");
                var descCn = GetDesc(relic, "cn");
                if (!string.IsNullOrEmpty(descEn)) sb.AppendLine($"- **EN**: {descEn}");
                if (!string.IsNullOrEmpty(descCn) && descCn != descEn) sb.AppendLine($"- **CN**: {descCn}");
                sb.AppendLine();
            }
        }
        File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", "relics.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WritePotionDoc(Dictionary<string, object> potions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Potions / 药水\n");
        foreach (var kv in potions.OrderBy(p => GetName((Dictionary<string, object>)p.Value, "en")))
        {
            var p = (Dictionary<string, object>)kv.Value;
            sb.AppendLine($"### {GetName(p, "en")} / {GetName(p, "cn")}");
            sb.AppendLine($"- **ID**: `{p["id"]}` | **Rarity**: {p["rarity"]} | **Usage**: {p["usage"]} | **Target**: {p["target"]}");
            var descEn = GetDesc(p, "en");
            var descCn = GetDesc(p, "cn");
            if (!string.IsNullOrEmpty(descEn)) sb.AppendLine($"- **EN**: {descEn}");
            if (!string.IsNullOrEmpty(descCn) && descCn != descEn) sb.AppendLine($"- **CN**: {descCn}");
            sb.AppendLine();
        }
        File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", "potions.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteMonsterDoc(Dictionary<string, object> monsters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Bestiary / 怪物图鉴\n");
        foreach (var kv in monsters.OrderBy(m => GetName((Dictionary<string, object>)m.Value, "en")))
        {
            var m = (Dictionary<string, object>)kv.Value;
            var hp = (Dictionary<string, int>)m["hp"];
            sb.AppendLine($"### {GetName(m, "en")} / {GetName(m, "cn")}");
            sb.AppendLine($"- **ID**: `{m["id"]}` | **HP**: {hp["min"]}-{hp["max"]}");

            var moves = (Dictionary<string, object>)m["moves"];
            if (moves.Count > 0)
            {
                sb.AppendLine("- **Moves**:");
                foreach (var (moveId, moveObj) in moves)
                {
                    var move = (Dictionary<string, object>)moveObj;
                    var intents = (List<string>)move["intents"];
                    var followUp = move.TryGetValue("followUp", out var fu) ? $" → {fu}" : "";
                    sb.AppendLine($"  - `{moveId}`: {string.Join(", ", intents)}{followUp}");
                }
            }
            var ai = (string)m["ai"];
            if (!string.IsNullOrEmpty(ai)) sb.AppendLine($"- **AI**: {ai}");
            sb.AppendLine();
        }
        File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", "bestiary.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteEncounterDoc(Dictionary<string, object> encounters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Encounters / 遭遇\n");

        var grouped = encounters.Values.Cast<Dictionary<string, object>>()
            .GroupBy(e => (string)e["roomType"]).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            sb.AppendLine($"## {group.Key}\n");
            foreach (var enc in group.OrderBy(e => (string)e["id"]))
            {
                var monsters = (List<string>)enc["monsters"];
                var acts = (List<string>)enc["acts"];
                sb.AppendLine($"### {GetName(enc, "en")} / {GetName(enc, "cn")}");
                sb.AppendLine($"- **ID**: `{enc["id"]}` | **Type**: {enc["roomType"]} | **Weak**: {enc["isWeak"]}");
                sb.AppendLine($"- **Monsters**: {string.Join(", ", monsters)}");
                sb.AppendLine($"- **Acts**: {string.Join(", ", acts)}");
                sb.AppendLine();
            }
        }
        File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", "encounters.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteEventDoc(Dictionary<string, object> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Events / 事件\n");
        foreach (var kv in events.OrderBy(e => GetName((Dictionary<string, object>)e.Value, "en")))
        {
            var evt = (Dictionary<string, object>)kv.Value;
            sb.AppendLine($"### {GetName(evt, "en")} / {GetName(evt, "cn")}");
            sb.AppendLine($"- **ID**: `{evt["id"]}`");
            var descEn = GetDesc(evt, "en");
            if (!string.IsNullOrEmpty(descEn)) sb.AppendLine($"- **Description**: {descEn}");

            var options = (List<Dictionary<string, object>>)evt["options"];
            if (options.Count > 0)
            {
                sb.AppendLine("- **Options**:");
                foreach (var opt in options)
                    sb.AppendLine($"  - [{opt["index"]}] {opt["text_en"]}{((bool)opt["isLocked"] ? " *(locked)*" : "")}");
            }
            sb.AppendLine();
        }
        File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", "events.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WritePowerDoc(Dictionary<string, object> powers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Powers / 能力\n");
        sb.AppendLine("| ID | Name EN | Name CN | Type | Stack |");
        sb.AppendLine("|----|---------|---------|------|-------|");
        foreach (var kv in powers.OrderBy(p => (string)((Dictionary<string, object>)p.Value)["id"]))
        {
            var p = (Dictionary<string, object>)kv.Value;
            var name = (Dictionary<string, string>)p["name"];
            sb.AppendLine($"| `{p["id"]}` | {name["en"]} | {name["cn"]} | {p["type"]} | {p["stackType"]} |");
        }
        File.WriteAllText(System.IO.Path.Combine(_outDir, "docs", "powers.md"), sb.ToString(), Encoding.UTF8);
    }

    // ═══════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════

    private static string GetName(Dictionary<string, object> entity, string lang)
    {
        if (entity.TryGetValue("name", out var n) && n is Dictionary<string, string> names)
            return names.TryGetValue(lang, out var v) ? v : "";
        return "";
    }

    private static string GetDesc(Dictionary<string, object> entity, string lang)
    {
        if (entity.TryGetValue("description", out var d) && d is Dictionary<string, string> descs)
            return descs.TryGetValue(lang, out var v) ? v : "";
        return "";
    }

    private static string CardTypeCn(CardType t) => t switch
    {
        CardType.Attack => "攻击", CardType.Skill => "技能", CardType.Power => "能力",
        CardType.Status => "状态", CardType.Curse => "诅咒", CardType.Quest => "任务",
        _ => t.ToString()
    };

    private static string KeywordCn(CardKeyword k) => k switch
    {
        CardKeyword.Exhaust => "消耗", CardKeyword.Ethereal => "虚无",
        CardKeyword.Innate => "固有", CardKeyword.Unplayable => "不可打出",
        CardKeyword.Retain => "保留", CardKeyword.Sly => "灵巧",
        CardKeyword.Eternal => "永恒", _ => k.ToString()
    };
}
