#!/usr/bin/env python3
"""
Convert STS2 local run history (.run files) to API RunUploadPayload format
and optionally upload to the server.

Usage:
  python convert_run_history.py                          # Auto-detect history dir
  python convert_run_history.py --history-dir /path/to   # Custom path
  python convert_run_history.py --upload --api-url http://localhost:5080
  python convert_run_history.py --dry-run                # Parse only, no upload
"""

import argparse
import glob
import hashlib
import json
import os
import sys
from pathlib import Path

# ── ID prefix strippers ─────────────────────────────────────
# .run files use "CARD.BASH", "RELIC.BURNING_BLOOD", "ENCOUNTER.JAW_WORM" etc.
# API expects raw IDs: "BASH", "BURNING_BLOOD", "JAW_WORM"

PREFIXES = {
    "CARD.": "",
    "RELIC.": "",
    "POTION.": "",
    "ENCOUNTER.": "",
    "MONSTER.": "",
    "CHARACTER.": "",
    "ACT.": "",
    "EVENT.": "",
    "NONE.NONE": "",
}


def strip_prefix(raw_id: str) -> str:
    """Remove game-internal prefixes like 'CARD.', 'RELIC.' etc."""
    if not raw_id:
        return ""
    for prefix in PREFIXES:
        if raw_id.startswith(prefix):
            return raw_id[len(prefix):]
    return raw_id


def extract_event_id_from_key(loc_key: str) -> str:
    """
    Extract event ID from localization key.
    e.g. 'BYRDONIS_NEST.pages.INITIAL.options.TAKE.title' → 'BYRDONIS_NEST'
    e.g. 'PAELS_CLAW.title' → '' (this is a relic, not an event)
    """
    if not loc_key:
        return ""
    # Event keys have pattern: EVENT_ID.pages.PAGE.options.OPTION.title
    if ".pages." in loc_key and ".options." in loc_key:
        return loc_key.split(".pages.")[0]
    return ""


def extract_option_id(loc_key: str) -> str:
    """
    Extract option name from localization key.
    e.g. 'BYRDONIS_NEST.pages.INITIAL.options.TAKE.title' → 'TAKE'
    """
    if not loc_key or ".options." not in loc_key:
        return ""
    parts = loc_key.split(".options.")
    if len(parts) >= 2:
        return parts[1].split(".")[0]
    return ""


def extract_page_name(loc_key: str) -> str:
    """
    Extract page name from localization key.
    e.g. 'TRIAL.pages.MERCHANT.options.GUILTY.title' → 'MERCHANT'
    """
    if not loc_key or ".pages." not in loc_key:
        return ""
    after_pages = loc_key.split(".pages.")[1]
    return after_pages.split(".")[0]


# ── Event option index mapping ─────────────────────────────────
# Built from decompiled GenerateInitialOptions() for each event.
# Maps: event_id → { page_name → [option_name_0, option_name_1, ...] }
# The list position IS the option_index.

EVENT_OPTION_MAP: dict[str, dict[str, list[str]]] = {
    "ABYSSAL_BATHS":          {"INITIAL": ["IMMERSE", "ABSTAIN"], "ALL": ["LINGER", "EXIT_BATHS"]},
    "AROMA_OF_CHAOS":         {"INITIAL": ["LET_GO", "MAINTAIN_CONTROL"]},
    "BATTLEWORN_DUMMY":       {"INITIAL": ["SETTING_1", "SETTING_2", "SETTING_3"]},
    "BRAIN_LEECH":            {"INITIAL": ["SHARE_KNOWLEDGE", "RIP"]},
    "BUGSLAYER":              {"INITIAL": ["EXTERMINATION", "SQUASH"]},
    "BYRDONIS_NEST":          {"INITIAL": ["EAT", "TAKE"]},
    "COLOSSAL_FLOWER":        {"INITIAL": ["EXTRACT_CURRENT_PRIZE_1", "REACH_DEEPER_1"]},
    "CRYSTAL_SPHERE":         {"INITIAL": ["UNCOVER_FUTURE", "PAYMENT_PLAN"]},
    "DENSE_VEGETATION":       {"INITIAL": ["TRUDGE_ON", "REST"], "REST": ["FIGHT"]},
    "DOLL_ROOM":              {"INITIAL": ["RANDOM", "TAKE_SOME_TIME", "EXAMINE"]},
    "DOORS_OF_LIGHT_AND_DARK":{"INITIAL": ["LIGHT", "DARK"]},
    "DROWNING_BEACON":        {"INITIAL": ["BOTTLE", "CLIMB"]},
    "FIELD_OF_MAN_SIZED_HOLES":{"INITIAL": ["RESIST", "ENTER_YOUR_HOLE"]},
    "GRAVE_OF_THE_FORGOTTEN": {"INITIAL": ["CONFRONT", "ACCEPT"]},
    "HUNGRY_FOR_MUSHROOMS":   {"INITIAL": ["BIG_MUSHROOM", "FRAGRANT_MUSHROOM"]},
    "INFESTED_AUTOMATON":     {"INITIAL": ["STUDY", "TOUCH_CORE"]},
    "JUNGLE_MAZE_ADVENTURE":  {"INITIAL": ["SOLO_QUEST", "JOIN_FORCES"]},
    "LOST_WISP":              {"INITIAL": ["CLAIM", "SEARCH"]},
    "LUMINOUS_CHOIR":         {"INITIAL": ["REACH_INTO_THE_FLESH", "OFFER_TRIBUTE"]},
    "MORPHIC_GROVE":          {"INITIAL": ["GROUP", "LONER"]},
    "POTION_COURIER":         {"INITIAL": ["GRAB_POTIONS", "RANSACK"]},
    "PUNCH_OFF":              {"INITIAL": ["NAB", "I_CAN_TAKE_THEM"], "I_CAN_TAKE_THEM": ["FIGHT"]},
    "RANWID_THE_ELDER":       {"INITIAL": ["POTION", "GOLD", "RELIC"]},
    "REFLECTIONS":            {"INITIAL": ["TOUCH_A_MIRROR", "SHATTER"]},
    "RELIC_TRADER":           {"INITIAL": ["TOP", "MIDDLE", "BOTTOM"]},
    "ROOM_FULL_OF_CHEESE":    {"INITIAL": ["GORGE", "SEARCH"]},
    "ROUND_TEA_PARTY":        {"INITIAL": ["ENJOY_TEA", "PICK_FIGHT"]},
    "SAPPHIRE_SEED":          {"INITIAL": ["EAT", "PLANT"]},
    "SELF_HELP_BOOK":         {"INITIAL": ["READ_THE_BACK", "READ_PASSAGE", "READ_ENTIRE_BOOK"]},
    "SPIRALING_WHIRLPOOL":    {"INITIAL": ["OBSERVE", "DRINK"]},
    "SPIRIT_GRAFTER":         {"INITIAL": ["LET_IT_IN", "REJECTION"]},
    "STONE_OF_ALL_TIME":      {"INITIAL": ["LIFT", "PUSH"]},
    "SUNKEN_STATUE":          {"INITIAL": ["GRAB_SWORD", "DIVE_INTO_WATER"]},
    "SUNKEN_TREASURY":        {"INITIAL": ["FIRST_CHEST", "SECOND_CHEST"]},
    "SYMBIOTE":               {"INITIAL": ["APPROACH", "KILL_WITH_FIRE"]},
    "TEA_MASTER":             {"INITIAL": ["BONE_TEA", "EMBER_TEA", "TEA_OF_DISCOURTESY"]},
    "THE_LANTERN_KEY":        {"INITIAL": ["RETURN_THE_KEY", "KEEP_THE_KEY"], "KEEP_THE_KEY": ["FIGHT"]},
    "THE_LEGENDS_WERE_TRUE":  {"INITIAL": ["NAB_THE_MAP", "SLOWLY_FIND_AN_EXIT"]},
    "THIS_OR_THAT":           {"INITIAL": ["PLAIN", "ORNATE"]},
    "TINKER_TIME":            {"INITIAL": ["CHOOSE_CARD_TYPE"], "CHOOSE_CARD_TYPE": ["ATTACK", "SKILL", "POWER"]},
    "TRASH_HEAP":             {"INITIAL": ["DIVE_IN", "GRAB"]},
    "TRIAL":                  {"INITIAL": ["ACCEPT", "REJECT"],
                               "MERCHANT": ["GUILTY", "INNOCENT"],
                               "NOBLE": ["GUILTY", "INNOCENT"],
                               "NONDESCRIPT": ["GUILTY", "INNOCENT"],
                               "REJECT": ["ACCEPT", "DOUBLE_DOWN"]},
    "UNREST_SITE":            {"INITIAL": ["REST", "KILL"]},
    "WAR_HISTORIAN_REPY":     {"INITIAL": ["UNLOCK_CAGE", "UNLOCK_CHEST"]},
    "WATERLOGGED_SCRIPTORIUM":{"INITIAL": ["BLOODY_INK", "TENTACLE_QUILL", "PRICKLY_SPONGE"]},
    "WELCOME_TO_WONGOS":      {"INITIAL": ["BARGAIN_BIN", "FEATURED_ITEM", "MYSTERY_BOX", "LEAVE"]},
    "WELLSPRING":             {"INITIAL": ["BOTTLE", "BATHE"]},
    "WHISPERING_HOLLOW":      {"INITIAL": ["GOLD", "HUG"]},
    "WOOD_CARVINGS":          {"INITIAL": ["SNAKE", "BIRD", "TORUS"]},
    "ZEN_WEAVER":             {"INITIAL": ["BREATHING_TECHNIQUES", "EMOTIONAL_AWARENESS", "ARACHNID_ACUPUNCTURE"]},
}


def resolve_event_option(event_id: str, page: str, option_name: str) -> tuple[int, int] | None:
    """
    Resolve (option_index, total_options) from the event option mapping.
    Returns None if the event/option can't be resolved (dynamic events, etc.).
    """
    # ── Pattern matching for events with numbered/dynamic pages ──
    if event_id == "SLIPPERY_BRIDGE":
        if option_name == "OVERCOME":
            return (0, 2)
        if option_name.startswith("HOLD_ON"):
            return (1, 2)
        return None
    if event_id == "TABLET_OF_TRUTH":
        if option_name.startswith("DECIPHER"):
            return (0, 2)
        if option_name in ("SMASH", "GIVE_UP"):
            return (1, 2)
        return None
    if event_id == "COLOSSAL_FLOWER":
        if option_name.startswith("EXTRACT") or option_name == "POLLINOUS_CORE":
            return (0, 2) if option_name.startswith("EXTRACT") else (1, 2)
        if option_name.startswith("REACH_DEEPER"):
            return (1, 2)
        return None
    if event_id == "ENDLESS_CONVEYOR":
        if option_name in ("OBSERVE_CHEF", "LEAVE"):
            return (1, 2)
        return (0, 2)  # any dish is always index 0

    # ── Static map lookup ──
    event_pages = EVENT_OPTION_MAP.get(event_id)
    if event_pages is None:
        return None
    options = event_pages.get(page)
    if options is not None and option_name in options:
        return (options.index(option_name), len(options))

    return None


# ── Room type to encounter_type mapping ──────────────────────

SKIPPED_EVENTS = {"THE_FUTURE_OF_POTIONS"}

ROOM_TYPE_MAP = {
    "monster": "normal",
    "monster_weak": "normal",
    "elite": "elite",
    "boss": "boss",
}


def _process_ancient_choices(choices: list[dict], event_choices: list[dict]) -> None:
    """
    Process ancient event choices (方案B: combo-based stats).
    Each entry has: title.key (loc key), was_chosen (bool).
    All presented options are recorded, not just the chosen one.
    """
    event_id = None
    option_ids = []
    chosen_option_id = None

    for choice in choices:
        title_info = choice.get("title", {})
        loc_key = title_info.get("key", "")
        if not loc_key:
            continue

        if event_id is None:
            event_id = extract_event_id_from_key(loc_key)

        option_name = extract_option_id(loc_key)
        if not option_name:
            continue

        option_ids.append(option_name)
        if choice.get("was_chosen", False):
            chosen_option_id = option_name

    if not event_id or not chosen_option_id or len(option_ids) < 2:
        return

    # Sort alphabetically and join with | to form combo_key
    sorted_ids = sorted(option_ids)
    combo_key = "|".join(sorted_ids)

    event_choices.append({
        "event_id": event_id,
        "option_index": -1,
        "total_options": len(option_ids),
        "combo_key": combo_key,
        "chosen_option_id": chosen_option_id,
    })


def convert_run(data: dict) -> dict | None:
    """Convert a single .run JSON to RunUploadPayload format."""
    # Skip non-standard game modes (daily, custom)
    if data.get("game_mode") != "standard":
        return None

    # Skip abandoned runs (player quit, not a real loss)
    if data.get("was_abandoned"):
        return None

    # Skip multiplayer runs (num_players > 1)
    players = data.get("players", [])
    if len(players) != 1:
        return None

    player = players[0]
    character = strip_prefix(player.get("character", ""))
    if not character:
        return None

    win = data.get("win", False)
    ascension = data.get("ascension", 0)
    game_version = data.get("build_id", "unknown")

    # ── Walk map_point_history to extract all data ──────────
    card_choices = []
    event_choices = []
    shop_purchases = []
    card_removals = []
    card_upgrades = []
    encounters = []
    floor_counter = 0
    max_floor = 0

    map_point_history = data.get("map_point_history", [])
    for act in map_point_history:
        for node in act:
            floor_counter += 1
            if floor_counter > max_floor:
                max_floor = floor_counter

            rooms = node.get("rooms", [])
            ps_list = node.get("player_stats", [])
            ps = ps_list[0] if ps_list else {}

            # ── Card choices ────────────────────────────
            for cc in ps.get("card_choices", []):
                card_info = cc.get("card", {})
                card_id = strip_prefix(card_info.get("id", ""))
                if not card_id:
                    continue
                upgrade = card_info.get("current_upgrade_level", 0)
                picked = cc.get("was_picked", False)
                floor = card_info.get("floor_added_to_deck", floor_counter)
                card_choices.append({
                    "card_id": card_id,
                    "upgrade_level": upgrade,
                    "was_picked": picked,
                    "floor": min(floor, 200),
                })

            # ── Event choices ───────────────────────────
            for ec in ps.get("event_choices", []):
                title_info = ec.get("title", {})
                loc_key = title_info.get("key", "")
                event_id = extract_event_id_from_key(loc_key)
                option_name = extract_option_id(loc_key)
                page_name = extract_page_name(loc_key)
                if not event_id or not option_name or not page_name:
                    continue

                # COLORFUL_PHILOSOPHERS: flat option tracking (方案A)
                if event_id == "COLORFUL_PHILOSOPHERS":
                    if option_name:
                        event_choices.append({
                            "event_id": event_id,
                            "option_index": -1,
                            "total_options": 0,
                            "chosen_option_id": option_name,
                        })
                    continue

                # Skip events with no meaningful option distinction
                if event_id in SKIPPED_EVENTS:
                    continue

                resolved = resolve_event_option(event_id, page_name, option_name)
                if resolved is not None:
                    opt_idx, total = resolved
                    event_choices.append({
                        "event_id": event_id,
                        "option_index": opt_idx,
                        "total_options": total,
                    })

            # ── Ancient event choices (combo-based, 方案B) ──
            ancient_choices = ps.get("ancient_choice", [])
            if len(ancient_choices) >= 2:
                _process_ancient_choices(ancient_choices, event_choices)

            # ── Card upgrades ───────────────────────────
            for upgraded in ps.get("upgraded_cards", []):
                card_id = strip_prefix(upgraded) if isinstance(upgraded, str) else strip_prefix(upgraded.get("id", ""))
                if card_id:
                    # Determine source: rest site vs event vs other
                    mpt = node.get("map_point_type", "")
                    source = "campfire" if mpt in ("rest", "rest_site") else "event" if mpt in ("unknown", "event") else "other"
                    card_upgrades.append({
                        "card_id": card_id,
                        "source": source,
                    })

            # ── Card removals ───────────────────────────
            for removed in ps.get("cards_removed", []):
                card_id = strip_prefix(removed.get("id", "") if isinstance(removed, dict) else removed)
                if card_id:
                    mpt = node.get("map_point_type", "")
                    source = "shop" if mpt == "shop" else "event"
                    card_removals.append({
                        "card_id": card_id,
                        "source": source,
                        "floor": floor_counter,
                    })

            # ── Shop purchases ──────────────────────────
            for relic_id in ps.get("bought_relics", []):
                rid = strip_prefix(relic_id)
                if rid:
                    shop_purchases.append({
                        "item_id": rid,
                        "item_type": "relic",
                        "cost": 0,  # .run files don't store prices
                        "floor": floor_counter,
                    })
            for potion_id in ps.get("bought_potions", []):
                pid = strip_prefix(potion_id)
                if pid:
                    shop_purchases.append({
                        "item_id": pid,
                        "item_type": "potion",
                        "cost": 0,
                        "floor": floor_counter,
                    })
            for card_id in ps.get("bought_colorless", []):
                cid = strip_prefix(card_id)
                if cid:
                    shop_purchases.append({
                        "item_id": cid,
                        "item_type": "card",
                        "cost": 0,
                        "floor": floor_counter,
                    })

            # ── Encounters ──────────────────────────────
            for room in rooms:
                room_type = room.get("room_type", "")
                enc_type = ROOM_TYPE_MAP.get(room_type)
                if enc_type is None:
                    continue  # skip non-combat rooms (event, shop, rest, etc.)
                enc_id = strip_prefix(room.get("model_id", ""))
                if not enc_id:
                    continue
                damage_taken = ps.get("damage_taken", 0)
                turns = room.get("turns_taken", 0)
                # Determine if player died in this encounter
                killed_by = strip_prefix(data.get("killed_by_encounter", ""))
                player_died = (not win) and (killed_by == enc_id) and (floor_counter == max_floor)
                encounters.append({
                    "encounter_id": enc_id,
                    "encounter_type": enc_type,
                    "damage_taken": min(damage_taken, 999999),
                    "turns_taken": min(turns, 999),
                    "player_died": player_died,
                    "floor": floor_counter,
                })

    # ── Final deck ──────────────────────────────────────
    final_deck = []
    for card in player.get("deck", []):
        card_id = strip_prefix(card.get("id", ""))
        if card_id:
            final_deck.append({
                "card_id": card_id,
                "upgrade_level": card.get("current_upgrade_level", 0),
            })

    # ── Final relics ────────────────────────────────────
    final_relics = []
    for relic in player.get("relics", []):
        relic_id = strip_prefix(relic.get("id", ""))
        if relic_id:
            final_relics.append(relic_id)

    # Compute dedup hash matching C# HistoryImporter.ComputeHash
    seed = data.get("seed", "")
    start_time = data.get("start_time", 0)
    hash_raw = f"{game_version}|{character}|{ascension}|{seed}|{start_time}"
    run_hash = hashlib.sha256(hash_raw.encode("utf-8")).hexdigest()[:32]

    return {
        "mod_version": "history_import",
        "game_version": game_version,
        "character": character,
        "ascension": ascension,
        "win": win,
        "player_win_rate": 0.0,  # Unknown for historical runs
        "num_players": 1,
        "floor_reached": max_floor,
        "run_hash": run_hash,
        "card_choices": card_choices[:500],
        "event_choices": event_choices[:100],
        "final_deck": final_deck[:200],
        "final_relics": final_relics[:50],
        "shop_purchases": shop_purchases[:100],
        "shop_card_offerings": [],  # No shop offering data in history files (mod-only)
        "card_removals": card_removals[:100],
        "card_upgrades": card_upgrades[:100],
        "encounters": encounters[:100],
        "contributions": [],  # No combat contribution data in history files
    }


# ── History directory auto-detection ─────────────────────────

def find_history_dirs() -> list[str]:
    """Auto-detect STS2 run history directories on Windows."""
    dirs = []
    appdata = os.environ.get("APPDATA", "")
    if not appdata:
        return dirs
    sts2_dir = os.path.join(appdata, "SlayTheSpire2")
    if not os.path.isdir(sts2_dir):
        return dirs
    # Search all platforms / profiles / modded variants
    for run_file in glob.glob(os.path.join(sts2_dir, "**", "history", "*.run"), recursive=True):
        hist_dir = os.path.dirname(run_file)
        if hist_dir not in dirs:
            dirs.append(hist_dir)
    return dirs


def load_run_files(history_dirs: list[str]) -> list[tuple[str, dict]]:
    """Load all .run files from given directories. Returns (path, data) pairs."""
    results = []
    for d in history_dirs:
        for f in sorted(glob.glob(os.path.join(d, "*.run"))):
            try:
                with open(f, "r", encoding="utf-8") as fh:
                    data = json.load(fh)
                results.append((f, data))
            except (json.JSONDecodeError, UnicodeDecodeError) as e:
                print(f"  [WARN] Skipping corrupt file: {f} ({e})")
    return results


def main():
    parser = argparse.ArgumentParser(description="Convert STS2 run history to API format")
    parser.add_argument("--history-dir", action="append", help="Path to history directory (can repeat)")
    parser.add_argument("--upload", action="store_true", help="Upload converted runs to API")
    parser.add_argument("--api-url", default="http://localhost:5080", help="API base URL")
    parser.add_argument("--dry-run", action="store_true", help="Parse and show stats, don't upload")
    parser.add_argument("--output", help="Write converted payloads to JSON file")
    args = parser.parse_args()

    # Find history dirs
    if args.history_dir:
        history_dirs = args.history_dir
    else:
        history_dirs = find_history_dirs()
        if not history_dirs:
            print("ERROR: No STS2 run history found. Use --history-dir to specify path.")
            return 1
        print(f"Auto-detected {len(history_dirs)} history dir(s):")
        for d in history_dirs:
            print(f"  {d}")

    # Load all .run files
    print("\nLoading .run files...")
    run_files = load_run_files(history_dirs)
    print(f"Found {len(run_files)} run files")

    # Convert
    converted = []
    skipped = {"abandoned": 0, "multiplayer": 0, "non_standard": 0, "parse_error": 0}
    char_counts = {}
    asc_counts = {}
    win_count = 0

    for path, data in run_files:
        try:
            payload = convert_run(data)
        except Exception as e:
            print(f"  [ERROR] {path}: {e}")
            skipped["parse_error"] += 1
            continue

        if payload is None:
            if data.get("was_abandoned"):
                skipped["abandoned"] += 1
            elif len(data.get("players", [])) != 1:
                skipped["multiplayer"] += 1
            else:
                skipped["non_standard"] += 1
            continue

        converted.append(payload)
        char = payload["character"]
        char_counts[char] = char_counts.get(char, 0) + 1
        asc = payload["ascension"]
        asc_counts[asc] = asc_counts.get(asc, 0) + 1
        if payload["win"]:
            win_count += 1

    # Report
    print(f"\n{'='*50}")
    print(f"Converted: {len(converted)} runs")
    print(f"Skipped:   {sum(skipped.values())} "
          f"(abandoned={skipped['abandoned']}, mp={skipped['multiplayer']}, "
          f"non_std={skipped['non_standard']}, error={skipped['parse_error']})")
    print(f"\nCharacter distribution:")
    for char, count in sorted(char_counts.items()):
        print(f"  {char:20s} {count:4d}")
    print(f"\nAscension distribution:")
    for asc in sorted(asc_counts.keys()):
        print(f"  Asc {asc:2d}: {asc_counts[asc]:4d}")
    print(f"\nWin rate: {win_count}/{len(converted)} ({win_count/len(converted)*100:.1f}%)" if converted else "")

    # Sample output
    if converted:
        sample = converted[0]
        print(f"\nSample run ({sample['character']} asc{sample['ascension']} {'W' if sample['win'] else 'L'}):")
        print(f"  card_choices:   {len(sample['card_choices'])}")
        print(f"  event_choices:  {len(sample['event_choices'])}")
        print(f"  final_deck:     {len(sample['final_deck'])}")
        print(f"  final_relics:   {len(sample['final_relics'])}")
        print(f"  shop_purchases: {len(sample['shop_purchases'])}")
        print(f"  card_removals:  {len(sample['card_removals'])}")
        print(f"  card_upgrades:  {len(sample['card_upgrades'])}")
        print(f"  encounters:     {len(sample['encounters'])}")
        print(f"  floor_reached:  {sample['floor_reached']}")

    # Write to file
    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(converted, f, indent=2, ensure_ascii=False)
        print(f"\nSaved {len(converted)} payloads to {args.output}")

    # Upload
    if args.upload and not args.dry_run:
        if not converted:
            print("Nothing to upload.")
            return 0

        import httpx
        print(f"\nUploading {len(converted)} runs to {args.api_url}/v1/runs ...")
        success = 0
        errors = 0
        with httpx.Client(timeout=30) as client:
            for i, payload in enumerate(converted):
                try:
                    resp = client.post(f"{args.api_url}/v1/runs", json=payload)
                    if resp.status_code == 200:
                        success += 1
                    else:
                        errors += 1
                        if errors <= 5:
                            print(f"  Error {resp.status_code}: {resp.text[:300]}")
                except Exception as e:
                    errors += 1
                    if errors <= 5:
                        print(f"  Error: {e}")
                if (i + 1) % 10 == 0:
                    print(f"  {i+1}/{len(converted)} (ok={success}, err={errors})")

        print(f"\nDone: {success} uploaded, {errors} failed")
        return 0 if errors == 0 else 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
