#!/usr/bin/env python3
"""Bulk-insert generated test data directly into PostgreSQL, bypassing API rate limits."""

import asyncio
import json
import sys
import asyncpg


DB_URL = "postgresql://sts2stats:your_strong_password_here@localhost:5432/sts2stats"


async def insert_all(runs: list[dict]):
    conn = await asyncpg.connect(DB_URL)
    print(f"Connected. Inserting {len(runs)} runs...")

    inserted = 0
    try:
        for i, r in enumerate(runs):
            async with conn.transaction():
                # Check dedup
                existing = await conn.fetchval(
                    "SELECT id FROM runs WHERE run_hash = $1", r.get("run_hash"))
                if existing is not None:
                    continue

                run_id = await conn.fetchval(
                    """INSERT INTO runs
                       (game_version, mod_version, character, ascension, win,
                        num_players, floor_reached, run_hash)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8) RETURNING id""",
                    r["game_version"], r["mod_version"], r["character"],
                    r["ascension"], r["win"], r["num_players"],
                    r["floor_reached"], r["run_hash"],
                )

                if r["card_choices"]:
                    await conn.executemany(
                        """INSERT INTO card_choices
                           (run_id, game_version, character, ascension, player_win_rate,
                            win, num_players, card_id, upgrade_level, was_picked, floor)
                           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["player_win_rate"], r["win"], r["num_players"],
                          c["card_id"], c["upgrade_level"], c["was_picked"], c["floor"])
                         for c in r["card_choices"]],
                    )

                if r["event_choices"]:
                    await conn.executemany(
                        """INSERT INTO event_choices
                           (run_id, game_version, character, ascension, player_win_rate,
                            win, event_id, option_index, total_options,
                            combo_key, chosen_option_id)
                           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["player_win_rate"], r["win"],
                          e["event_id"], e["option_index"], e["total_options"],
                          e.get("combo_key"), e.get("chosen_option_id"))
                         for e in r["event_choices"]],
                    )

                if r["final_relics"]:
                    await conn.executemany(
                        """INSERT INTO relic_records
                           (run_id, game_version, character, ascension, player_win_rate,
                            win, relic_id)
                           VALUES ($1,$2,$3,$4,$5,$6,$7)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["player_win_rate"], r["win"], rid)
                         for rid in r["final_relics"]],
                    )

                if r["shop_purchases"]:
                    await conn.executemany(
                        """INSERT INTO shop_purchases
                           (run_id, game_version, character, ascension, player_win_rate,
                            win, item_id, item_type, cost, floor)
                           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["player_win_rate"], r["win"],
                          s["item_id"], s["item_type"], s["cost"], s["floor"])
                         for s in r["shop_purchases"]],
                    )

                if r["card_removals"]:
                    await conn.executemany(
                        """INSERT INTO card_removals
                           (run_id, game_version, character, ascension, win,
                            card_id, source, floor)
                           VALUES ($1,$2,$3,$4,$5,$6,$7,$8)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["win"], cr["card_id"], cr["source"], cr["floor"])
                         for cr in r["card_removals"]],
                    )

                if r["card_upgrades"]:
                    await conn.executemany(
                        """INSERT INTO card_upgrades
                           (run_id, game_version, character, ascension, win,
                            card_id, source)
                           VALUES ($1,$2,$3,$4,$5,$6,$7)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["win"], cu["card_id"], cu["source"])
                         for cu in r["card_upgrades"]],
                    )

                if r.get("final_deck"):
                    await conn.executemany(
                        """INSERT INTO final_deck
                           (run_id, game_version, character, ascension, win,
                            card_id, upgrade_level)
                           VALUES ($1,$2,$3,$4,$5,$6,$7)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["win"], d["card_id"], d["upgrade_level"])
                         for d in r["final_deck"]],
                    )

                if r.get("shop_card_offerings"):
                    await conn.executemany(
                        """INSERT INTO shop_card_offerings
                           (run_id, game_version, character, ascension, win,
                            card_id, floor)
                           VALUES ($1,$2,$3,$4,$5,$6,$7)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          r["win"], o["card_id"], o["floor"])
                         for o in r["shop_card_offerings"]],
                    )

                if r["encounters"]:
                    await conn.executemany(
                        """INSERT INTO encounter_records
                           (run_id, game_version, character, ascension,
                            encounter_id, encounter_type, damage_taken,
                            turns_taken, player_died, floor)
                           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)""",
                        [(run_id, r["game_version"], r["character"], r["ascension"],
                          e["encounter_id"], e["encounter_type"], e["damage_taken"],
                          e["turns_taken"], e["player_died"], e["floor"])
                         for e in r["encounters"]],
                    )

                # Register game version
                await conn.execute(
                    """INSERT INTO game_versions (version)
                       VALUES ($1)
                       ON CONFLICT (version) DO UPDATE SET last_seen = NOW()""",
                    r["game_version"],
                )

                inserted += 1

            if (i + 1) % 500 == 0:
                print(f"  {i+1}/{len(runs)} inserted={inserted}")

    finally:
        await conn.close()

    print(f"\nDone: {inserted} runs inserted out of {len(runs)}")


def main():
    data_path = sys.argv[1] if len(sys.argv) > 1 else "/tmp/test_runs.json"
    with open(data_path, "r", encoding="utf-8") as f:
        runs = json.load(f)
    print(f"Loaded {len(runs)} runs from {data_path}")
    asyncio.run(insert_all(runs))


if __name__ == "__main__":
    main()
