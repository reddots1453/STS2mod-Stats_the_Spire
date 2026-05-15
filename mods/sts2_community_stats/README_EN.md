# Stats the Spire — STS2 Community Stats Mod

**Version**: 0.14.0  
**For**: Slay the Spire 2 (Early Access v0.99.x+)  
**Type**: Info display only, no gameplay impact

---

## What Does This Mod Do?

Stats the Spire displays potion drop chances, enemy intents, in-combat contribution breakdowns, and more to help you make better decisions during your climb. It collects both your local data and community data from all subscribed players, aggregating them into live community statistics shown directly in the game UI. Helping you make smarter choices — or showing you just how wild your choices really were.

---

## Feature Overview

### 0. STS1 Mod Migration: Infomod + Intent Graph


### Infomod

**Potion Chance Tracker**

An indicator on the top bar tracks your current potion drop chance. Hover over it to see elite combat drop chance and the probability of seeing at least one potion after multiple combats.

**Unknown Room Preview**

Hover over unvisited unknown rooms to see the full probability list of what the next unknown room encounter could be. Relics that affect unknown room encounter chances are factored into the calculation.

**Card Drop Chance**

An indicator on the top bar shows the likelihood of seeing each rarity tier in your next combat reward.

**Shop Prices**

Hover over unvisited shop rooms to see expected prices at the next shop. Prices are color-coded so you can easily tell whether you can afford a given item with your current gold. Relics that affect shop prices are factored into the price calculation.




### Intent Graph

Displays monster intents as a state machine. Hover over a monster to view its intent state machine.

**Intent**
Shows what the monster will do next turn.

**Intent Group**
A group of intents enclosed in a blue box, representing all possible actions the monster may take next turn.
Top-left: probability.
Top-right: the maximum number of consecutive uses for this intent.

**Intent Transitions**
Always starts from the "Initial" intent.
On the following turn, follows the yellow arrows to the next intent or intent group.



### 1. Card Reward Screen — Community Pick Rate & Win Rate

On the card reward screen, displayed below each card:

```
Pick 72% | Win 54%
```

- **Pick**: the proportion of times the community picked this card when it appeared in a reward
- **Win**: the proportion of players who picked this card that went on to win the run
- Color coding: green (high win rate) / yellow (medium) / red (low win rate)

### 2. Event Options — Community Selection Rate & Win Rate

Displayed next to event option buttons:

```
Pick 64% | Win 48%
```

### 3. Map Nodes — Encounter Danger Level

```
Death 3.2% | Avg Damage 18
```

### 4. Combat Contribution Chart

**Automatically pops up after each combat** (close with F8), showing the contribution of every card, relic, and potion during that combat:

```
Vs. Gremlin Nob
────────────────────────────
Damage
  Strike     x3  ████████████████  64  (42%)
  Bash       x1  ██████████  40  (26%)
  [R] Vajra       █████  20  (13%)      ← Relic contribution
  [P] Fire Potion ████  16  (10%)       ← Potion contribution
────────────────────────────
Defense
  Defend     x3  ████████████  30  (60%)
  [R] Orichalcum  ██████  15  (30%)
────────────────────────────
Card Draw
  Battle Trance x1 ████████████  3  (75%)
  [R] Bag          ████  1  (25%)
────────────────────────────
Energy Gained
  Bloodletting x1 ████████████  2  (100%)
```

**Run Summary** additionally includes a **Healing** section:

```
Healing
  [R] Burning Blood  ████████████  24  (48%)
  Rest Site          ██████████  20  (40%)
  [Event] Abyss Bath ███  6  (12%)
```

**Highlights**:
- Cards (blue), relics (gold), potions (cyan) mixed and sorted together in one chart
- **Indirect damage tracking**: poison, Vulnerable bonus damage etc. attributed to the card that applied the effect
- **Modifier attribution**: Strength (purple segment), Vulnerable bonuses etc. are attributed to the source that provided the modifier (e.g., Inflame, Demon Form), not to the attack card
- **Power indirect effects**: Rage block, Flame Barrier reflect, Juggernaut damage, Inferno damage etc. correctly attributed
- **Defense tracking**: FIFO block attribution + Dexterity bonus + Weak mitigation + Buffer/Intangible mitigation
- **Card draw / energy** displayed independently per source
- **Potion contributions**: Strength potions, draw potions, block potions etc. all correctly attributed
- **Healing stats** (run summary): rest sites, relics (Burning Blood), event healing / max HP increases, floor regen shown separately
- **Sub-bars**: transformed/generated cards (e.g., Giant Rock, Shiv) displayed as indented sub-bars under their source card
- **Upgrade contributions**: damage/block bonuses from in-combat upgrades (e.g., Armaments) shown as orange segments
- `x3` shows number of times played, `[R]` marks relics, `[P]` marks potions

---

#### Detailed Contribution Rules

The combat contribution panel shows exactly how much each card, relic, and potion contributed during a combat. Every effect is tracked and attributed to **the original card (or relic, or potion) that produced it**.

---

##### How Damage Is Assigned

**Direct damage** goes to the card that dealt it. If Strike deals 6 damage, Strike gets 6 direct damage.

**Strength bonus** does NOT go to Strike — it goes to the card that gave you the Strength. If Inflame gave you 3 Strength, the extra 3 damage on every subsequent attack card is credited to Inflame — because the root cause of the higher damage is Inflame, not the attack card itself. Strength contributions appear as **purple segments** in the chart.

**Vulnerable bonus** follows the same logic. An enemy under Vulnerable takes 50% extra damage — that extra 50% is attributed to the card that applied Vulnerable. If you have the Paper Phrog relic (increasing Vulnerable to 75%), the additional 25% goes to Paper Phrog. If Cruelty further amplifies Vulnerable, Cruelty gets its share. If Debilitate doubles the entire effect, the doubled portion goes to Debilitate. Every source receives credit proportional to its contribution.

**Multiple sources stack proportionally**. If you use Inflame (+2 Strength) and then a Strength Potion (+2 Strength) for a total of +4, each contributed half and gets 50% of the Strength bonus credit.

**Poison, Thorns, Flame Barrier, and other indirect damage** — these are not "played" but triggered automatically by powers. They are attributed to **the card that originally applied the power**. If you apply 6 Poison to an enemy, every turn's poison tick damage goes to the card that applied it. If multiple cards applied poison to the same enemy, each tick's damage is split proportionally by remaining stacks from each source.

---

##### How Defense Is Calculated

When an enemy attacks you, damage is reduced through five layers, applied in order. Each layer's reduction is credited to the source that created that layer of defense.

**Layer 1: Reduce enemy Strength.** Every point of Strength you remove from an enemy reduces their attack damage by the same amount per hit. This reduction is credited to the card that reduced the Strength (e.g., Disarm).

**Layer 2: Debuffs on the enemy.** Weak reduces enemy damage to 75% — the 25% prevented is credited to the card that applied Weak. If you have the Paper Krane relic, Weak becomes 60% — the extra 15% goes to Paper Krane. If Debilitate doubles Weak, the doubled portion goes to Debilitate. Other debuffs (such as Constrict ×0.50) work the same way.

**Layer 3: Intangible.** Each hit is capped at 1 damage. All damage above 1 is prevented, credited to the card that applied Intangible.

**Layer 4: Block.** Your block value directly absorbs damage. Block is consumed in **the order it was generated** — first generated, first consumed. If you have both base block and Dexterity-boosted block, the base portion goes to the card that produced the block, and the Dexterity bonus goes to the source that provided Dexterity.

**Layer 5: Buffer.** If block is broken through, each Buffer layer can completely negate one attack. For example: enemy attacks 4 times for 5 each, you have 2 Buffer layers and 7 Block. Block absorbs the first two hits (5+2), the 3 penetrating damage from the third hit plus all 5 from the fourth are each negated by a Buffer layer. Each layer's actual prevented amount is recorded, credited to the card that applied Buffer.

> **Colossus special case**: when the attacker has Vulnerable, Colossus halves your damage taken. This reduction is credited to Colossus.

---

##### Layer Consumption Rules

Vulnerable, Weak, and Poison have stacks and durations in the game. The mod tracks which source contributed each individual layer, and consumes them following the game's actual logic.

**Vulnerable and Weak (toggle-type):** their effect only cares about "present or not", not "how many stacks". Each turn, whichever source's layer is at the front of the queue (applied first) gets full credit for that turn's entire effect. At turn end, the front layer loses 1 stack — when it expires, it leaves the queue and the next source takes over. First-applied, first-consumed — matching the game's actual behavior.

**Poison (cumulative-type):** each turn's poison damage is split proportionally by remaining stacks from each source. If source A applied 4 stacks and source B applied 6, this turn's tick gives A 40% and B 60%. After the tick, stacks decay — the first-applied stacks decay first, and when they expire, later-applied stacks move up.

**Doom:** same mechanism — first-applied Doom layers are first to detonate. Self-Doom (e.g., Borrowed Time) is completely excluded and does not interfere with enemy Doom attribution.

---

##### Orb Attribution

An orb's passive damage belongs to whoever generated it. If Electrify creates a Lightning Orb, the orb's passive damage each turn goes to Electrify.

But if you use Dualcast to evoke someone else's orb — the 1st evoke still goes to the orb's creator (Electrify), while the 2nd and any subsequent bonus evokes go to the evoker (Dualcast). Because the 1st evoke is intrinsic to the orb; extra evocations are actively triggered by the evoker.

---

##### Other Rules

**Self-damage**: cards like Bloodletting and Blood Wall that damage the player are shown as a red segment in the Defense section. If a card provides both defense and self-damage, it is split into two rows — positive defense shown normally, self-damage shown as an indented red bar separately, excluded from the defense percentage calculation.

**In-combat upgrades**: if Armaments upgrades Strike (damage 6→9), the extra damage goes to Armaments, not Strike. Multi-hit attacks (e.g., Strike ×4) correctly account for the upgrade delta per hit. Upgrade contributions appear as **orange segments**.

**Card draw and energy**: the 5 cards drawn naturally each turn are excluded. Extra draws are credited to the card/relic that triggered them. The fixed +3 energy per turn is excluded; extra energy goes to the source. Cost reduction effects (e.g., Enlightenment) and Snecko Eye's random cost changes (which can be negative) are credited to their respective sources.

**Healing**: only shown in the run summary panel. Rest sites credited to Rest Site; post-combat healing to relics (e.g., Burning Blood); event healing to the event; floor transition regen is tagged separately.

---

##### Color Legend

| Color | Meaning |
|---|---|
| **Blue** | Direct damage / direct block |
| **Purple** | Modifier bonus (extra damage/block from Strength, Vulnerable, Dexterity, etc.) |
| **Orange** | Upgrade bonus (e.g., Armaments upgrading other cards) |
| **Red** | Self-damage (damage dealt to yourself) |
| **Gold bar** | Relic contribution |
| **Cyan bar** | Potion contribution |
| **Green bar** | Healing contribution |

##### Source Markers

| Marker | Meaning |
|---|---|
| `[R]` | Relic |
| `[P]` | Potion |
| `[E]` | Event |
| `[Event]` | Event healing |
| `└ sub-bar` | Card generated by the source above (e.g., Shiv originating from Infinite Blades) |

### 5. Global Contribution Summary

Press **F8** anytime to open the contribution panel, with two tabs:

| Tab | Content |
|------|------|
| **Last Combat** | Detailed contributions from the most recent combat |
| **Run Summary** | Cumulative contributions across all combats this run |

### 6. Settings Panel

Press **F9** to open the settings panel (also accessible from the Mod Settings screen):

| Setting | Description |
|--------|------|
| **Upload run data** | Enabled by default. Disable to stop sending any data to the server |
| **Auto-match ascension** | Automatically match data near your current ascension level |
| **Min / Max ascension** | Manually set ascension range (0-20) |
| **Min player win rate** | Filter for data from high-win-rate players |
| **Game version** | Current version / All versions |
| **Data branch** | Auto (my branch) / Release / Beta / All — separates release and beta server data |
| **Language** | 中文 / English |

### 7. Shop Purchase Tracking

Records your purchases in shops (cards, relics, potions) and displays community buy rates.

### 8. Card Removal & Upgrade Tracking

- Shows **upgrade rate** when upgrading at a Rest Site
- Shows **removal rate** when removing cards at shops/events

### 9. Relic Win Rate

Hover over relics in your relic bar to see community pick rate and win rate.

### 10. Personal Career Statistics

Go to **Compendium → Career Stats** — the new **Personal Career Stats** tab displays aggregated data from all your local RunHistory:

- **Win rate summary cards**: data range / ascension W-L / ascension win rate / highest win streak, adjustable min ascension (0-10)
- **Win rate trend**: rolling win rate over last 10 / 50 / 100 / all runs
- **Death cause ranking**: Top 10 causes of death, distinguished by combat / event / abandoned run / Ancient Elder icons
- **Deck building** + **Path stats**: per-Act averages for cards obtained / bought / removed / upgraded, plus normal / elite / ? room / shop / rest site counts
- **Ancient relic pick rates**: grouped by Elder, with pick rate / pick count / win rate / win rate delta for each pool
- **Boss damage taken**: average HP loss + death rate for all 4 act bosses (including placeholders for zero encounters)
- Character filter: all characters / single character

### 11. Run History Single-Run Stats

In **Run History details**, a **📊 Run Stats** button appears in the top right. Click to open a centered popup showing that specific run's:

- Character · Ascension · Floor · Victory/Defeat
- Deck building / Path stats (per-Act tables)
- Ancient relic picks (with Elder avatar + Chinese name + relic icon)
- Boss damage taken (HP lost per boss fight)
- **View Contribution Chart** button: review all combat contributions for this run

Popup supports ✕ / Esc / clicking the dimmed background to close.

### 12. Card Library & Relic Collection Personal Stats

In **Compendium → Card Library / Relic Collection**, each card or relic's detail panel shows your personal sample size + pick rate + win rate + upgrade rate + removal rate + buy rate, displayed alongside community data for comparison.

---

## Hotkeys

| Hotkey | Function |
|--------|------|
| **F8** | Open/close contribution stats panel |
| **F9** | Open/close settings panel |

---

## Data Information

### When is data uploaded?

**Local history data is uploaded when the mod is first loaded.**
**After that, only one upload at the end of each Run** (win or lose). You can disable upload in settings.

### What data is uploaded?

| Data | Description |
|------|------|
| Character & ascension level | Used for category filtering |
| Win/loss result & floor reached | Used for win rate calculation |
| Each card reward choice | Which cards appeared, which one was picked |
| Event option choices | Which option was selected |
| Shop purchases | What was bought, cost in gold |
| Card removal/upgrade records | Which card was removed/upgraded |
| Encounter data | Enemy ID, damage taken, turns taken, whether player died |
| Combat contribution data | Damage/defense/draw etc. per card/relic/potion |
| Data branch | Release / Beta, auto-detected from Steam branch |
| Final deck & relics | Deck and relic list at run end |

### What is NOT uploaded

- **No** Steam ID or any personally identifiable information
- **No** system information or other game data

### Offline Mode

If the server is unreachable:
- Upload data is automatically saved to a local queue and retried when connectivity returns
- Built-in test data serves as a fallback.

---

## Data Storage Location

```
%AppData%/sts2_community_stats/
├── cache/          # Disk cache (stats data)
├── pending/        # Pending upload queue (offline)
└── settings.json   # Filter settings
```

---

## Installation

1. Place the `sts2_community_stats` folder into the game directory's `mods/` folder
2. Launch the game — the mod loads automatically
3. Start a new Run, and stats will load and display automatically

### File Structure

```
mods/sts2_community_stats/
├── manifest.json              # Mod metadata
├── config.json                # API server config
├── sts2_community_stats.dll   # Mod main program
├── README.md                  # This document
└── test/
    └── test_data.json         # Built-in test/fallback data
```

---

## FAQ

**Q: Does this mod affect gameplay balance?**  
A: No. The mod is marked `affects_gameplay: false` and only displays information.

**Q: Can I disable data upload?**  
A: Yes. Press F9 to open settings and uncheck "Upload run data".

**Q: Why does data show "No Data"?**  
A: There may not be enough samples for that card/event yet, or the ID is not included in the test data.

---

## Version Compatibility

The mod automatically detects the current game version. If cards/relics are renamed during Early Access, the server automatically handles ID migration mapping.
