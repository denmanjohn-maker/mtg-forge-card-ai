#!/usr/bin/env python3
"""
MTG Card Ingestion Pipeline
Pulls bulk card data from Scryfall → stores in MongoDB → embeds into Qdrant
Pricing data is sourced from MTGJSON (same source as the MtgDeckForge app).

Requirements:
    pip install requests pymongo qdrant-client sentence-transformers tqdm ijson

Usage:
    python ingest_cards.py
    python ingest_cards.py --skip-download        # reuse cached Scryfall bulk file
    python ingest_cards.py --skip-price-download  # reuse cached MTGJSON price files
    python ingest_cards.py --limit 1000           # test with subset
"""

import argparse
import json
import os
import sys
import time
from pathlib import Path

import ijson
import requests
from pymongo import MongoClient, UpdateOne
from pymongo.errors import BulkWriteError
from qdrant_client import QdrantClient
from qdrant_client.models import (
    Distance,
    VectorParams,
    PointStruct,
    Filter,
    FieldCondition,
    MatchValue,
)
from sentence_transformers import SentenceTransformer
from tqdm import tqdm

# ─── Config ──────────────────────────────────────────────────────────────────

MONGO_URI = os.getenv("MONGO_URI", "mongodb://admin:password@localhost:27017")
MONGO_DB = os.getenv("MONGO_DB", "mtgforge")
QDRANT_HOST = os.getenv("QDRANT_HOST", "localhost")
QDRANT_PORT = int(os.getenv("QDRANT_PORT", "6333"))
COLLECTION_NAME = "mtg_cards"
EMBED_MODEL = "all-MiniLM-L6-v2"  # Fast, local, 384-dim — runs well on M2
BULK_DATA_CACHE = Path("./data/scryfall_oracle_cards.json")
BATCH_SIZE = 256
SUPPORTED_FORMATS = ["commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage"]

# ─── MTGJSON Config ───────────────────────────────────────────────────────────
# Same sources used by MtgDeckForge's MtgJsonPricingImportService

MTGJSON_PRICES_URL = "https://mtgjson.com/api/v5/AllPricesToday.json"
MTGJSON_PRINTINGS_URL = "https://mtgjson.com/api/v5/AllPrintings.json"
MTGJSON_UUID_NAMES_CACHE = Path("./data/mtgjson_uuid_names.json")
MTGJSON_UUID_PRICES_CACHE = Path("./data/mtgjson_uuid_prices.json")
MTGJSON_NAME_PRICES_CACHE = Path("./data/mtgjson_name_prices.json")

# ─── Scryfall Download ────────────────────────────────────────────────────────

def get_bulk_download_uri() -> str:
    print("Fetching Scryfall bulk data index...")
    r = requests.get("https://api.scryfall.com/bulk-data", timeout=30)
    r.raise_for_status()
    for entry in r.json()["data"]:
        if entry["type"] == "oracle_cards":
            return entry["download_uri"]
    raise RuntimeError("Could not find oracle_cards bulk data URI")


def download_scryfall_data(force: bool = False) -> list[dict]:
    if BULK_DATA_CACHE.exists() and not force:
        print(f"Using cached data at {BULK_DATA_CACHE}")
        with open(BULK_DATA_CACHE) as f:
            return json.load(f)

    uri = get_bulk_download_uri()
    print(f"Downloading from {uri} ...")
    r = requests.get(uri, timeout=120, stream=True)
    r.raise_for_status()

    BULK_DATA_CACHE.parent.mkdir(parents=True, exist_ok=True)
    total = int(r.headers.get("content-length", 0))
    with open(BULK_DATA_CACHE, "wb") as f, tqdm(
        total=total, unit="B", unit_scale=True, desc="Downloading"
    ) as bar:
        for chunk in r.iter_content(chunk_size=8192):
            f.write(chunk)
            bar.update(len(chunk))

    with open(BULK_DATA_CACHE) as f:
        return json.load(f)


# ─── MTGJSON Pricing ─────────────────────────────────────────────────────────
# Mirrors the logic in MtgDeckForge's MtgJsonPricingImportService:
#   1. Stream AllPrintings → build uuid→name map
#   2. Stream AllPricesToday → take the first positive price per UUID
#   3. Join on UUID → normalized card name → price

def _fetch_uuid_to_name(force: bool = False) -> dict[str, str]:
    """Stream-parse AllPrintings to build a uuid→card_name mapping."""
    if MTGJSON_UUID_NAMES_CACHE.exists() and not force:
        print(f"Using cached UUID→name map at {MTGJSON_UUID_NAMES_CACHE}")
        with open(MTGJSON_UUID_NAMES_CACHE) as f:
            return json.load(f)

    # AllPrintings is ~700MB; download to disk first, then stream-parse
    tmp_path = Path("./data/mtgjson_printings_tmp.json")
    tmp_path.parent.mkdir(parents=True, exist_ok=True)

    print("Downloading MTGJSON AllPrintings (~700MB)...")
    with requests.get(MTGJSON_PRINTINGS_URL, stream=True, timeout=600) as r:
        r.raise_for_status()
        total = int(r.headers.get("content-length", 0))
        with open(tmp_path, "wb") as f, tqdm(total=total, unit="B", unit_scale=True, desc="AllPrintings") as bar:
            for chunk in r.iter_content(chunk_size=65536):
                f.write(chunk)
                bar.update(len(chunk))

    print("Parsing AllPrintings (uuid→name)...")
    uuid_to_name: dict[str, str] = {}
    with open(tmp_path, "rb") as f:
        # ijson.kvitems streams one set at a time under data.{set_code}
        for _set_code, set_obj in ijson.kvitems(f, "data"):
            for card in set_obj.get("cards", []):
                uuid = card.get("uuid")
                name = card.get("name") or card.get("faceName", "")
                if uuid and name and uuid not in uuid_to_name:
                    uuid_to_name[uuid] = name

    tmp_path.unlink(missing_ok=True)

    with open(MTGJSON_UUID_NAMES_CACHE, "w") as f:
        json.dump(uuid_to_name, f)
    print(f"MTGJSON: cached {len(uuid_to_name)} uuid→name entries")
    return uuid_to_name


def _first_positive_price(obj) -> float | None:
    """Recursively find the first positive numeric price in a nested price object."""
    if isinstance(obj, (int, float)) and obj > 0:
        return float(obj)
    if isinstance(obj, str):
        try:
            v = float(obj)
            if v > 0:
                return v
        except ValueError:
            pass
    if isinstance(obj, dict):
        for v in obj.values():
            result = _first_positive_price(v)
            if result is not None:
                return result
    if isinstance(obj, list):
        for v in obj:
            result = _first_positive_price(v)
            if result is not None:
                return result
    return None


def _fetch_uuid_to_price(force: bool = False) -> dict[str, float]:
    """Stream-parse AllPricesToday and take the first positive price per UUID."""
    if MTGJSON_UUID_PRICES_CACHE.exists() and not force:
        print(f"Using cached UUID→price map at {MTGJSON_UUID_PRICES_CACHE}")
        with open(MTGJSON_UUID_PRICES_CACHE) as f:
            return json.load(f)

    # Download to a temp file via iter_content so requests handles decompression
    # (r.raw passes compressed bytes directly to ijson which cannot decode them)
    tmp_path = Path("./data/mtgjson_prices_tmp.json")
    tmp_path.parent.mkdir(parents=True, exist_ok=True)

    print("Downloading MTGJSON AllPricesToday...")
    with requests.get(MTGJSON_PRICES_URL, stream=True, timeout=300) as r:
        r.raise_for_status()
        total = int(r.headers.get("content-length", 0))
        with open(tmp_path, "wb") as f, tqdm(total=total, unit="B", unit_scale=True, desc="AllPricesToday") as bar:
            for chunk in r.iter_content(chunk_size=65536):
                f.write(chunk)
                bar.update(len(chunk))

    print("Parsing AllPricesToday (uuid→price)...")
    uuid_to_price: dict[str, float] = {}
    with open(tmp_path, "rb") as f:
        for uuid, price_obj in tqdm(ijson.kvitems(f, "data"), desc="Prices"):
            price = _first_positive_price(price_obj)
            if price is not None:
                uuid_to_price[uuid] = price

    tmp_path.unlink(missing_ok=True)

    with open(MTGJSON_UUID_PRICES_CACHE, "w") as f:
        json.dump(uuid_to_price, f)
    print(f"MTGJSON: cached {len(uuid_to_price)} uuid→price entries")
    return uuid_to_price


def fetch_mtgjson_name_prices(force: bool = False) -> dict[str, float]:
    """Return a normalized-name→USD-price map sourced from MTGJSON.

    Uses the same AllPrintings + AllPricesToday sources as MtgDeckForge's
    MtgJsonPricingImportService so that price enforcement is consistent
    between the local LLM pipeline and the production app.
    """
    if MTGJSON_NAME_PRICES_CACHE.exists() and not force:
        print(f"Using cached name→price map at {MTGJSON_NAME_PRICES_CACHE}")
        with open(MTGJSON_NAME_PRICES_CACHE) as f:
            return json.load(f)

    uuid_to_name = _fetch_uuid_to_name(force)
    uuid_to_price = _fetch_uuid_to_price(force)

    name_to_price: dict[str, float] = {}
    for uuid, price in uuid_to_price.items():
        name = uuid_to_name.get(uuid)
        if not name:
            continue
        normalized = name.strip().lower()
        # Keep the lowest price when the same card name appears in multiple printings
        if normalized not in name_to_price or price < name_to_price[normalized]:
            name_to_price[normalized] = price

    with open(MTGJSON_NAME_PRICES_CACHE, "w") as f:
        json.dump(name_to_price, f)
    print(f"MTGJSON: {len(name_to_price)} unique card prices resolved")
    return name_to_price


# ─── MongoDB Ingestion ────────────────────────────────────────────────────────

def ingest_to_mongo(cards: list[dict]) -> None:
    client = MongoClient(MONGO_URI)
    db = client[MONGO_DB]
    collection = db["cards"]

    # Index for fast lookups
    collection.create_index("id", unique=True)
    collection.create_index("name")
    collection.create_index("color_identity")
    collection.create_index("legalities.commander")

    print(f"Upserting {len(cards)} cards into MongoDB...")
    ops = [
        UpdateOne({"id": card["id"]}, {"$set": card}, upsert=True)
        for card in cards
    ]

    for i in tqdm(range(0, len(ops), BATCH_SIZE), desc="MongoDB"):
        batch = ops[i : i + BATCH_SIZE]
        try:
            collection.bulk_write(batch, ordered=False)
        except BulkWriteError as e:
            print(f"Batch write warning (likely duplicate): {e.details['nInserted']} inserted")

    print(f"MongoDB: {collection.count_documents({})} total cards stored")
    client.close()


# ─── Qdrant Embedding ─────────────────────────────────────────────────────────

def build_card_text(card: dict) -> str:
    """Build a rich text representation of a card for embedding."""
    parts = [
        card.get("name", ""),
        card.get("type_line", ""),
        card.get("oracle_text", ""),
        card.get("flavor_text", ""),
        f"Mana cost: {card.get('mana_cost', '')}",
        f"CMC: {card.get('cmc', '')}",
        f"Colors: {' '.join(card.get('colors', []))}",
        f"Color identity: {' '.join(card.get('color_identity', []))}",
        f"Keywords: {' '.join(card.get('keywords', []))}",
        f"Power: {card.get('power', '')} Toughness: {card.get('toughness', '')}",
        f"Loyalty: {card.get('loyalty', '')}",
    ]
    return " | ".join(p for p in parts if p.strip(" |"))


def build_payload(card: dict, mtgjson_prices: dict[str, float] | None = None) -> dict:
    """Extract structured metadata for Qdrant payload (filterable fields).

    price_usd is sourced from MTGJSON (same as MtgDeckForge's PricingService)
    when available, with Scryfall prices as a fallback.
    """
    prices = card.get("prices", {})
    legalities = card.get("legalities", {})

    # Prefer MTGJSON price (matches production app); fall back to Scryfall
    scryfall_price = float(prices.get("usd") or 0)
    if mtgjson_prices is not None:
        normalized_name = card.get("name", "").strip().lower()
        price_usd = mtgjson_prices.get(normalized_name, scryfall_price)
    else:
        price_usd = scryfall_price

    payload = {
        "scryfall_id": card["id"],
        "name": card.get("name", ""),
        "mana_cost": card.get("mana_cost", ""),
        "cmc": float(card.get("cmc", 0)),
        "type_line": card.get("type_line", ""),
        "oracle_text": card.get("oracle_text", ""),
        "colors": card.get("colors", []),
        "color_identity": card.get("color_identity", []),
        "keywords": card.get("keywords", []),
        "power": card.get("power"),
        "toughness": card.get("toughness"),
        "loyalty": card.get("loyalty"),
        "set_name": card.get("set_name", ""),
        "rarity": card.get("rarity", ""),
        "price_usd": price_usd,
        "price_usd_foil": float(prices.get("usd_foil") or 0),
        "scryfall_uri": card.get("scryfall_uri", ""),
        "image_uri": card.get("image_uris", {}).get("normal", ""),
    }
    # Store legality for all supported formats as individual filterable fields
    for fmt in SUPPORTED_FORMATS:
        payload[f"legality_{fmt}"] = legalities.get(fmt, "not_legal")
    return payload


def ingest_to_qdrant(cards: list[dict], mtgjson_prices: dict[str, float] | None = None) -> None:
    client = QdrantClient(host=QDRANT_HOST, port=QDRANT_PORT)
    model = SentenceTransformer(EMBED_MODEL)
    vector_size = model.get_sentence_embedding_dimension()

    # Create or recreate collection
    existing = [c.name for c in client.get_collections().collections]
    if COLLECTION_NAME not in existing:
        print(f"Creating Qdrant collection '{COLLECTION_NAME}' (dim={vector_size})...")
        client.create_collection(
            collection_name=COLLECTION_NAME,
            vectors_config=VectorParams(size=vector_size, distance=Distance.COSINE),
        )
    else:
        print(f"Collection '{COLLECTION_NAME}' already exists, upserting...")

    # Include all cards legal in at least one supported format (not just Commander)
    legal_cards = [
        c for c in cards
        if any(c.get("legalities", {}).get(fmt) == "legal" for fmt in SUPPORTED_FORMATS)
    ]
    print(f"Embedding {len(legal_cards)} format-legal cards (of {len(cards)} total)...")

    for i in tqdm(range(0, len(legal_cards), BATCH_SIZE), desc="Qdrant"):
        batch = legal_cards[i : i + BATCH_SIZE]
        texts = [build_card_text(c) for c in batch]
        vectors = model.encode(texts, show_progress_bar=False).tolist()

        points = []
        for card, vector in zip(batch, vectors):
            # Use a stable integer ID from the scryfall UUID
            point_id = abs(hash(card["id"])) % (2**63)
            points.append(
                PointStruct(
                    id=point_id,
                    vector=vector,
                    payload=build_payload(card, mtgjson_prices),
                )
            )

        client.upsert(collection_name=COLLECTION_NAME, points=points)

    info = client.get_collection(COLLECTION_NAME)
    print(f"Qdrant: {info.points_count} vectors stored")


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Ingest MTG card data")
    parser.add_argument("--skip-download", action="store_true", help="Use cached Scryfall data")
    parser.add_argument("--skip-price-download", action="store_true", help="Use cached MTGJSON price files")
    parser.add_argument("--limit", type=int, default=None, help="Limit cards for testing")
    parser.add_argument("--mongo-only", action="store_true", help="Skip Qdrant embedding")
    parser.add_argument("--qdrant-only", action="store_true", help="Skip MongoDB ingestion")
    args = parser.parse_args()

    start = time.time()

    cards = download_scryfall_data(force=not args.skip_download)
    if args.limit:
        cards = cards[: args.limit]
        print(f"Limited to {len(cards)} cards for testing")

    # Fetch MTGJSON prices (same source as MtgDeckForge's PricingService)
    mtgjson_prices = None
    if not args.mongo_only:
        mtgjson_prices = fetch_mtgjson_name_prices(force=not args.skip_price_download)

    if not args.qdrant_only:
        ingest_to_mongo(cards)

    if not args.mongo_only:
        ingest_to_qdrant(cards, mtgjson_prices)

    elapsed = time.time() - start
    print(f"\nIngestion complete in {elapsed:.1f}s")
    print(f"  MongoDB : {MONGO_URI}/{MONGO_DB}/cards")
    print(f"  Qdrant  : http://{QDRANT_HOST}:{QDRANT_PORT}/collections/{COLLECTION_NAME}")


if __name__ == "__main__":
    main()
