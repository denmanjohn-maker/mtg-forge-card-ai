#!/usr/bin/env python3
"""
Scrape Tournament Decklists
Scrapes winning MTG decklists from MTGTop8, enriches cards via the Scryfall API,
and formats the results as Alpaca-style or ChatML instruction-tuning training data
compatible with export_training_data.py and finetune.py.

Data flow:
  1. Crawl MTGTop8 format pages to discover recent events
  2. Parse each event page to extract deck IDs, archetype names, and player names
  3. Download plain-text decklists via the MTGTop8 export endpoint
  4. Enrich every card with Scryfall (type, oracle text, price, color identity, rarity)
  5. Categorize cards into sections, compute budget, infer color identity
  6. Emit one JSONL training example per deck

Output formats:
  - JSONL  (default)  — Alpaca-style: {"instruction": "...", "input": "...", "output": "..."}
  - ChatML            — {"messages": [{"role": "system", ...}, ...]}

Usage:
    python scrape_decklists.py
    python scrape_decklists.py --formats modern pioneer --limit 20
    python scrape_decklists.py --format chatml --out ./data/scraped_chatml.jsonl
    python scrape_decklists.py --formats commander --limit 10 --delay 0.5
"""

import argparse
import json
import re
import time
from datetime import datetime
from pathlib import Path

import requests
from bs4 import BeautifulSoup
from tqdm import tqdm

# ─── Constants ────────────────────────────────────────────────────────────────

MTGTOP8_BASE = "https://www.mtgtop8.com"

FORMAT_CODES = {
    "standard":  "ST",
    "modern":    "MO",
    "pioneer":   "PI",
    "pauper":    "PAU",
    "legacy":    "LE",
    "vintage":   "VI",
    "commander": "EDH",
}

# Maps format names to deck size used for generating the training "input"
FORMAT_DECK_SIZES = {
    "commander": 100,
    "standard":  60,
    "modern":    60,
    "pioneer":   60,
    "pauper":    60,
    "legacy":    60,
    "vintage":   60,
}

# Matches the system prompt from export_training_data.py / DeckGenerationService
SYSTEM_PROMPT = (
    "You are an expert Magic: The Gathering deckbuilder with deep knowledge of "
    "card synergies, deck archetypes, and competitive strategy. When building a deck you must "
    "select the correct number of cards for the format, respect color identity constraints, "
    "include appropriate ramp, card draw, removal, and win conditions, and stay within the "
    "specified budget. Always respond in JSON format with 'reasoning' and 'sections' fields."
)

USER_AGENT = "MtgForgeAI/1.0 (training-data-pipeline; +https://github.com/mtg-forge-card-ai)"

SCRYFALL_HEADERS = {
    "User-Agent": USER_AGENT,
    "Accept": "application/json",
}

MTGTOP8_HEADERS = {
    "User-Agent": USER_AGENT,
}

# ─── Scryfall Card Cache ─────────────────────────────────────────────────────

_scryfall_cache: dict[str, dict | None] = {}


def scryfall_lookup(card_name: str, delay: float) -> dict | None:
    """Look up a single card by exact name via the Scryfall API, with caching."""
    key = card_name.lower().strip()
    if key in _scryfall_cache:
        return _scryfall_cache[key]

    time.sleep(delay)
    try:
        resp = requests.get(
            "https://api.scryfall.com/cards/named",
            params={"exact": card_name},
            headers=SCRYFALL_HEADERS,
            timeout=15,
        )
        if resp.status_code == 200:
            data = resp.json()
            _scryfall_cache[key] = data
            return data
        # Card not found or ambiguous — try fuzzy
        if resp.status_code == 404:
            time.sleep(delay)
            resp2 = requests.get(
                "https://api.scryfall.com/cards/named",
                params={"fuzzy": card_name},
                headers=SCRYFALL_HEADERS,
                timeout=15,
            )
            if resp2.status_code == 200:
                data = resp2.json()
                _scryfall_cache[key] = data
                return data
    except requests.RequestException:
        pass

    _scryfall_cache[key] = None
    return None


def get_card_price(card: dict) -> float:
    """Extract USD price from a Scryfall card object."""
    prices = card.get("prices", {})
    usd = prices.get("usd") or prices.get("usd_foil") or "0"
    try:
        return float(usd)
    except (ValueError, TypeError):
        return 0.0


def get_color_identity(card: dict) -> list[str]:
    """Extract color identity from a Scryfall card object."""
    return card.get("color_identity", [])


def get_type_line(card: dict) -> str:
    """Extract type line, handling double-faced cards."""
    return card.get("type_line", "")


def get_oracle_text(card: dict) -> str:
    """Extract oracle text, handling double-faced cards."""
    text = card.get("oracle_text", "")
    if not text and "card_faces" in card:
        texts = [f.get("oracle_text", "") for f in card["card_faces"]]
        text = " // ".join(t for t in texts if t)
    return text


def get_mana_cost(card: dict) -> str:
    """Extract mana cost, handling double-faced cards."""
    cost = card.get("mana_cost", "")
    if not cost and "card_faces" in card:
        cost = card["card_faces"][0].get("mana_cost", "")
    return cost


# ─── Card Categorization ─────────────────────────────────────────────────────

def guess_category(type_line: str) -> str:
    """Categorize a card by its type line — matches DeckGenerationService.GuessCategory."""
    if not type_line:
        return "Other"
    if "Land" in type_line:
        return "Lands"
    if "Creature" in type_line:
        return "Creatures"
    if "Planeswalker" in type_line:
        return "Planeswalkers"
    if "Instant" in type_line:
        return "Instants"
    if "Sorcery" in type_line:
        return "Sorceries"
    if "Enchantment" in type_line:
        return "Enchantments"
    if "Artifact" in type_line:
        return "Artifacts"
    return "Other"


# ─── MTGTop8 Scraping ────────────────────────────────────────────────────────

def fetch_event_ids(format_code: str, limit: int, delay: float) -> list[tuple[int, str]]:
    """
    Fetch recent event IDs for a given format from the MTGTop8 format page.
    Returns list of (event_id, event_name).
    """
    url = f"{MTGTOP8_BASE}/format?f={format_code}"
    try:
        resp = requests.get(url, headers=MTGTOP8_HEADERS, timeout=30)
        resp.raise_for_status()
    except requests.RequestException as e:
        print(f"  ⚠ Failed to fetch format page {url}: {e}")
        return []

    soup = BeautifulSoup(resp.text, "html.parser")
    events: list[tuple[int, str]] = []

    for link in soup.find_all("a", href=True):
        href = link["href"]
        match = re.search(r"event\?e=(\d+)&f=", href)
        if match:
            event_id = int(match.group(1))
            event_name = link.get_text(strip=True)
            if event_id not in {eid for eid, _ in events}:
                events.append((event_id, event_name))

    # We collect more events than needed since each yields multiple decks
    max_events = max(limit, 10)
    return events[:max_events]


def fetch_decks_from_event(
    event_id: int, format_code: str, delay: float
) -> list[dict]:
    """
    Parse an event page to extract deck entries.
    Returns list of dicts with keys: deck_id, archetype, player, placement.
    """
    url = f"{MTGTOP8_BASE}/event?e={event_id}&f={format_code}"
    time.sleep(delay)
    try:
        resp = requests.get(url, headers=MTGTOP8_HEADERS, timeout=30)
        resp.raise_for_status()
    except requests.RequestException as e:
        print(f"  ⚠ Failed to fetch event {event_id}: {e}")
        return []

    soup = BeautifulSoup(resp.text, "html.parser")
    decks: list[dict] = []

    for link in soup.find_all("a", href=True):
        href = link["href"]
        match = re.search(r"[?&]e=\d+&d=(\d+)&f=", href)
        if not match:
            continue

        deck_id = int(match.group(1))
        if deck_id in {d["deck_id"] for d in decks}:
            continue

        archetype = link.get_text(strip=True)
        if not archetype or archetype in ("→", "←"):
            continue

        # Skip non-archetype links (player names, etc.)
        parent = link.find_parent("div", class_="S14")
        if parent is None:
            continue

        # Try to find placement from sibling div
        placement_div = parent.find_parent("div")
        placement = len(decks) + 1

        if placement_div:
            rank_div = placement_div.find("div", class_="S14", string=re.compile(r"^\d+$"))
            if rank_div:
                try:
                    placement = int(rank_div.get_text(strip=True))
                except ValueError:
                    pass

        # Find player name
        player = ""
        player_link = placement_div.find("a", class_="player") if placement_div else None
        if player_link:
            player = player_link.get_text(strip=True)

        decks.append({
            "deck_id":   deck_id,
            "archetype": archetype,
            "player":    player,
            "placement": placement,
        })

    return decks


def fetch_decklist(deck_id: int, delay: float) -> list[tuple[int, str, bool]]:
    """
    Download a plain-text decklist from MTGTop8.
    Returns list of (quantity, card_name, is_sideboard).
    """
    url = f"{MTGTOP8_BASE}/mtgo?d={deck_id}"
    time.sleep(delay)
    try:
        resp = requests.get(url, headers=MTGTOP8_HEADERS, timeout=30)
        resp.raise_for_status()
    except requests.RequestException as e:
        print(f"  ⚠ Failed to fetch decklist {deck_id}: {e}")
        return []

    cards: list[tuple[int, str, bool]] = []
    is_sideboard = False

    for line in resp.text.strip().splitlines():
        line = line.strip()
        if not line:
            continue
        if line.lower().startswith("sideboard"):
            is_sideboard = True
            continue

        match = re.match(r"^(\d+)\s+(.+)$", line)
        if match:
            qty = int(match.group(1))
            name = match.group(2).strip()
            cards.append((qty, name, is_sideboard))

    return cards


# ─── Deck Processing ─────────────────────────────────────────────────────────

def process_deck(
    deck_meta: dict,
    format_name: str,
    delay: float,
) -> dict | None:
    """
    Download and enrich a single deck.
    Returns a dict ready for training export, or None on failure.
    """
    deck_id = deck_meta["deck_id"]
    archetype = deck_meta["archetype"]

    raw_cards = fetch_decklist(deck_id, delay)
    if not raw_cards:
        return None

    mainboard = [(qty, name) for qty, name, sb in raw_cards if not sb]
    sideboard = [(qty, name) for qty, name, sb in raw_cards if sb]

    if len(mainboard) < 10:
        return None

    # Enrich via Scryfall
    enriched: list[dict] = []
    all_colors: set[str] = set()
    total_cost = 0.0
    commander_name = None

    all_card_names = set()
    for qty, name in mainboard + sideboard:
        all_card_names.add(name)

    for card_name in all_card_names:
        scryfall_lookup(card_name, delay)

    for qty, name in mainboard:
        card_data = _scryfall_cache.get(name.lower().strip())
        type_line = get_type_line(card_data) if card_data else ""
        price = get_card_price(card_data) if card_data else 0.0
        colors = get_color_identity(card_data) if card_data else []
        oracle = get_oracle_text(card_data) if card_data else ""
        mana_cost = get_mana_cost(card_data) if card_data else ""
        rarity = (card_data.get("rarity", "") if card_data else "")

        all_colors.update(colors)
        total_cost += price * qty

        # Detect commander for EDH (legendary creature in the 1-of slot)
        if (
            format_name == "commander"
            and commander_name is None
            and "Legendary" in type_line
            and ("Creature" in type_line or "Planeswalker" in type_line)
        ):
            commander_name = name

        enriched.append({
            "name":      name,
            "quantity":  qty,
            "type_line": type_line,
            "category":  guess_category(type_line),
            "price":     price,
            "oracle":    oracle,
            "mana_cost": mana_cost,
            "rarity":    rarity,
        })

    # Group into sections
    sections_map: dict[str, list[dict]] = {}
    for card in enriched:
        cat = card["category"]
        if cat not in sections_map:
            sections_map[cat] = []
        sections_map[cat].append({
            "name":     card["name"],
            "quantity": card["quantity"],
        })

    # Stable category ordering
    category_order = [
        "Commander", "Creatures", "Planeswalkers", "Instants", "Sorceries",
        "Enchantments", "Artifacts", "Lands", "Other",
    ]
    sections = []

    # If commander was detected, split it out into its own section
    if commander_name and format_name == "commander":
        sections.append({
            "category": "Commander",
            "cards": [{"name": commander_name, "quantity": 1}],
        })
        # Remove commander from its original category
        for cat, cards in sections_map.items():
            sections_map[cat] = [c for c in cards if c["name"] != commander_name]

    for cat in category_order:
        if cat == "Commander":
            continue
        if cat in sections_map and sections_map[cat]:
            sections.append({"category": cat, "cards": sections_map[cat]})

    # Add any categories not in the ordered list
    for cat, cards in sections_map.items():
        if cat not in category_order and cards:
            sections.append({"category": cat, "cards": cards})

    # Sideboard section
    if sideboard:
        sb_cards = [{"name": n, "quantity": q} for q, n in sideboard]
        sections.append({"category": "Sideboard", "cards": sb_cards})

    # Infer color identity string
    color_map = {"W": "White", "U": "Blue", "B": "Black", "R": "Red", "G": "Green"}
    color_names = [color_map.get(c, c) for c in sorted(all_colors)]
    if not color_names:
        color_names = ["Colorless"]

    # Determine power level heuristic (placement based)
    placement = deck_meta.get("placement", 5)
    if placement <= 2:
        power_level = 8
    elif placement <= 4:
        power_level = 7
    else:
        power_level = 6

    # Generate reasoning from archetype and composition
    reasoning = generate_reasoning(archetype, format_name, enriched, color_names)

    deck_size = FORMAT_DECK_SIZES.get(format_name, 60)

    return {
        "commander":      commander_name or "",
        "archetype":      archetype,
        "format":         format_name,
        "colors":         color_names,
        "color_letters":  sorted(all_colors),
        "estimated_cost": round(total_cost, 2),
        "power_level":    power_level,
        "deck_size":      deck_size,
        "sections":       sections,
        "reasoning":      reasoning,
        "player":         deck_meta.get("player", ""),
        "placement":      placement,
    }


def generate_reasoning(
    archetype: str,
    format_name: str,
    cards: list[dict],
    color_names: list[str],
) -> str:
    """Build a strategy description from deck composition."""
    creature_count = sum(c["quantity"] for c in cards if c["category"] == "Creatures")
    instant_count = sum(c["quantity"] for c in cards if c["category"] == "Instants")
    sorcery_count = sum(c["quantity"] for c in cards if c["category"] == "Sorceries")
    land_count = sum(c["quantity"] for c in cards if c["category"] == "Lands")
    artifact_count = sum(c["quantity"] for c in cards if c["category"] == "Artifacts")
    enchantment_count = sum(c["quantity"] for c in cards if c["category"] == "Enchantments")

    spell_count = instant_count + sorcery_count
    nonland_count = sum(c["quantity"] for c in cards) - land_count

    colors_str = ", ".join(color_names)
    fmt = format_name.capitalize()

    parts = [
        f"This is a {colors_str} {archetype} deck built for {fmt} format.",
    ]

    if creature_count > nonland_count * 0.5:
        parts.append(
            f"The deck is creature-heavy with {creature_count} creatures, "
            f"aiming to establish board presence and apply pressure."
        )
    elif spell_count > nonland_count * 0.5:
        parts.append(
            f"The deck is spell-focused with {spell_count} instants/sorceries, "
            f"leveraging interaction and card advantage."
        )
    elif artifact_count > nonland_count * 0.3:
        parts.append(
            f"The deck relies heavily on artifacts ({artifact_count} total) "
            f"for its core strategy."
        )

    if creature_count > 0:
        # Find the most-played creature
        creatures = [c for c in cards if c["category"] == "Creatures"]
        creatures.sort(key=lambda c: c["quantity"], reverse=True)
        top_creature = creatures[0]["name"]
        parts.append(f"Key creature: {top_creature}.")

    if land_count > 0:
        parts.append(f"The mana base runs {land_count} lands for consistent plays.")

    return " ".join(parts)


# ─── Training Data Export ─────────────────────────────────────────────────────

def deck_to_instruction(deck: dict) -> str:
    """Build the user instruction string from a processed deck — matches export_training_data.py."""
    colors = ", ".join(deck["colors"])
    fmt = deck["format"].capitalize()

    lines = [f"Build a {fmt} deck with the following requirements:"]

    if deck["commander"]:
        lines.append(f"- Commander: {deck['commander']}")

    lines.append(f"- Theme/Strategy: {deck['archetype']}")
    lines.append(f"- Color Identity: {colors}")
    lines.append(f"- Budget: ${deck['estimated_cost']:.2f} total")
    lines.append(f"- Power Level: {deck['power_level']}/10")

    return "\n".join(lines)


def deck_to_output(deck: dict) -> str:
    """Serialize a deck to the JSON format the LLM should produce."""
    sections = []
    for s in deck["sections"]:
        sections.append({
            "category": s["category"],
            "cards": [
                {"name": c["name"], "quantity": c["quantity"]}
                for c in s["cards"]
            ],
        })

    return json.dumps({
        "reasoning": deck["reasoning"],
        "sections":  sections,
    }, indent=2)


def export_jsonl(decks: list[dict], out_path: Path) -> None:
    """Alpaca-style JSONL — compatible with Unsloth, TRL SFTTrainer."""
    with open(out_path, "w") as f:
        for deck in decks:
            record = {
                "instruction": SYSTEM_PROMPT,
                "input":       deck_to_instruction(deck),
                "output":      deck_to_output(deck),
            }
            f.write(json.dumps(record) + "\n")

    print(f"\nExported {len(decks)} training examples → {out_path}")


def export_chatml(decks: list[dict], out_path: Path) -> None:
    """ChatML format — works with Ollama Modelfile and HuggingFace chat templates."""
    with open(out_path, "w") as f:
        for deck in decks:
            record = {
                "messages": [
                    {"role": "system",    "content": SYSTEM_PROMPT},
                    {"role": "user",      "content": deck_to_instruction(deck)},
                    {"role": "assistant", "content": deck_to_output(deck)},
                ],
            }
            f.write(json.dumps(record) + "\n")

    print(f"\nExported {len(decks)} ChatML examples → {out_path}")


# ─── Main ─────────────────────────────────────────────────────────────────────

def scrape_format(format_name: str, limit: int, delay: float) -> list[dict]:
    """Scrape up to `limit` decks for a single format."""
    code = FORMAT_CODES[format_name]
    print(f"\n{'─' * 60}")
    print(f"Scraping {format_name.upper()} (code={code}, limit={limit})")
    print(f"{'─' * 60}")

    events = fetch_event_ids(code, limit, delay)
    if not events:
        print(f"  No events found for {format_name}")
        return []

    print(f"  Found {len(events)} events")

    all_deck_metas: list[dict] = []
    for event_id, event_name in events:
        if len(all_deck_metas) >= limit:
            break
        time.sleep(delay)
        deck_metas = fetch_decks_from_event(event_id, code, delay)
        for dm in deck_metas:
            dm["event_name"] = event_name
        all_deck_metas.extend(deck_metas)

    all_deck_metas = all_deck_metas[:limit]
    print(f"  Collected {len(all_deck_metas)} deck references")

    processed: list[dict] = []
    for dm in tqdm(all_deck_metas, desc=f"  {format_name}", unit="deck"):
        try:
            deck = process_deck(dm, format_name, delay)
            if deck:
                processed.append(deck)
        except Exception as e:
            tqdm.write(f"  ⚠ Skipping deck {dm['deck_id']}: {e}")

    print(f"  Successfully processed {len(processed)} / {len(all_deck_metas)} decks")
    return processed


def main():
    parser = argparse.ArgumentParser(
        description="Scrape MTG tournament decklists and format as training data"
    )
    parser.add_argument(
        "--formats",
        nargs="+",
        choices=list(FORMAT_CODES.keys()),
        default=list(FORMAT_CODES.keys()),
        help="Formats to scrape (default: all)",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=50,
        help="Max decks per format (default: 50)",
    )
    parser.add_argument(
        "--format",
        choices=["jsonl", "chatml"],
        default="jsonl",
        dest="output_format",
        help="Output format (default: jsonl)",
    )
    parser.add_argument(
        "--out",
        type=str,
        default=None,
        help="Output file path",
    )
    parser.add_argument(
        "--delay",
        type=float,
        default=0.2,
        help="Delay between HTTP requests in seconds (default: 0.2)",
    )
    args = parser.parse_args()

    print(f"MTGTop8 Decklist Scraper")
    print(f"Formats: {', '.join(args.formats)}")
    print(f"Limit per format: {args.limit}")
    print(f"Output format: {args.output_format}")
    print(f"Request delay: {args.delay}s")

    all_decks: list[dict] = []
    for fmt in args.formats:
        decks = scrape_format(fmt, args.limit, args.delay)
        all_decks.extend(decks)

    if not all_decks:
        print("\nNo decks scraped. Check your network connection and try again.")
        return

    print(f"\n{'═' * 60}")
    print(f"Total decks processed: {len(all_decks)}")
    print(f"Scryfall cache size:   {len(_scryfall_cache)} cards")

    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")

    if args.output_format == "jsonl":
        out = Path(args.out or f"./data/scraped_training_{timestamp}.jsonl")
        out.parent.mkdir(parents=True, exist_ok=True)
        export_jsonl(all_decks, out)
    else:
        out = Path(args.out or f"./data/scraped_training_chatml_{timestamp}.jsonl")
        out.parent.mkdir(parents=True, exist_ok=True)
        export_chatml(all_decks, out)

    print("\nTip: Combine with export_training_data.py output for a richer training set.")
    print("     cat data/training_*.jsonl data/scraped_training_*.jsonl > data/combined.jsonl")
    print("     python finetune.py --data ./data/combined.jsonl")


if __name__ == "__main__":
    main()
