#!/usr/bin/env python3
"""Generate 3000 realistic test runs and insert directly into PostgreSQL."""

import random
import hashlib
import json
import sys
import argparse

# ── Game entity pools ──────────────────────────────────────

CHARACTERS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]
VERSIONS = ["v0.98.2", "v0.98.3", "v0.99.1", "v0.101.0"]

CARDS_BY_CHAR = {
    "IRONCLAD": [
        "AGGRESSION", "ANGER", "ARMAMENTS", "ASHEN_STRIKE", "BARRICADE",
        "BASH", "BATTLE_TRANCE", "BLOOD_WALL", "BLOODLETTING", "BLUDGEON",
        "BODY_SLAM", "BRAND", "BREAK", "BREAKTHROUGH", "BULLY",
        "BURNING_PACT", "CORRUPTION", "CRIMSON_MANTLE", "CRUELTY", "DARK_EMBRACE",
        "DEFEND_R", "STRIKE_R", "FLAME_BARRIER", "HEADBUTT", "IRON_WAVE",
        "METALLICIZE", "PUMMEL", "RAGE", "SEEING_RED", "SHRUG_IT_OFF",
    ],
    "SILENT": [
        "ABRASIVE", "ACCELERANT", "ACCURACY", "ACROBATICS", "ADRENALINE",
        "AFTERIMAGE", "ANTICIPATE", "ASSASSINATE", "BACKFLIP", "BACKSTAB",
        "BLADE_DANCE", "BLUR", "BOUNCING_FLASK", "BULLET_TIME", "BURST",
        "CALCULATED_GAMBLE", "CLOAK_AND_DAGGER", "CORROSIVE_WAVE", "DAGGER_SPRAY",
        "DEADLY_POISON", "DEFEND_G", "STRIKE_G", "DODGE_AND_ROLL", "ENVENOM",
        "ESCAPE_PLAN", "FOOTWORK", "LEG_SWEEP", "NEUTRALIZE", "OUTMANEUVER", "PREPARED",
    ],
    "DEFECT": [
        "BALL_LIGHTNING", "BARRAGE", "BEAM_CELL", "BIASED_COGNITION", "BLIZZARD",
        "BOOT_SEQUENCE", "BUFFER", "CAPACITOR", "CHARGE_BATTERY", "CHILL",
        "COLD_SNAP", "COMPILE_DRIVER", "CONSUME", "COOLHEADED", "CORE_SURGE",
        "CREATIVE_AI", "DEFRAGMENT", "DOOM_AND_GLOOM", "DUALCAST", "ECHO_FORM",
        "DEFEND_B", "STRIKE_B", "ELECTRODYNAMICS", "FISSION", "FLUX",
        "FORCE_FIELD", "FTL", "FUSION", "GENETIC_ALGORITHM", "GLACIER",
    ],
    "NECROBINDER": [
        "AFTERLIFE", "BANSHEES_CRY", "BLIGHT_STRIKE", "BODYGUARD", "BONE_SHARDS",
        "BORROWED_TIME", "BURY", "CALCIFY", "CALL_OF_THE_VOID", "CAPTURE_SPIRIT",
        "CLEANSE", "COUNTDOWN", "DANSE_MACABRE", "DEATH_MARCH", "DEATHBRINGER",
        "DEATHS_DOOR", "DEBILITATE", "DEFILE", "DEFY", "DELAY",
        "DEFEND_N", "STRIKE_N", "EPHEMERAL_LANCE", "FADE", "GRAVE_ROB",
        "HAUNT", "HEX", "LAMENT", "LIFETAP", "PHANTOM_STRIKE",
    ],
    "REGENT": [
        "ALIGNMENT", "ARSENAL", "ASTRAL_PULSE", "BEAT_INTO_SHAPE", "BEGONE",
        "BIG_BANG", "BLACK_HOLE", "BOMBARDMENT", "BULWARK", "BUNDLE_OF_JOY",
        "CELESTIAL_MIGHT", "CHARGE", "CHILD_OF_THE_STARS", "CLOAK_OF_STARS",
        "COLLISION_COURSE", "COMET", "CONQUEROR", "CONVERGENCE", "COSMIC_INDIFFERENCE",
        "CRASH_LANDING", "DEFEND_P", "STRIKE_P", "DECISIONS_DECISIONS",
        "HEIRLOOM_HAMMER", "KINGLY_KICK", "PALE_BLUE_DOT", "PROPHESIZE",
        "SOLAR_STRIKE", "THE_SMITH", "WROUGHT_IN_WAR",
    ],
}

COLORLESS_CARDS = [
    "BANDAGE_UP", "BLIND", "DARK_SHACKLES", "DEEP_BREATH", "DISCOVERY",
    "DRAMATIC_ENTRANCE", "FINESSE", "FLASH_OF_STEEL", "GOOD_INSTINCTS", "IMPATIENCE",
    "JACK_OF_ALL_TRADES", "MADNESS", "PANACEA", "PANACHE", "PURITY",
    "SECRET_TECHNIQUE", "SECRET_WEAPON", "SWIFT_STRIKE", "THINKING_AHEAD", "TRIP",
]

RELICS = [
    "AKABEKO", "ANCHOR", "ART_OF_WAR", "BAG_OF_MARBLES", "BAG_OF_PREPARATION",
    "BEAUTIFUL_BRACELET", "BELLOWS", "BELT_BUCKLE", "BIG_HAT", "BLACK_STAR",
    "BLOOD_VIAL", "BONE_FLUTE", "BOOK_OF_FIVE_RINGS", "BOOKMARK", "BOOMING_CONCH",
    "BURNING_BLOOD", "CRACKED_CORE", "RING_OF_THE_SNAKE", "DUSTY_TOME",
    "GOLDEN_IDOL", "HAPPY_FLOWER", "HORN_CLEAT", "ICE_CREAM", "JUZU_BRACELET",
    "LANTERN", "LIZARD_TAIL", "MANGO", "MARK_OF_PAIN", "MEAT_ON_THE_BONE",
    "MERCURY_HOURGLASS", "MUMMIFIED_HAND", "NUNCHAKU", "ODDLY_SMOOTH_STONE",
    "OMAMORI", "ORICHALCUM", "PEN_NIB", "PHILOSOPHER_STONE", "PRAYER_WHEEL",
    "PRESERVED_INSECT", "RED_SKULL", "REGAL_PILLOW", "SELF_FORMING_CLAY",
    "SINGING_BOWL", "SMILING_MASK", "SNECKO_EYE", "STRANGE_SPOON", "SUNDIAL",
    "THE_BOOT", "TINY_CHEST", "TORII", "TOY_ORNITHOPTER", "TUNGSTEN_ROD",
    "TURNIP", "UNCEASING_TOP", "VAJRA", "VELVET_CHOKER", "WAR_PAINT", "WHETSTONE",
]

ENCOUNTERS = {
    "normal": [
        "CUBEX_CONSTRUCT_NORMAL", "FLYCONID_NORMAL", "FOGMOG_NORMAL",
        "INKLETS_NORMAL", "MAWLER_NORMAL", "NIBBITS_NORMAL", "NIBBITS_WEAK",
        "RUBY_RAIDERS_NORMAL", "SHRINKER_BEETLE_WEAK", "SLIMES_NORMAL",
        "SLIMES_WEAK", "VINE_SHAMBLER_NORMAL", "OVERGROWTH_CRAWLERS",
        "FUZZY_WURM_CRAWLER_WEAK",
    ],
    "elite": [
        "BYGONE_EFFIGY_ELITE", "BYRDONIS_ELITE", "PHROG_PARASITE_ELITE",
        "MAGMA_SENTRY_ELITE", "IRON_AUTOMATON_ELITE",
    ],
    "boss": [
        "CEREMONIAL_BEAST_BOSS", "THE_KIN_BOSS", "VANTOM_BOSS",
        "QUEEN_BEE_BOSS", "THE_ANCIENT_BOSS",
    ],
}

# event_id → { page: [options] }
EVENTS = {
    "ABYSSAL_BATHS":           {"INITIAL": ["IMMERSE", "ABSTAIN"]},
    "AROMA_OF_CHAOS":          {"INITIAL": ["LET_GO", "MAINTAIN_CONTROL"]},
    "BATTLEWORN_DUMMY":        {"INITIAL": ["SETTING_1", "SETTING_2", "SETTING_3"]},
    "BRAIN_LEECH":             {"INITIAL": ["SHARE_KNOWLEDGE", "RIP"]},
    "BUGSLAYER":               {"INITIAL": ["EXTERMINATION", "SQUASH"]},
    "BYRDONIS_NEST":           {"INITIAL": ["EAT", "TAKE"]},
    "CRYSTAL_SPHERE":          {"INITIAL": ["UNCOVER_FUTURE", "PAYMENT_PLAN"]},
    "DENSE_VEGETATION":        {"INITIAL": ["TRUDGE_ON", "REST"]},
    "DOLL_ROOM":               {"INITIAL": ["RANDOM", "TAKE_SOME_TIME", "EXAMINE"]},
    "DOORS_OF_LIGHT_AND_DARK": {"INITIAL": ["LIGHT", "DARK"]},
    "DROWNING_BEACON":         {"INITIAL": ["BOTTLE", "CLIMB"]},
    "HUNGRY_FOR_MUSHROOMS":    {"INITIAL": ["BIG_MUSHROOM", "FRAGRANT_MUSHROOM"]},
    "RELIC_TRADER":            {"INITIAL": ["TOP", "MIDDLE", "BOTTOM"]},
    "TEA_MASTER":              {"INITIAL": ["BONE_TEA", "EMBER_TEA", "TEA_OF_DISCOURTESY"]},
    "WOOD_CARVINGS":           {"INITIAL": ["SNAKE", "BIRD", "TORUS"]},
    "SAPPHIRE_SEED":           {"INITIAL": ["EAT", "PLANT"]},
    "WELLSPRING":              {"INITIAL": ["BOTTLE", "BATHE"]},
    "THIS_OR_THAT":            {"INITIAL": ["PLAIN", "ORNATE"]},
    "REFLECTIONS":             {"INITIAL": ["TOUCH_A_MIRROR", "SHATTER"]},
    "SYMBIOTE":                {"INITIAL": ["APPROACH", "KILL_WITH_FIRE"]},
}

# Ancient events for combo-based stats
ANCIENT_RELIC_POOLS = {
    "OROBAS":     ["SEA_GLASS", "PRISMATIC_GEM", "BONE_FLUTE", "DUSTY_TOME", "GOLDEN_IDOL"],
    "VAKUU":      ["AKABEKO", "LANTERN", "ANCHOR", "BAG_OF_PREPARATION", "HAPPY_FLOWER", "MANGO"],
    "TEZCATARA":  ["VAJRA", "ORICHALCUM", "PEN_NIB", "SUNDIAL", "TURNIP", "WAR_PAINT"],
    "PAEL":       ["BURNING_BLOOD", "MARK_OF_PAIN", "RED_SKULL", "SELF_FORMING_CLAY", "MEAT_ON_THE_BONE"],
    "TANX":       ["SINGING_BOWL", "TORII", "PRAYER_WHEEL", "STRANGE_SPOON", "ICE_CREAM"],
    "NONUPEIPE":  ["JUZU_BRACELET", "OMAMORI", "SMILING_MASK", "PHILOSOPHER_STONE", "SNECKO_EYE"],
    "DARV":       ["LIZARD_TAIL", "TUNGSTEN_ROD", "MERCURY_HOURGLASS", "PRESERVED_INSECT"],
    "NEOW":       ["TINY_CHEST", "HORN_CLEAT", "BAG_OF_MARBLES", "NUNCHAKU", "THE_BOOT"],
}

# COLORFUL_PHILOSOPHERS flat options
PHILOSOPHER_COLORS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]


def generate_runs(n: int, seed: int = 42) -> list[dict]:
    """Generate n run payloads with realistic distributions."""
    rng = random.Random(seed)
    runs = []

    for i in range(n):
        char = rng.choice(CHARACTERS)
        asc = rng.choices(range(21), weights=[3]+[2]*4+[3]*5+[2]*5+[1]*5+[1], k=1)[0]
        ver = rng.choice(VERSIONS)
        # Win rate: ~55% at asc 0-5, ~40% at 10-15, ~25% at 16-20
        win_prob = max(0.15, 0.60 - asc * 0.02 + rng.gauss(0, 0.05))
        win = rng.random() < win_prob
        floor_reached = rng.randint(35, 55) if win else rng.randint(5, 45)
        player_wr = round(rng.uniform(0.2, 0.8), 2)

        char_cards = CARDS_BY_CHAR[char]
        num_card_choices = rng.randint(15, 40)
        card_choices = []
        for _ in range(num_card_choices):
            cid = rng.choice(char_cards) if rng.random() < 0.85 else rng.choice(COLORLESS_CARDS)
            picked = rng.random() < 0.35
            card_choices.append({
                "card_id": cid,
                "upgrade_level": rng.choice([0, 0, 0, 1]) if picked else 0,
                "was_picked": picked,
                "floor": rng.randint(1, min(floor_reached, 200)),
            })

        # Event choices (3-6 per run)
        num_events = rng.randint(3, 6)
        event_choices = []
        chosen_events = rng.sample(list(EVENTS.keys()), min(num_events, len(EVENTS)))
        for eid in chosen_events:
            page = "INITIAL"
            opts = EVENTS[eid][page]
            idx = rng.randrange(len(opts))
            event_choices.append({
                "event_id": eid,
                "option_index": idx,
                "total_options": len(opts),
            })

        # COLORFUL_PHILOSOPHERS (30% of runs)
        if rng.random() < 0.30:
            event_choices.append({
                "event_id": "COLORFUL_PHILOSOPHERS",
                "option_index": -1,
                "total_options": 0,
                "chosen_option_id": rng.choice(PHILOSOPHER_COLORS),
            })

        # Ancient events (40% of runs get 1 ancient event)
        if rng.random() < 0.40:
            anc_eid = rng.choice(list(ANCIENT_RELIC_POOLS.keys()))
            pool = ANCIENT_RELIC_POOLS[anc_eid]
            num_opts = rng.choice([2, 3, 3])
            opts = rng.sample(pool, min(num_opts, len(pool)))
            chosen = rng.choice(opts)
            combo_key = "|".join(sorted(opts))
            event_choices.append({
                "event_id": anc_eid,
                "option_index": -1,
                "total_options": len(opts),
                "combo_key": combo_key,
                "chosen_option_id": chosen,
            })

        # Relics (5-12)
        num_relics = rng.randint(5, 12) if win else rng.randint(3, 8)
        final_relics = rng.sample(RELICS, num_relics)

        # Shop purchases (0-4)
        shop_purchases = []
        for _ in range(rng.randint(0, 4)):
            if rng.random() < 0.5:
                shop_purchases.append({
                    "item_id": rng.choice(char_cards + COLORLESS_CARDS),
                    "item_type": "card", "cost": rng.randint(50, 200),
                    "floor": rng.randint(1, min(floor_reached, 200)),
                })
            else:
                shop_purchases.append({
                    "item_id": rng.choice(RELICS),
                    "item_type": "relic", "cost": rng.randint(150, 350),
                    "floor": rng.randint(1, min(floor_reached, 200)),
                })

        # Card removals (0-3)
        card_removals = []
        for _ in range(rng.randint(0, 3)):
            card_removals.append({
                "card_id": rng.choice(char_cards),
                "source": rng.choice(["shop", "event"]),
                "floor": rng.randint(1, min(floor_reached, 200)),
            })

        # Card upgrades (2-8)
        card_upgrades = []
        for _ in range(rng.randint(2, 8)):
            card_upgrades.append({
                "card_id": rng.choice(char_cards),
                "source": rng.choice(["campfire", "campfire", "campfire", "event", "other"]),
            })

        # Final deck (10-30 cards, some upgraded)
        final_deck = []
        # Always start with starter cards
        strike_id = f"STRIKE_{char[0]}"
        defend_id = f"DEFEND_{char[0]}"
        for sid in [strike_id] * rng.randint(3, 5) + [defend_id] * rng.randint(3, 5):
            final_deck.append({
                "card_id": sid,
                "upgrade_level": 1 if rng.random() < 0.3 else 0,
            })
        # Add picked cards from card_choices + some extras
        picked_cards = [c["card_id"] for c in card_choices if c["was_picked"]]
        for cid in picked_cards:
            final_deck.append({
                "card_id": cid,
                "upgrade_level": 1 if rng.random() < 0.35 else 0,
            })
        # Fill to realistic size
        while len(final_deck) < rng.randint(15, 30):
            final_deck.append({
                "card_id": rng.choice(char_cards),
                "upgrade_level": 1 if rng.random() < 0.25 else 0,
            })

        # Shop card offerings (each shop visit offers 5+2 cards)
        shop_card_offerings = []
        num_shop_visits = rng.randint(1, 3)
        for _ in range(num_shop_visits):
            shop_floor = rng.randint(1, min(floor_reached, 200))
            # 5 character cards + 2 colorless
            for __ in range(5):
                shop_card_offerings.append({
                    "card_id": rng.choice(char_cards),
                    "floor": shop_floor,
                })
            for __ in range(2):
                shop_card_offerings.append({
                    "card_id": rng.choice(COLORLESS_CARDS),
                    "floor": shop_floor,
                })

        # Encounters (8-18)
        encounters = []
        num_normals = rng.randint(6, 12)
        num_elites = rng.randint(1, 3)
        num_bosses = 1 if win else rng.choice([0, 1])
        killed_by = None

        for _ in range(num_normals):
            eid = rng.choice(ENCOUNTERS["normal"])
            died = False
            encounters.append({
                "encounter_id": eid, "encounter_type": "normal",
                "damage_taken": rng.randint(0, 25),
                "turns_taken": rng.randint(2, 8), "player_died": died,
                "floor": rng.randint(1, min(floor_reached, 200)),
            })
        for _ in range(num_elites):
            eid = rng.choice(ENCOUNTERS["elite"])
            encounters.append({
                "encounter_id": eid, "encounter_type": "elite",
                "damage_taken": rng.randint(5, 40),
                "turns_taken": rng.randint(4, 12), "player_died": False,
                "floor": rng.randint(1, min(floor_reached, 200)),
            })
        for _ in range(num_bosses):
            eid = rng.choice(ENCOUNTERS["boss"])
            died = not win and rng.random() < 0.5
            if died:
                killed_by = eid
            encounters.append({
                "encounter_id": eid, "encounter_type": "boss",
                "damage_taken": rng.randint(10, 60),
                "turns_taken": rng.randint(6, 20), "player_died": died,
                "floor": min(floor_reached, 200),
            })
        # If loss and no boss kill, mark a random encounter as death
        if not win and not killed_by and encounters:
            death_enc = rng.choice(encounters)
            death_enc["player_died"] = True

        hash_raw = f"{ver}|{char}|{asc}|seed_{i}|{i}"
        run_hash = hashlib.sha256(hash_raw.encode()).hexdigest()[:32]

        runs.append({
            "mod_version": "test_data",
            "game_version": ver,
            "character": char,
            "ascension": asc,
            "win": win,
            "player_win_rate": player_wr,
            "num_players": 1,
            "floor_reached": floor_reached,
            "run_hash": run_hash,
            "card_choices": card_choices[:500],
            "event_choices": event_choices[:100],
            "final_deck": final_deck[:200],
            "final_relics": final_relics[:50],
            "shop_purchases": shop_purchases[:100],
            "shop_card_offerings": shop_card_offerings[:100],
            "card_removals": card_removals[:100],
            "card_upgrades": card_upgrades[:100],
            "encounters": encounters[:100],
            "contributions": [],
        })

    return runs


def main():
    parser = argparse.ArgumentParser(description="Generate test data for STS2 stats server")
    parser.add_argument("--count", type=int, default=3000, help="Number of runs to generate")
    parser.add_argument("--seed", type=int, default=42, help="Random seed")
    parser.add_argument("--upload", action="store_true", help="Upload via API")
    parser.add_argument("--api-url", default="http://localhost:5080", help="API base URL")
    parser.add_argument("--output", help="Write payloads to JSON file")
    parser.add_argument("--clear", action="store_true", help="Clear existing test_data runs before insert")
    args = parser.parse_args()

    print(f"Generating {args.count} test runs (seed={args.seed})...")
    runs = generate_runs(args.count, args.seed)

    # Stats
    chars = {}
    ascs = {}
    wins = 0
    for r in runs:
        chars[r["character"]] = chars.get(r["character"], 0) + 1
        ascs[r["ascension"]] = ascs.get(r["ascension"], 0) + 1
        if r["win"]:
            wins += 1

    print(f"\nGenerated {len(runs)} runs:")
    print(f"  Win rate: {wins}/{len(runs)} ({wins/len(runs)*100:.1f}%)")
    print(f"  Characters: {dict(sorted(chars.items()))}")
    print(f"  Ascension range: {min(ascs)}–{max(ascs)}")
    total_cards = sum(len(r["card_choices"]) for r in runs)
    total_events = sum(len(r["event_choices"]) for r in runs)
    total_encounters = sum(len(r["encounters"]) for r in runs)
    print(f"  Total card_choices: {total_cards}")
    print(f"  Total event_choices: {total_events}")
    print(f"  Total encounters: {total_encounters}")

    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(runs, f, ensure_ascii=False)
        print(f"\nSaved to {args.output}")

    if args.upload:
        import httpx
        print(f"\nUploading to {args.api_url}/v1/runs ...")

        if args.clear:
            print("  Clearing existing test_data runs...")
            # Can't do this via API, skip

        ok = 0
        err = 0
        dedup = 0
        with httpx.Client(timeout=30) as client:
            for i, payload in enumerate(runs):
                try:
                    resp = client.post(f"{args.api_url}/v1/runs", json=payload)
                    body = resp.json()
                    if resp.status_code == 200:
                        if body.get("dedup"):
                            dedup += 1
                        else:
                            ok += 1
                    else:
                        err += 1
                        if err <= 3:
                            print(f"  [{resp.status_code}] {resp.text[:200]}")
                except Exception as e:
                    err += 1
                    if err <= 3:
                        print(f"  Error: {e}")
                if (i + 1) % 500 == 0:
                    print(f"  {i+1}/{len(runs)} (ok={ok}, dedup={dedup}, err={err})")

        print(f"\nDone: {ok} inserted, {dedup} deduped, {err} errors")

    return 0


if __name__ == "__main__":
    sys.exit(main())
