#!/usr/bin/env python3
"""
Export Training Data
Pulls saved decks from MongoDB and formats them as instruction-tuning pairs
suitable for LoRA fine-tuning with Unsloth / HuggingFace TRL.

Output formats:
  - JSONL  (default)  — one JSON object per line, Alpaca-style
  - CSV               -- for quick inspection in Excel / Numbers
  - HuggingFace Dataset -- saved to disk, ready for trainer.train()

Usage:
    python export_training_data.py
    python export_training_data.py --format csv
    python export_training_data.py --min-cards 90   # only complete decks
    python export_training_data.py --out ./my_data.jsonl
"""

import argparse
import csv
import json
import os
from datetime import datetime
from pathlib import Path

from pymongo import MongoClient

# ─── Config ──────────────────────────────────────────────────────────────────

MONGO_URI = os.getenv("MONGO_URI", "mongodb://admin:password@localhost:27017")
MONGO_DB  = os.getenv("MONGO_DB",  "mtgforge")

SYSTEM_PROMPT = """You are an expert Magic: The Gathering Commander deckbuilder with deep knowledge of
card synergies, deck archetypes, and competitive strategy. When building a deck you must select exactly
99 cards plus 1 commander (100 total), respect the commander's color identity, include appropriate
ramp, card draw, removal, board wipes, and win conditions, and stay within the specified budget.
Always respond in JSON format with 'reasoning' and 'sections' fields."""


# ─── Helpers ─────────────────────────────────────────────────────────────────

def deck_to_instruction(deck: dict) -> str:
    """Build the user instruction string from a saved deck document."""
    colors = ", ".join(deck.get("colorIdentity") or deck.get("color_identity", []))
    return (
        f"Build a Commander deck with the following requirements:\n"
        f"- Commander: {deck.get('commander', 'Unknown')}\n"
        f"- Theme/Strategy: {deck.get('theme', 'Unknown')}\n"
        f"- Color Identity: {colors}\n"
        f"- Budget: ${deck.get('estimatedCost', deck.get('estimated_cost', 0)):.2f} total\n"
        f"- Power Level: {deck.get('powerLevel', deck.get('power_level', 6))}/10"
    )


def deck_to_output(deck: dict) -> str:
    """Serialize a deck back to the JSON format the LLM should produce."""
    sections = deck.get("sections", [])

    # Normalize field names — MongoDB may have camelCase or snake_case
    normalized_sections = []
    for s in sections:
        normalized_sections.append({
            "category": s.get("category", s.get("Category", "Other")),
            "cards": [
                {
                    "name":     c.get("name",     c.get("Name", "")),
                    "quantity": c.get("quantity", c.get("Quantity", 1))
                }
                for c in s.get("cards", s.get("Cards", []))
            ]
        })

    return json.dumps({
        "reasoning": deck.get("reasoning", deck.get("Reasoning", "")),
        "sections":  normalized_sections
    }, indent=2)


def count_cards(deck: dict) -> int:
    total = 0
    for s in deck.get("sections", []):
        for c in s.get("cards", s.get("Cards", [])):
            total += c.get("quantity", c.get("Quantity", 1))
    return total


# ─── Fetch from MongoDB ───────────────────────────────────────────────────────

def fetch_decks(min_cards: int = 80) -> list[dict]:
    client = MongoClient(MONGO_URI)
    db     = client[MONGO_DB]
    col    = db["decks"]

    all_decks = list(col.find({}, {"_id": 0}))
    print(f"Found {len(all_decks)} saved decks in MongoDB")

    # Filter out incomplete decks (LLM sometimes produces partial outputs)
    valid = [d for d in all_decks if count_cards(d) >= min_cards]
    print(f"  → {len(valid)} meet the minimum card count ({min_cards})")

    client.close()
    return valid


# ─── Export Formats ───────────────────────────────────────────────────────────

def export_jsonl(decks: list[dict], out_path: Path) -> None:
    """
    Alpaca-style JSONL format:
    {"instruction": "...", "input": "", "output": "..."}

    Compatible with: Unsloth, TRL SFTTrainer, LLaMA-Factory
    """
    with open(out_path, "w") as f:
        for deck in decks:
            record = {
                "instruction": SYSTEM_PROMPT,
                "input":       deck_to_instruction(deck),
                "output":      deck_to_output(deck)
            }
            f.write(json.dumps(record) + "\n")

    print(f"Exported {len(decks)} training examples → {out_path}")


def export_chatml(decks: list[dict], out_path: Path) -> None:
    """
    ChatML format — works directly with Ollama's Modelfile and HuggingFace chat templates.
    {"messages": [{"role": "system", ...}, {"role": "user", ...}, {"role": "assistant", ...}]}
    """
    with open(out_path, "w") as f:
        for deck in decks:
            record = {
                "messages": [
                    {"role": "system",    "content": SYSTEM_PROMPT},
                    {"role": "user",      "content": deck_to_instruction(deck)},
                    {"role": "assistant", "content": deck_to_output(deck)}
                ]
            }
            f.write(json.dumps(record) + "\n")

    print(f"Exported {len(decks)} ChatML examples → {out_path}")


def export_csv(decks: list[dict], out_path: Path) -> None:
    """CSV for quick review — not used for training directly."""
    with open(out_path, "w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "commander", "theme", "color_identity", "budget",
            "power_level", "card_count", "reasoning_preview"
        ])
        writer.writeheader()
        for deck in decks:
            writer.writerow({
                "commander":       deck.get("commander", ""),
                "theme":           deck.get("theme", ""),
                "color_identity":  ",".join(deck.get("colorIdentity", [])),
                "budget":          f"${deck.get('estimatedCost', 0):.2f}",
                "power_level":     deck.get("powerLevel", ""),
                "card_count":      count_cards(deck),
                "reasoning_preview": (deck.get("reasoning", ""))[:120]
            })

    print(f"Exported {len(decks)} rows → {out_path}")


def export_hf_dataset(decks: list[dict], out_dir: Path) -> None:
    """
    HuggingFace Dataset saved to disk.
    Load later with: dataset = load_from_disk('./data/hf_dataset')
    """
    try:
        from datasets import Dataset
    except ImportError:
        print("datasets not installed. Run: pip install datasets")
        return

    records = [
        {
            "instruction": SYSTEM_PROMPT,
            "input":       deck_to_instruction(d),
            "output":      deck_to_output(d)
        }
        for d in decks
    ]

    dataset = Dataset.from_list(records)
    dataset.save_to_disk(str(out_dir))
    print(f"Saved HuggingFace Dataset ({len(decks)} examples) → {out_dir}")
    print(f"  Load with: from datasets import load_from_disk; ds = load_from_disk('{out_dir}')")


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Export MTG deck training data")
    parser.add_argument("--format",    choices=["jsonl", "chatml", "csv", "hf"], default="jsonl")
    parser.add_argument("--out",       type=str, default=None, help="Output path")
    parser.add_argument("--min-cards", type=int, default=80,   help="Min cards for a valid deck")
    args = parser.parse_args()

    decks = fetch_decks(min_cards=args.min_cards)

    if not decks:
        print("No valid decks found. Generate some decks first using the API!")
        return

    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")

    if args.format == "jsonl":
        out = Path(args.out or f"./data/training_{timestamp}.jsonl")
        out.parent.mkdir(parents=True, exist_ok=True)
        export_jsonl(decks, out)

    elif args.format == "chatml":
        out = Path(args.out or f"./data/training_chatml_{timestamp}.jsonl")
        out.parent.mkdir(parents=True, exist_ok=True)
        export_chatml(decks, out)

    elif args.format == "csv":
        out = Path(args.out or f"./data/training_{timestamp}.csv")
        out.parent.mkdir(parents=True, exist_ok=True)
        export_csv(decks, out)

    elif args.format == "hf":
        out = Path(args.out or f"./data/hf_dataset_{timestamp}")
        out.mkdir(parents=True, exist_ok=True)
        export_hf_dataset(decks, out)

    print("\nTip: Run finetune.py next to train a LoRA adapter on this data.")


if __name__ == "__main__":
    main()
