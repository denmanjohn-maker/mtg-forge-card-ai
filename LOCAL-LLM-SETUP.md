# Local LLM Setup — MTG Deck Forge

## Summary

Your production app (`bensmagicforge.app`) uses Claude Sonnet to generate MTG decks, but Claude ignores price constraints because it estimates card prices from training memory. The fix is structural: route generation through a local RAG pipeline (`mtg-forge-local`) that pre-filters cards in Qdrant by price **before** the LLM ever sees them.

### What's included in mtg-forge-local

- **Multi-format support**: `commander`, `standard`, `modern`, `legacy`, `pioneer`, `pauper`, `vintage` — format-aware prompts, Qdrant legality filters, and deck size rules
- **Admin ingestion endpoint**: `POST /api/admin/ingest` — ingest cards from Scryfall without needing Python
- **Color identity filtering**: Correctly excludes cards outside the requested color identity using `MustNot` filters
- **Embedding model alignment**: `appsettings.json` uses `all-minilm` (384-dim) matching the Python ingestion script
- **API-only mode**: No frontend — accepts requests from the main MtgDeckForge app

---

## Steps to Set Up

### 1. Pull Ollama Models

Make sure the LLM and embedding models are downloaded:
```bash
# LLM for deck generation (default is mistral)
ollama pull mistral:latest

# Embedding model for semantic card search (~90MB)
ollama pull all-minilm
```

You can change the LLM model in `MtgForgeLocal/appsettings.json` under `"Ollama:Model"` (e.g. `llama3.1:8b`, `phi3`).

---

### 2. Start Infrastructure

```bash
# From project root
docker compose up -d

# Verify services
docker compose ps
```

This starts MongoDB (port 27017) and Qdrant (ports 6333/6334).

> **Note:** Ollama runs natively on macOS (Metal GPU acceleration). It does NOT run inside Docker.
> Docker containers reach Ollama via `host.docker.internal:11434` — already configured in `docker-compose.yml`.

---

### 3. Ingest Card Data

#### Option A: Admin API Endpoint (recommended — no Python needed)

Start the .NET API first (Step 4), then trigger ingestion:

```bash
# Full ingestion (~27k cards — takes 5-30 min depending on embedding speed)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{}'

# Quick test with 1000 cards
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{"limit": 1000}'
```

#### Option B: Python Script

```bash
cd scripts
pip install -r requirements.txt   # first time only
python ingest_cards.py
```

Ingestion stores `legality_standard`, `legality_modern`, `legality_commander`, etc. for all 7 formats. **This is required for non-Commander formats to work.**

---

### 4. Start the .NET API

```bash
cd MtgForgeLocal
dotnet run
```

Or with Docker:
```bash
docker compose up mtgforge
```

Verify it's running:
```bash
curl http://localhost:5000/api/health
```

API: http://localhost:5000
Swagger UI: http://localhost:5000/swagger

---

### 5. Connect MtgDeckForge (Production App)

In `MtgDeckForge.Api/appsettings.json`, switch the provider:

```json
"LlmProvider": "Local"
```

The `LocalLlm` section should already be present:
```json
"LocalLlm": {
  "BaseUrl": "http://localhost:5000",
  "OllamaUrl": "http://localhost:11434",
  "Model": "mistral"
}
```

To switch back to Claude at any time, set `"LlmProvider": "Claude"` and restart.

---

### 6. Test End-to-End

Generate a deck via the MtgDeckForge app:

1. Select format: **Standard** (or any non-Commander format to verify multi-format support)
2. Set budget: **$50**
3. Generate — all cards in the result should have individual prices that sum to ≤ $50

---

## Architecture Overview

```
MtgDeckForge.Api
  └─ DecksController
       └─ IDeckGenerationService
            ├─ ClaudeService        (LlmProvider = "Claude")
            └─ LocalLlmService      (LlmProvider = "Local")
                  ├─ GenerateDeckAsync  → mtg-forge-local :5000/api/decks/generate
                  ├─ AnalyzeDeckAsync   → Ollama :11434 directly
                  └─ SuggestBudgetReplacementsAsync → returns [] (Qdrant pre-filters by price)

mtg-forge-local :5000
  ├─ POST /api/decks/generate      → DeckGenerationService → Qdrant + Ollama
  ├─ POST /api/cards/search        → CardSearchService → Qdrant semantic search
  ├─ POST /api/admin/ingest        → CardIngestionService → Scryfall → MongoDB + Qdrant
  ├─ GET  /api/decks               → MongoService (list saved decks)
  ├─ GET  /api/health              → Health check (Ollama, MongoDB, Qdrant)
  └─ MongoDB (card catalog + saved decks)
```

## Notes

| Topic | Details |
|---|---|
| Ollama runs natively on macOS | Docker containers reach Ollama via `host.docker.internal:11434` — already configured |
| `SuggestBudgetReplacementsAsync` returns `[]` in Local mode | By design — Qdrant pre-filtering makes it unnecessary |
| Embedding model alignment | Both Python ingestion and .NET API use 384-dim models (`all-MiniLM-L6-v2` / `all-minilm`). See README.md for details on changing models. |
