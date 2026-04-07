#!/usr/bin/env python3
"""
MTG Forge — LoRA Fine-Tuning Pipeline
Fine-tunes Llama 3.1 8B on your saved MTG deck data using Unsloth + TRL.

Unsloth makes 4-bit LoRA training fast and memory-efficient — Llama 3.1 8B
fits comfortably in 8–10GB RAM, which is well within the M2 16GB budget
alongside your other services being stopped during training.

Prerequisites:
    pip install unsloth datasets trl transformers accelerate

Usage:
    # Export training data first
    python export_training_data.py

    # Then fine-tune
    python finetune.py --data ./data/training_latest.jsonl
    python finetune.py --data ./data/training.jsonl --epochs 3 --output ./my_model
    python finetune.py --test-only   # Test inference without training

After training, the adapter is saved and can be merged into the base model
or loaded separately for faster iteration.
"""

import argparse
import json
import sys
from pathlib import Path

# ─── Check dependencies ───────────────────────────────────────────────────────

def check_deps():
    missing = []
    for pkg in ["unsloth", "trl", "transformers", "datasets"]:
        try:
            __import__(pkg)
        except ImportError:
            missing.append(pkg)
    if missing:
        print(f"Missing packages: {', '.join(missing)}")
        print(f"Install with: pip install {' '.join(missing)}")
        sys.exit(1)

check_deps()

# ─── Imports (after dep check) ────────────────────────────────────────────────

from unsloth import FastLanguageModel
from trl import SFTTrainer, SFTConfig
from datasets import Dataset
import torch

# ─── Config ───────────────────────────────────────────────────────────────────

BASE_MODEL   = "unsloth/Meta-Llama-3.1-8B-Instruct-bnb-4bit"
MAX_SEQ_LEN  = 4096   # Deck outputs can be long
LORA_RANK    = 16     # Higher = more capacity but more VRAM
LORA_ALPHA   = 32
LORA_DROPOUT = 0.05

# Alpaca prompt template — matches our export format
ALPACA_TEMPLATE = """Below is an instruction that describes a task, paired with input that provides further context. Write a response that appropriately completes the request.

### Instruction:
{}

### Input:
{}

### Response:
{}"""

EOS_TOKEN_PLACEHOLDER = "{eos}"


# ─── Data Loading ─────────────────────────────────────────────────────────────

def load_jsonl(path: str) -> Dataset:
    records = []
    with open(path) as f:
        for line in f:
            line = line.strip()
            if line:
                records.append(json.loads(line))

    print(f"Loaded {len(records)} training examples from {path}")
    return Dataset.from_list(records)


def format_example(example: dict, tokenizer) -> dict:
    """Format a training example into the Alpaca prompt template."""
    text = ALPACA_TEMPLATE.format(
        example["instruction"],
        example["input"],
        example["output"]
    ) + tokenizer.eos_token
    return {"text": text}


# ─── Model Loading ────────────────────────────────────────────────────────────

def load_model_and_tokenizer():
    print(f"Loading base model: {BASE_MODEL}")
    print("This will download ~5GB on first run...")

    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=BASE_MODEL,
        max_seq_length=MAX_SEQ_LEN,
        dtype=None,          # Auto-detect: bfloat16 on M2
        load_in_4bit=True,   # 4-bit quantization — fits in 8GB
    )

    # Apply LoRA adapters — we only train a small subset of parameters
    model = FastLanguageModel.get_peft_model(
        model,
        r=LORA_RANK,
        target_modules=[
            "q_proj", "k_proj", "v_proj", "o_proj",   # Attention layers
            "gate_proj", "up_proj", "down_proj"         # MLP layers
        ],
        lora_alpha=LORA_ALPHA,
        lora_dropout=LORA_DROPOUT,
        bias="none",
        use_gradient_checkpointing="unsloth",  # Reduces VRAM significantly
        random_state=42,
    )

    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    total     = sum(p.numel() for p in model.parameters())
    print(f"Trainable parameters: {trainable:,} / {total:,} ({100*trainable/total:.2f}%)")

    return model, tokenizer


# ─── Training ─────────────────────────────────────────────────────────────────

def train(data_path: str, output_dir: str, num_epochs: int = 2):
    model, tokenizer = load_model_and_tokenizer()

    # Load and format dataset
    raw_dataset = load_jsonl(data_path)
    dataset = raw_dataset.map(
        lambda ex: format_example(ex, tokenizer),
        remove_columns=raw_dataset.column_names
    )

    print(f"\nTraining on {len(dataset)} examples for {num_epochs} epoch(s)")
    print(f"Output will be saved to: {output_dir}\n")

    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=dataset,
        dataset_text_field="text",
        max_seq_length=MAX_SEQ_LEN,
        args=SFTConfig(
            output_dir=output_dir,
            num_train_epochs=num_epochs,
            per_device_train_batch_size=1,    # Keep low for 16GB
            gradient_accumulation_steps=4,    # Effective batch size = 4
            warmup_steps=10,
            learning_rate=2e-4,
            fp16=False,      # M2 uses bfloat16
            bf16=True,
            logging_steps=5,
            save_steps=50,
            save_total_limit=2,
            optim="adamw_8bit",
            weight_decay=0.01,
            lr_scheduler_type="cosine",
            seed=42,
            report_to="none",   # Set to "wandb" if you want experiment tracking
        ),
    )

    print("Starting training...")
    trainer.train()

    # Save the LoRA adapter
    adapter_path = Path(output_dir) / "lora_adapter"
    model.save_pretrained(str(adapter_path))
    tokenizer.save_pretrained(str(adapter_path))
    print(f"\nLoRA adapter saved to: {adapter_path}")

    return model, tokenizer, adapter_path


# ─── Merge + Export for Ollama ────────────────────────────────────────────────

def merge_and_export(model, tokenizer, adapter_path: Path, output_dir: str):
    """
    Merge the LoRA weights into the base model and export in GGUF format
    so Ollama can serve it locally.
    """
    merged_path = Path(output_dir) / "merged_model"
    gguf_path   = Path(output_dir) / "mtg_forge.gguf"

    print("\nMerging LoRA adapter into base model...")
    model.save_pretrained_merged(
        str(merged_path),
        tokenizer,
        save_method="merged_16bit"
    )

    print("Converting to GGUF for Ollama...")
    model.save_pretrained_gguf(
        str(gguf_path.parent / "mtg_forge"),
        tokenizer,
        quantization_method="q4_k_m"   # Good quality/size tradeoff
    )

    print(f"\nGGUF model saved to: {gguf_path}")
    print("\nTo use with Ollama:")
    print(f"  ollama create mtg-forge -f {output_dir}/Modelfile")
    print("  Then update appsettings.json: Ollama:Model = mtg-forge")

    # Write a Modelfile for Ollama
    modelfile = f"""FROM {gguf_path}

PARAMETER temperature 0.7
PARAMETER num_ctx 4096
PARAMETER num_predict 2048

SYSTEM You are an expert Magic: The Gathering Commander deckbuilder with deep knowledge of card synergies, deck archetypes, and competitive strategy. Always respond in JSON format with 'reasoning' and 'sections' fields.
"""
    modelfile_path = Path(output_dir) / "Modelfile"
    modelfile_path.write_text(modelfile)
    print(f"Modelfile written to: {modelfile_path}")


# ─── Test Inference ───────────────────────────────────────────────────────────

def test_inference(model, tokenizer):
    """Quick sanity check — generate a deck recommendation."""
    FastLanguageModel.for_inference(model)

    prompt = ALPACA_TEMPLATE.format(
        "You are an expert MTG Commander deckbuilder. Respond in JSON with 'reasoning' and 'sections'.",
        "Build a Commander deck: Commander: Meren of Clan Nel Toth, Theme: sacrifice reanimation, Colors: B G, Budget: $50, Power Level: 6/10",
        ""  # Empty — model fills this in
    )

    inputs = tokenizer(prompt, return_tensors="pt").to(model.device)

    print("\nRunning test inference...")
    outputs = model.generate(
        **inputs,
        max_new_tokens=512,
        temperature=0.7,
        do_sample=True,
        pad_token_id=tokenizer.eos_token_id
    )

    response = tokenizer.decode(outputs[0][inputs["input_ids"].shape[1]:], skip_special_tokens=True)
    print("\n── Sample Output ──────────────────────────────")
    print(response[:1000])
    print("──────────────────────────────────────────────")


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Fine-tune LLM on MTG deck data")
    parser.add_argument("--data",       type=str, default="./data/training.jsonl")
    parser.add_argument("--output",     type=str, default="./models/mtg_lora")
    parser.add_argument("--epochs",     type=int, default=2)
    parser.add_argument("--test-only",  action="store_true", help="Skip training, test base model")
    parser.add_argument("--no-export",  action="store_true", help="Skip GGUF export after training")
    args = parser.parse_args()

    if args.test_only:
        model, tokenizer = load_model_and_tokenizer()
        test_inference(model, tokenizer)
        return

    if not Path(args.data).exists():
        print(f"Training data not found: {args.data}")
        print("Run: python export_training_data.py first")
        sys.exit(1)

    Path(args.output).mkdir(parents=True, exist_ok=True)

    model, tokenizer, adapter_path = train(args.data, args.output, args.epochs)
    test_inference(model, tokenizer)

    if not args.no_export:
        merge_and_export(model, tokenizer, adapter_path, args.output)

    print("\nFine-tuning complete!")
    print("Next steps:")
    print("  1. ollama create mtg-forge -f ./models/mtg_lora/Modelfile")
    print("  2. Update appsettings.json: Ollama:Model = mtg-forge")
    print("  3. Restart the .NET API and test deck generation")


if __name__ == "__main__":
    main()
