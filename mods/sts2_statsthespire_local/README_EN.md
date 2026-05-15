# Stats the Spire (Local Edition) — STS2 Data Analysis Mod

**Version**: 1.0.0
**For**: Slay the Spire 2 (Early Access v0.99.x+)
**Type**: Info display only, no gameplay impact. No network, fully offline.

---

## What Does This Mod Do?

Stats the Spire (Local Edition) is a pure offline data analysis tool for Slay the Spire 2. It displays potion drop chances, enemy intent state machines, in-combat contribution breakdowns, career statistics from your run history, and more to help you make better decisions during your climb.

**No network. No server. All data is local.**

---

## Feature Overview

### Infomod — In-Run Information

**Potion Chance Tracker**

An indicator on the top bar tracks your current potion drop chance. Hover over it to see elite combat drop chance and the probability of seeing at least one potion after multiple combats.

**Unknown Room Preview**

Hover over unvisited unknown rooms to see the full probability list of what the next unknown room encounter could be. Relics that affect unknown room encounter chances are factored into the calculation.

**Card Drop Chance**

An indicator on the top bar shows the likelihood of seeing each rarity tier in your next combat reward.

**Shop Prices**

Hover over unvisited shop rooms to see expected prices at the next shop. Prices are color-coded so you can easily tell whether you can afford a given item with your current gold. Relics that affect shop prices are factored into the price calculation.

### Intent Graph — Monster AI Visualization

Displays monster intents as a state machine. Hover over a monster to view its intent state machine.

**Intent** — Shows what the monster will do next turn.

**Intent Group** — A group of intents enclosed in a blue box, representing all possible actions the monster may take next turn. Top-left: probability. Top-right: the maximum number of consecutive uses for this intent.

**Intent Transitions** — Always starts from the "Initial" intent. On the following turn, follows the yellow arrows to the next intent or intent group.

### Combat Contribution Chart

**Automatically pops up after each combat** (toggle with F8), showing the contribution of every card, relic, and potion during that combat:

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

**Run Summary** additionally includes a **Healing** section.

**Highlights**:
- Cards, relics, potions mixed and sorted together in one chart
- **Indirect damage tracking**: poison, Vulnerable bonus damage etc. attributed to the card that applied the effect
- **Modifier attribution**: Strength, Vulnerable bonuses etc. attributed to the source that provided the modifier
- **Defense tracking**: FIFO block attribution + Dexterity bonus + Weak mitigation + Buffer/Intangible mitigation
- **Card draw / energy** displayed independently per source
- **Potion contributions** correctly attributed
- **Sub-bars**: transformed/generated cards displayed as indented sub-bars under their source card
- **Upgrade contributions**: in-combat upgrade bonuses shown as orange segments
- **Real-time updates**: open the panel during combat to see live contribution data

#### Detailed Contribution Rules

The combat contribution panel shows exactly how much each card, relic, and potion contributed during a combat. Every effect is tracked and attributed to **the original card (or relic, or potion) that produced it**.

##### How Damage Is Assigned

**Direct damage** goes to the card that dealt it.

**Strength bonus** does NOT go to the attack card — it goes to the card that gave you the Strength.

**Vulnerable bonus** follows the same logic. The extra damage from Vulnerable is attributed to the card that applied Vulnerable.

**Multiple sources stack proportionally**.

**Poison, Thorns, Flame Barrier, and other indirect damage** are attributed to the card that originally applied the power.

##### How Defense Is Calculated

Damage is reduced through five layers, applied in order:
1. **Reduce enemy Strength** — credited to the card that reduced Strength
2. **Debuffs on the enemy** (Weak, etc.) — credited to the source that applied the debuff
3. **Intangible** — credited to the source that applied Intangible
4. **Block** — consumed FIFO (first-generated, first-consumed)
5. **Buffer** — credited to the source that applied Buffer

##### Other Rules

- **Self-damage**: shown as a red segment in the Defense section, excluded from percentage calculation
- **In-combat upgrades**: extra damage/block goes to the upgrade source, shown as orange segments
- **Orb attribution**: passive damage belongs to the orb's creator; extra evokes go to the evoker

##### Color Legend

| Color | Meaning |
|---|---|
| **Blue** | Direct damage / direct block |
| **Purple** | Modifier bonus (Strength, Vulnerable, Dexterity, etc.) |
| **Orange** | Upgrade bonus (e.g., Armaments upgrading other cards) |
| **Red** | Self-damage |
| **Gold bar** | Relic contribution |
| **Cyan bar** | Potion contribution |
| **Green bar** | Healing contribution |

### Global Contribution Summary

Press **F8** anytime to open the contribution panel, with two tabs:

| Tab | Content |
|------|------|
| **This Combat** | Detailed contributions from the current (or most recent) combat |
| **Run Summary** | Cumulative contributions across all combats this run |

### Personal Career Statistics

Go to **Compendium → Character Data → Personal Career Stats** to see aggregated data from all your local RunHistory:

- **Win rate summary cards**: data range / ascension W-L / ascension win rate / highest win streak
- **Win rate trend**: rolling win rate over last 10 / 50 / 100 / all runs
- **Death cause ranking**: Top causes of death by encounter
- **Deck building + Path stats**: per-Act averages for cards obtained / bought / removed / upgraded, plus monster / elite / ? room / shop / rest site counts
- **Ancient relic pick rates**: grouped by Elder, with pick rate / pick count / win rate / win rate delta for each pool
- **Boss damage taken**: average HP loss + death rate for all act bosses
- Character filter: all characters / single character

### Run History Single-Run Stats

In **Run History details**, a **Run Stats** button appears. Click to open a popup showing that specific run's:

- Character · Ascension · Floor · Victory/Defeat
- Deck building / Path stats (per-Act tables)
- Ancient relic picks
- Boss damage taken
- **View Contribution Chart** button: review all combat contributions for this run

### Card Library & Relic Collection Personal Stats

In **Compendium → Card Library / Relic Collection**, each card or relic's detail panel shows your personal sample size + pick rate + win rate + upgrade rate + removal rate + buy rate, aggregated from your local run history.

---

## Hotkeys

| Hotkey | Function |
|--------|------|
| **F7** | Open/close settings panel |
| **F8** | Open/close contribution stats panel |

---

## Settings Panel (F7)

| Setting | Description |
|--------|------|
| **Language** | 中文 / English |
| **Feature Toggles** | Enable/disable individual sub-features: Contribution Panel, Card Library Stats, Relic Stats, Unknown Room Odds, Shop Prices, Intent State Machine |

---

## Data Storage Location

```
%AppData%/sts2_community_stats/
├── contributions/   # Per-combat and run summary contribution data
└── settings.json    # Mod settings
```

---

## Installation

1. Place the `sts2_statsthespire_local` folder into the game directory's `mods/` folder
2. Launch the game — the mod loads automatically

### File Structure

```
mods/sts2_statsthespire_local/
├── manifest.json                      # Mod metadata
├── config.json                        # Mod configuration
├── sts2_statsthespire_local.dll       # Mod main program
├── README_EN.md                       # This document
└── README_ZH.md                       # Chinese documentation
```

---

## FAQ

**Q: Does this mod affect gameplay balance?**
A: No. The mod is marked `affects_gameplay: false` and only displays information.

**Q: Does this mod require internet?**
A: No. This is the Local Edition — all data comes from your local run history. No network connection is ever used.

**Q: Why does personal data show 0 samples?**
A: You may not have enough run history yet. Play more runs and the data will accumulate. The mod analyzes your saved `.run` files.

---

## Version Compatibility

The mod works with Slay the Spire 2 Early Access. Card/relic ID changes across game versions are handled automatically.
