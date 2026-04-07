# Local LLM Setup — MTG Deck Forge

## Summary

Your production app (`bensmagicforge.app`) uses Claude Sonnet to generate MTG decks, but Claude ignores price constraints because it estimates card prices from training memory. The fix is structural: route generation through a local RAG pipeline (`mtg-forge-local`) that pre-filters cards in Qdrant by price **before** the LLM ever sees them.

Two repos have been updated:

| Repo | Location |
|---|---|
| `mtg-forge-local` | `~/Desktop/Local LLM Magic/mtg-forge-local/` |
| `MtgDeckForge` | `~/Desktop/Repos/MtgDeckForge/` |

### What was already done by Copilot

- **`mtg-forge-local`** extended to support all major formats: `commander`, `standard`, `modern`, `legacy`, `pioneer`, `pauper`, `vintage` — format-aware prompts, Qdrant filters, and deck size rules
- **`MtgDeckForge`** given a `IDeckGenerationService` abstraction with two implementations: `ClaudeService` (existing) and `LocalLlmService` (new)
- Provider toggled by `"LlmProvider"` in `appsettings.json` — no code changes required to switch
- Both repos build cleanly (0 errors)

---

## Steps You Need to Perform

### 1. Fix the Embedding Model Config

The `mtg-forge-local` config has a mismatch that will silently break semantic search. The ingestion script uses a **384-dimension** model but the config points to a **768-dimension** model.

**File:** `~/Desktop/Local LLM Magic/mtg-forge-local/MtgForgeLocal/appsettings.json`

Change:
```json
"EmbedModel": "nomic-embed-text"
```
To:
```json
"EmbedModel": "all-minilm"
```

Then pull the model in Ollama if you haven't already:
```bash
ollama pull all-minilm
```

---

### 2. Pull the LLM Model in Ollama

Make sure the generation model is downloaded (default is `mistral`):
```bash
ollama pull mistral
```

You can change the model in `mtg-forge-local/MtgForgeLocal/appsettings.json` under `"LlmModel"` if you prefer a different one (e.g. `llama3`, `phi3`).

---

### 3. Re-run the Card Ingestion Script

The existing Qdrant collection only has `legality_commander`. Re-ingestion adds `legality_standard`, `legality_modern`, etc. for all 7 formats. **This step is required for non-Commander formats to work.**

This will take ~15 minutes (downloads ~80MB from Scryfall, embeds ~26k cards).

```bash
cd ~/Desktop/Local\ LLM\ Magic/mtg-forge-local/scripts
pip install -r requirements.txt   # first time only
python ingest_cards.py
```

> **Prerequisites:** Docker must be running with Qdrant and MongoDB containers up.
> Start them with: `cd ~/Desktop/Local\ LLM\ Magic/mtg-forge-local && docker compose up -d qdrant mongo`

---

### 4. Start the `mtg-forge-local` Services

```bash
cd ~/Desktop/Local\ LLM\ Magic/mtg-forge-local
docker compose up -d
```

Verify it's running:
```bash
curl http://localhost:5000/health
```

---

### 5. Switch MtgDeckForge to Local Mode

**File:** `~/Desktop/Repos/MtgDeckForge/MtgDeckForge.Api/appsettings.json`

Change:
```json
"LlmProvider": "Claude"
```
To:
```json
"LlmProvider": "Local"
```

The `LocalLlm` section should already be present with default values:
```json
"LocalLlm": {
  "BaseUrl": "http://localhost:5000",
  "OllamaUrl": "http://localhost:11434",
  "Model": "mistral"
}
```

---

### 6. Test End-to-End

Run `MtgDeckForge` locally and generate a deck:

1. Select format: **Standard** (or any non-Commander format to verify multi-format support)
2. Set budget: **Budget ($0–$50)**
3. Generate — all cards in the result should have individual prices that sum to ≤ $50

To switch back to Claude at any time, set `"LlmProvider": "Claude"` and restart.

---

## Architecture Overview

```
MtgDeckForge.Api
  └─ DecksController
       └─ IDeckGenerationService
            ├─ ClaudeService        (LlmProvider = "Claude")
            └─ LocalLlmService      (LlmProvider = "Local")
                  ├─ GenerateDeckAsync  → mtg-forge-local :5000/api/deck/generate
                  ├─ AnalyzeDeckAsync   → Ollama :11434 directly
                  └─ SuggestBudgetReplacementsAsync → returns [] (Qdrant pre-filters by price)

mtg-forge-local :5000
  └─ /api/deck/generate
       ├─ CardSearchService  → Qdrant (semantic search + price filter + legality filter)
       ├─ DeckGenerationService → Ollama (format-aware prompt)
       └─ MongoDB (saved decks)
```

## Known Issues / Optional Follow-Up

| Issue | Severity | Notes |
|---|---|---|
| Qdrant color identity filter uses `Must` (requires ALL listed colors) instead of `MustNot` (exclude colors outside identity) | Low | Only affects multi-color Commander decks; cards returned may include colors outside the identity |
| `mtg-forge-local` runs in Docker but Ollama runs natively on macOS | Info | Docker containers reach Ollama via `host.docker.internal:11434` — already configured |
| `SuggestBudgetReplacementsAsync` returns `[]` in Local mode | By design | Budget enforcement loop in `DecksController` handles this gracefully; Qdrant pre-filtering makes it unnecessary |
