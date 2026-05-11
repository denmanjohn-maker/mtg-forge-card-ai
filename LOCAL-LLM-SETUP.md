# Railway Architecture — MTG Deck Forge

## Summary

`mtg-forge-ai` is the deck generation backend for the MtgDeckForge frontend app. Both services are deployed on Railway.app. The backend runs a RAG pipeline that pre-filters cards in Qdrant by price **before** the LLM ever sees them, ensuring budget constraints are respected.

> **Why DeepInfra?** Ollama was initially run in Railway as the LLM for deck generation, but without GPU access the response times exceeded 5 minutes per deck. DeepInfra provides GPU-accelerated inference with sub-10-second generation times and the widest open-source model catalog (~$0.23/1M tokens).

### What's included in mtg-forge-ai

- **Multi-format support**: `commander`, `standard`, `modern`, `legacy`, `pioneer`, `pauper`, `vintage` — format-aware prompts, Qdrant legality filters, and deck size rules
- **Admin ingestion endpoint**: `POST /api/admin/ingest` — ingest cards from Scryfall without needing Python
- **Color identity filtering**: Correctly excludes cards outside the requested color identity using `MustNot` filters
- **Embedding model alignment**: `appsettings.json` uses `BAAI/bge-m3` (1024-dim) via DeepInfra — if switching to local Ollama, use `all-minilm` (384-dim) and re-ingest Qdrant data
- **API-only mode**: No frontend — accepts requests from the main MtgDeckForge app

---

## Railway Environments

| Environment | Branch | Purpose |
|---|---|---|
| **Production** | `main` | Live user-facing deployment |
| **Staging** | `staging` | Pre-release testing — mirrors the other repo's staging branch |

Both environments run the same service topology: .NET API + MongoDB + Qdrant + DeepInfra (LLM chat + embeddings).

---

## Railway Service Topology

Each environment deploys the following Railway services:

| Service | Purpose | Internal URL |
|---|---|---|
| `.NET API` (this repo) | Deck generation, card search, ingestion | `mtgforgeai.railway.internal` |
| `MongoDB` | Card catalog + saved decks | `mongodb.railway.internal:27017` |
| `Qdrant` | Vector search | `qdrant.railway.internal:6334` |
| DeepInfra | LLM chat completions + embeddings (hosted, no Railway service) | `https://api.deepinfra.com/v1/openai` |

---

## Steps to Set Up (Local Development)

### 1. Pull Ollama Models (Local Dev Only)

For local development, if you prefer `LLM__Provider=ollama`, pull the models. For Railway deployments using DeepInfra, skip this step:

```bash
# Embedding model for semantic card search (~23 MB)
ollama pull all-minilm

# Optional: chat model if you want to test with local Ollama instead of DeepInfra
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
  ├─ POST /api/decks/generate      → DeckGenerationService → Qdrant + DeepInfra
  ├─ POST /api/cards/search        → CardSearchService → Qdrant semantic search
  ├─ POST /api/admin/ingest        → CardIngestionService → Scryfall → MongoDB + Qdrant
  ├─ GET  /api/admin/ingest-status → IngestionStatusService (also at GET /api/admin/ingest)
  ├─ GET  /api/decks               → MongoService (list saved decks)
  ├─ GET  /api/health              → Health check (LLM, MongoDB, Qdrant)
  └─ MongoDB (card catalog + saved decks)
```

## Notes

| Topic | Details |
|---|---|
| Ollama in Railway | Not used in Railway. Ollama is only needed locally when `LLM__Provider=ollama`. In Railway, DeepInfra handles both LLM chat and embeddings. |
| `SuggestBudgetReplacementsAsync` returns `[]` in Local mode | By design — Qdrant pre-filtering makes it unnecessary |
| Embedding model alignment | Production uses `BAAI/bge-m3` (1024-dim) via DeepInfra. Local Ollama uses `all-minilm` (384-dim). Switching embed providers requires re-ingesting Qdrant data. |
