#!/usr/bin/env python3
"""
Compute Tournament Meta Signals

Reuses the MTGTop8 scraping pipeline from ``scrape_decklists.py`` to aggregate
card-inclusion statistics per format, then upserts those "meta signals" into
MongoDB so the .NET deck-generation service can use them as soft hints during
prompting.

For every (format, card) pair we compute:
  - sample_size       — number of tournament decks observed for the format
  - inclusion_count   — how many of those decks contain the card
  - inclusion_rate    — inclusion_count / sample_size                (0.0..1.0)
  - top8_count        — how many appearances were in a top-8 finish (placement <= 8)
  - top8_rate         — top8_count / inclusion_count                 (0.0..1.0)
  - avg_placement     — average tournament placement across appearances
  - archetypes        — distinct archetype labels the card appears in (top 5)
  - updated_at        — UTC timestamp of this aggregation run

Documents are upserted with a compound key {format, card_name_lc} so re-running
the script refreshes the data.  A per-format summary document is also written
to ``meta_signal_stats`` with collection metadata (sample_size, updated_at).

Usage:
    python compute_meta_signals.py
    python compute_meta_signals.py --formats modern pioneer --limit 200
    python compute_meta_signals.py --formats commander --min-inclusions 3
    python compute_meta_signals.py --dry-run --out ./data/meta.json
"""

import argparse
import json
import os
import sys
import time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

# Reuse the scrapers already validated in scrape_decklists.py
sys.path.insert(0, str(Path(__file__).parent))
from scrape_decklists import (  # noqa: E402
    FORMAT_CODES,
    fetch_decklist,
    fetch_decks_from_event,
    fetch_event_ids,
)


# ─── Aggregation ─────────────────────────────────────────────────────────────

def scrape_deck_entries(
    format_name: str, limit: int, delay: float,
) -> list[tuple[dict, list[tuple[int, str, bool]]]]:
    """
    Collect up to ``limit`` raw decklists for a format.
    Returns a list of (deck_meta, cards) where cards is [(qty, name, is_sideboard), ...].
    Sideboard cards are INCLUDED here — the caller decides whether to count them.
    """
    code = FORMAT_CODES[format_name]
    events = fetch_event_ids(code, limit, delay)
    if not events:
        print(f"  No events found for {format_name}")
        return []

    print(f"  Found {len(events)} events; collecting deck references…")

    deck_metas: list[dict] = []
    for event_id, _event_name in events:
        if len(deck_metas) >= limit:
            break
        time.sleep(delay)
        for dm in fetch_decks_from_event(event_id, code, delay):
            deck_metas.append(dm)
            if len(deck_metas) >= limit:
                break

    deck_metas = deck_metas[:limit]
    print(f"  Collected {len(deck_metas)} deck references; downloading lists…")

    out: list[tuple[dict, list[tuple[int, str, bool]]]] = []
    for dm in deck_metas:
        cards = fetch_decklist(dm["deck_id"], delay)
        if cards:
            out.append((dm, cards))

    print(f"  Downloaded {len(out)} decklists")
    return out


def aggregate_signals(
    decks: list[tuple[dict, list[tuple[int, str, bool]]]],
    format_name: str,
    include_sideboard: bool,
    min_inclusions: int,
) -> tuple[list[dict], dict]:
    """
    Aggregate per-card inclusion stats across the scraped decks.
    Returns (signal_docs, stats_doc).
    """
    sample_size = len(decks)
    if sample_size == 0:
        return [], _empty_stats(format_name)

    inclusion_count: dict[str, int] = defaultdict(int)
    top8_count:      dict[str, int] = defaultdict(int)
    placements:      dict[str, list[int]] = defaultdict(list)
    archetypes:      dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))

    for deck_meta, cards in decks:
        placement = int(deck_meta.get("placement", 99) or 99)
        archetype = (deck_meta.get("archetype") or "").strip() or "Unknown"

        seen_this_deck: set[str] = set()
        for _qty, name, is_sideboard in cards:
            if is_sideboard and not include_sideboard:
                continue
            key = name.strip()
            if not key or key in seen_this_deck:
                continue
            seen_this_deck.add(key)

            inclusion_count[key] += 1
            placements[key].append(placement)
            if placement <= 8:
                top8_count[key] += 1
            archetypes[key][archetype] += 1

    now = datetime.now(timezone.utc).isoformat()
    docs: list[dict] = []
    for name, incl in inclusion_count.items():
        if incl < min_inclusions:
            continue

        arch_counter = archetypes[name]
        top_archetypes = [
            a for a, _ in sorted(arch_counter.items(), key=lambda x: (-x[1], x[0]))[:5]
        ]
        pls = placements[name]
        avg_placement = round(sum(pls) / len(pls), 2) if pls else None
        t8 = top8_count[name]

        docs.append({
            "format":          format_name,
            "card_name":       name,
            "card_name_lc":    name.lower(),
            "sample_size":     sample_size,
            "inclusion_count": incl,
            "inclusion_rate":  round(incl / sample_size, 4),
            "top8_count":      t8,
            "top8_rate":       round(t8 / incl, 4) if incl else 0.0,
            "avg_placement":   avg_placement,
            "archetypes":      top_archetypes,
            "updated_at":      now,
        })

    # Sort by inclusion rate, descending, for deterministic output
    docs.sort(key=lambda d: (-d["inclusion_rate"], d["card_name"]))

    stats_doc = {
        "format":          format_name,
        "sample_size":     sample_size,
        "unique_cards":    len(docs),
        "min_inclusions":  min_inclusions,
        "include_sideboard": include_sideboard,
        "updated_at":      now,
    }
    return docs, stats_doc


def _empty_stats(format_name: str) -> dict:
    return {
        "format":          format_name,
        "sample_size":     0,
        "unique_cards":    0,
        "min_inclusions":  0,
        "include_sideboard": False,
        "updated_at":      datetime.now(timezone.utc).isoformat(),
    }


# ─── MongoDB Persistence ─────────────────────────────────────────────────────

def upsert_to_mongo(
    docs: list[dict],
    stats: dict,
    connection_string: str,
    database_name: str,
) -> None:
    """Upsert signal docs (one per card) and a per-format stats doc."""
    try:
        from pymongo import MongoClient, UpdateOne
    except ImportError as e:
        raise SystemExit(
            "pymongo is required for --mongo output. Install with: pip install pymongo"
        ) from e

    client = MongoClient(connection_string)
    db = client[database_name]

    signals = db["meta_signals"]
    stats_col = db["meta_signal_stats"]

    # Idempotent indexes
    signals.create_index([("format", 1), ("card_name_lc", 1)], unique=True)
    signals.create_index([("format", 1), ("inclusion_rate", -1)])
    stats_col.create_index([("format", 1)], unique=True)

    if docs:
        ops = [
            UpdateOne(
                {"format": d["format"], "card_name_lc": d["card_name_lc"]},
                {"$set": d},
                upsert=True,
            )
            for d in docs
        ]
        result = signals.bulk_write(ops, ordered=False)
        print(
            f"  Mongo: upserted {result.upserted_count} / modified {result.modified_count} "
            f"/ matched {result.matched_count}"
        )

    # Drop stale card signals for this format that we didn't observe this run
    fmt = stats["format"]
    kept = {d["card_name_lc"] for d in docs}
    if kept:
        deleted = signals.delete_many({
            "format": fmt,
            "card_name_lc": {"$nin": list(kept)},
        }).deleted_count
        if deleted:
            print(f"  Mongo: pruned {deleted} stale signals for {fmt}")

    stats_col.update_one(
        {"format": fmt}, {"$set": stats}, upsert=True,
    )
    print(f"  Mongo: updated stats for {fmt} (sample_size={stats['sample_size']})")


# ─── Main ────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Aggregate MTGTop8 tournament data into meta-signal documents."
    )
    parser.add_argument(
        "--formats",
        nargs="+",
        choices=list(FORMAT_CODES.keys()),
        default=["commander", "modern", "pioneer", "standard", "pauper", "legacy"],
        help="Formats to aggregate (default: commander, modern, pioneer, standard, pauper, legacy)",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=150,
        help="Max decks sampled per format (default: 150)",
    )
    parser.add_argument(
        "--min-inclusions",
        type=int,
        default=2,
        help="Minimum inclusion count to emit a signal (default: 2 — filters noise)",
    )
    parser.add_argument(
        "--include-sideboard",
        action="store_true",
        help="Also count sideboard cards (off by default — mainboard signals are stronger)",
    )
    parser.add_argument(
        "--delay",
        type=float,
        default=0.2,
        help="Delay between HTTP requests in seconds (default: 0.2)",
    )
    parser.add_argument(
        "--mongo-uri",
        default=os.environ.get(
            "MONGODB_URI",
            "mongodb://admin:password@localhost:27017",
        ),
        help="MongoDB connection string (env MONGODB_URI; default local docker compose)",
    )
    parser.add_argument(
        "--mongo-db",
        default=os.environ.get("MONGODB_DB", "mtgforge"),
        help="MongoDB database name (env MONGODB_DB; default 'mtgforge')",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Skip MongoDB writes; print summary only",
    )
    parser.add_argument(
        "--out",
        type=str,
        default=None,
        help="Optional JSON file to write the aggregated signals to (for inspection).",
    )
    args = parser.parse_args()

    print("Tournament Meta Signals — aggregator")
    print(f"  Formats:         {', '.join(args.formats)}")
    print(f"  Per-format cap:  {args.limit} decks")
    print(f"  Min inclusions:  {args.min_inclusions}")
    print(f"  Sideboard:       {'included' if args.include_sideboard else 'excluded'}")
    print(f"  Mongo:           {'dry-run' if args.dry_run else args.mongo_uri}")

    all_results: dict[str, tuple[list[dict], dict]] = {}
    for fmt in args.formats:
        print(f"\n─── {fmt.upper()} ───")
        decks = scrape_deck_entries(fmt, args.limit, args.delay)
        docs, stats = aggregate_signals(
            decks, fmt, args.include_sideboard, args.min_inclusions,
        )
        all_results[fmt] = (docs, stats)
        if docs:
            top = docs[0]
            print(
                f"  {fmt}: {stats['sample_size']} decks → {len(docs)} cards "
                f"(top: {top['card_name']} @ {top['inclusion_rate']*100:.1f}%)"
            )
        else:
            print(f"  {fmt}: {stats['sample_size']} decks → 0 signals emitted")

    if args.out:
        out_path = Path(args.out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            fmt: {"stats": s, "signals": d}
            for fmt, (d, s) in all_results.items()
        }
        out_path.write_text(json.dumps(payload, indent=2))
        print(f"\nWrote aggregated signals → {out_path}")

    if args.dry_run:
        print("\nDry run — MongoDB not updated.")
        return

    print("\nWriting to MongoDB…")
    for fmt, (docs, stats) in all_results.items():
        if stats["sample_size"] == 0:
            print(f"  {fmt}: skipped (no data)")
            continue
        upsert_to_mongo(docs, stats, args.mongo_uri, args.mongo_db)

    print("\nDone. The .NET API will pick up the new signals on next deck generation.")


if __name__ == "__main__":
    main()
