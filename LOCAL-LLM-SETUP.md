# Railway Architecture — MTG Deck Forge

## Summary

`mtg-forge-ai` is the deck generation backend for the MtgDeckForge frontend app. Both services are deployed on Railway.app. The backend runs a RAG pipeline that pre-filters cards in Qdrant by price **before** the LLM ever sees them, ensuring budget constraints are respected.

> **Why Together.ai?** Ollama was initially run in Railway as the LLM for deck generation, but without GPU access the response times exceeded 5 minutes per deck. Together.ai provides GPU-accelerated inference with sub-10-second generation times.

### What's included in mtg-forge-ai

- **Multi-format support**: `commander`, `standard`, `modern`, `legacy`, `pioneer`, `pauper`, `vintage` — format-aware prompts, Qdrant legality filters, and deck size rules
- **Admin ingestion endpoint**: `POST /api/admin/ingest` — ingest cards from Scryfall without needing Python
- **Color identity filtering**: Correctly excludes cards outside the requested color identity using `MustNot` filters
- **Embedding model alignment**: `appsettings.json` uses `all-minilm` (384-dim) matching the Python ingestion script
- **API-only mode**: No frontend — accepts requests from the main MtgDeckForge app

---

## Railway Environments

| Environment | Branch | Purpose |
|---|---|---|
| **Production** | `main` | Live user-facing deployment |
| **Staging** | `staging` | Pre-release testing — mirrors the other repo's staging branch |

Both environments run the same service topology: .NET API + MongoDB + Qdrant + Ollama (embeddings only) + Together.ai (LLM chat).

---

## Railway Service Topology

Each environment deploys the following Railway services:

| Service | Purpose | Internal URL |
|---|---|---|
| `.NET API` (this repo) | Deck generation, card search, ingestion | `mtgforgeai.railway.internal` |
| `MongoDB` | Card catalog + saved decks | `mongodb.railway.internal:27017` |
| `Qdrant` | Vector search | `qdrant.railway.internal:6334` |
| `Ollama` | Embedding model only (`all-minilm`) | `ollama.railway.internal:11434` |
| Together.ai | LLM chat completions (hosted, no Railway service) | `https://api.together.xyz` |

---

## Steps to Set Up (Local Development)

### 1. Pull Ollama Models

For local development, make sure the embedding model is downloaded (the LLM chat uses Together.ai, so no large local LLM is needed):

```bash
# Embedding model for semantic card search (~23 MB)
ollama pull all-minilm

# Optional: chat model if you want to test with local Ollama instead of Together.ai
ollama pull mistral:latest
```

---

### 2. Start Infrastructure

```bash
# From project root
docker compose up -d

# Verify services
docker compose ps
```

This starts MongoDB (port 27017) and Qdrant (ports 6333/6334).

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
cd MtgForgeAi
dotnet run
```

Or with Docker:
```bash
docker compose up mtgforge
```

Verify it's running:
```bash
# Local (dotnet run) — port 5000
curl http://localhost:5000/api/health

# Docker — port 5001 (mapped from internal 8080)
curl http://localhost:5001/api/health
```

API (local): http://localhost:5000 / Swagger: http://localhost:5000/swagger
API (Docker): http://localhost:5001 / Swagger: http://localhost:5001/swagger

---

### 5. Connect MtgDeckForge

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

> If mtg-forge-ai is running via Docker (`docker compose up mtgforge`), use port `5001` instead: `"BaseUrl": "http://localhost:5001"`.

---

## Architecture Overview

```
MtgDeckForge.Api (Railway)
  └─ DecksController
       └─ IDeckGenerationService
            └─ LocalLlmService      (LlmProvider = "Local")
                  └─ GenerateDeckAsync  → mtg-forge-ai /api/decks/generate

mtg-forge-ai (Railway)
  ├─ POST /api/decks/generate      → DeckGenerationService → Qdrant + Together.ai
  ├─ POST /api/cards/search        → CardSearchService → Qdrant semantic search
  ├─ POST /api/admin/ingest        → CardIngestionService → Scryfall → MongoDB + Qdrant
  ├─ GET  /api/decks               → MongoService (list saved decks)
  ├─ GET  /api/health              → Health check (LLM, MongoDB, Qdrant)
  └─ MongoDB (card catalog + saved decks)
```

## Notes

| Topic | Details |
|---|---|
| Ollama in Railway (embeddings only) | Ollama runs as a Railway service but is used **only** for the `all-minilm` embedding model. LLM chat goes to Together.ai. |
| `SuggestBudgetReplacementsAsync` returns `[]` in Local mode | By design — Qdrant pre-filtering makes it unnecessary |
| Embedding model alignment | Both Python ingestion and .NET API use 384-dim models (`all-MiniLM-L6-v2` / `all-minilm`). See README.md for details on changing models. |
