# MTG Forge AI

> API-only RAG system for MTG deck generation. Uses Together.ai for LLM chat completions and Ollama for embeddings only. MongoDB for card data + saved decks, Qdrant for vector search. Deployed on Railway.app — accepts requests from the MtgDeckForge frontend app — no UI included.

---

## Architecture

```
MtgDeckForge App (frontend — separate repo)
    │
    ▼
.NET 10 API (this repo)
    │
    ├──►  ILlmService (provider-agnostic chat completions)
    │        ├── OllamaLlmService   (local dev — /api/chat)
    │        └── OpenAiLlmService   (production — /v1/chat/completions)
    │
    ├──►  OllamaEmbedService (always Ollama — all-minilm, 384-dim)
    ├──►  MongoDB (card catalog + saved decks)
    └──►  Qdrant  (vector search — semantic card retrieval)
```

### How deck generation works

1. `DecksController.Generate` validates format and commander, calls `DeckGenerationService`
2. `CardSearchService.GetDeckCandidatesAsync` queries Qdrant for ~80 candidate cards (semantic search filtered by format legality, color identity, budget)
3. `BuildUserPrompt` groups candidates by type and builds a prompt
4. `ILlmService.ChatAsync` sends the prompt to whichever LLM provider is configured
5. `ParseDeckResponse` extracts JSON from the LLM output, enriches cards with data from the candidate pool, computes cost, and saves to MongoDB

### Ingestion paths

- **Admin API**: `POST /api/admin/ingest` — downloads Scryfall data, upserts to MongoDB, embeds into Qdrant
- **Python script**: `scripts/ingest_cards.py` — same pipeline for local dev

### Training data pipeline

- `scripts/scrape_decklists.py` — scrapes tournament decklists from MTGTop8, enriches via Scryfall, outputs JSONL
- `scripts/export_training_data.py` — exports saved decks from MongoDB as JSONL training pairs
- `scripts/finetune.py` — LoRA fine-tuning on Llama 3.1 8B with Unsloth

---

## Prerequisites

| Tool | Purpose | Install |
|---|---|---|
| Docker Desktop | MongoDB + Qdrant infrastructure | https://www.docker.com/products/docker-desktop/ |
| .NET 10 SDK | Build and run the API | https://dotnet.microsoft.com/download/dotnet/10 |
| Ollama | Local LLM + embeddings | https://ollama.com/download |
| Python 3.11+ | Scripts (ingestion, scraping, fine-tuning) — optional | https://www.python.org/downloads/ |

---

## Quick Start

### 1. Start infrastructure

```bash
docker compose up -d     # MongoDB (27017) + Qdrant (6333/6334)
docker compose ps        # verify both are running
```

### 2. Configure LLM provider

The chat LLM is provider-agnostic. Choose one:

#### Option A: Ollama (local dev — default, no config needed)

```bash
ollama pull mistral:latest   # chat model
ollama pull all-minilm       # embedding model (always needed, even with Option B)
ollama list                  # verify
```

#### Option B: Together.ai / OpenAI-compatible (production — current Railway setup)

Set env vars or update `appsettings.json`:

```bash
LLM__Provider=openai
LLM__BaseUrl=https://api.together.xyz
LLM__Model=meta-llama/Llama-3.3-70B-Instruct-Turbo
LLM__ApiKey=your-api-key-here
```

Works with any OpenAI-compatible API (Together.ai, OpenRouter, Fireworks, etc.).

> **Important:** Embeddings always use Ollama (`all-minilm`), even when chat is hosted externally. In production (Railway), Ollama runs as a Railway service for embeddings — the model is tiny (~23MB) and fast on CPU.

### 3. Run the API

```bash
dotnet run --project MtgForgeAi   # http://localhost:5000
```

Or via Docker:
```bash
docker compose up mtgforge        # http://localhost:5001 (mapped from 8080)
```

Swagger UI: `http://localhost:5000/swagger` (or `:5001` for Docker)

### 4. Ingest card data

Start the API first, then:

```bash
# Full ingestion (~37k cards, 5-30 min depending on embedding speed)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" -d '{}'

# Quick test with 1000 cards
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" -d '{"limit": 1000}'
```

Or via Python:
```bash
cd scripts && pip install -r requirements.txt
python ingest_cards.py             # full ingestion
python ingest_cards.py --limit 1000  # quick test
```

### 5. Generate a deck

```bash
curl -X POST http://localhost:5000/api/decks/generate \
  -H "Content-Type: application/json" \
  -d '{
    "format": "commander",
    "commander": "Meren of Clan Nel Toth",
    "theme": "sacrifice and reanimation",
    "colorIdentity": ["B", "G"],
    "budget": 50.00,
    "powerLevel": 6,
    "extraContext": "focus on creature-based synergies"
  }'
```

---

## Configuration Reference

All config lives in `MtgForgeAi/appsettings.json`. Override via env vars using `__` for nesting (e.g., `LLM__Provider`).

```jsonc
{
  "MongoDB": {
    "ConnectionString": "mongodb://admin:password@localhost:27017",
    "DatabaseName": "mtgforge"
  },
  "Qdrant": {
    "Host": "localhost",      // gRPC host
    "Port": 6334              // gRPC port (NOT the HTTP 6333 port)
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "Model": "mistral:latest",   // used when LLM.Provider = "ollama"
    "EmbedModel": "all-minilm"   // always used for embeddings
  },
  "LLM": {
    "Provider": "ollama",        // "ollama" or "openai"
    "BaseUrl": "https://api.together.xyz",  // only used when Provider = "openai"
    "Model": "meta-llama/Llama-3.3-70B-Instruct-Turbo",
    "ApiKey": ""                 // required when Provider = "openai"
  }
}
```

### Railway deployment env vars

The production and staging environments on Railway.app use Together.ai for LLM chat (Ollama was tried for LLM chat in Railway but was too slow without a GPU — deck generation took over 5 minutes). Ollama remains in Railway solely for the embedding model, which runs fine on CPU.

**Production** (main branch) and **Staging** (staging branch) both use:

```
MongoDB__ConnectionString=mongodb://user:pass@mongodb.railway.internal:27017
MongoDB__DatabaseName=mtgforge
Qdrant__Host=qdrant.railway.internal
Qdrant__Port=6334
Ollama__BaseUrl=http://ollama.railway.internal:11434
Ollama__EmbedModel=all-minilm
LLM__Provider=openai
LLM__BaseUrl=https://api.together.xyz
LLM__Model=meta-llama/Llama-3.3-70B-Instruct-Turbo
LLM__ApiKey=your-key
```

---

## API Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/decks/generate` | Generate a new deck (format, commander, theme, colors, budget, powerLevel) |
| `GET` | `/api/decks` | List all saved decks |
| `GET` | `/api/decks/{id}` | Get a specific saved deck |
| `DELETE` | `/api/decks/{id}` | Delete a saved deck |
| `POST` | `/api/cards/search` | Semantic card search (query, colors, maxPrice, limit, format) |
| `POST` | `/api/admin/ingest` | Ingest cards from Scryfall (mongoOnly, qdrantOnly, limit) |
| `GET` | `/api/health` | Health check (LLM, MongoDB, Qdrant) |

### Supported formats

`commander`, `standard`, `modern`, `legacy`, `pioneer`, `pauper`, `vintage`

---

## Embedding Model Alignment

**CRITICAL**: Ingestion and search must use the same embedding dimensions. A mismatch causes silent search failures.

| Component | Model | Dimensions |
|---|---|---|
| Python ingestion (`ingest_cards.py`) | `all-MiniLM-L6-v2` | 384 |
| Admin API ingestion (`/api/admin/ingest`) | Ollama `all-minilm` | 384 |
| .NET search queries (`OllamaEmbedService`) | Ollama `all-minilm` | 384 |

If you change the embedding model, you **must** re-ingest:
```bash
curl -X DELETE http://localhost:6333/collections/mtg_cards
curl -X POST http://localhost:5000/api/admin/ingest -H "Content-Type: application/json" -d '{}'
```

---

## Project Structure

```
mtg-forge-card-ai/
├── MtgForgeAi/                    # .NET 10 API project
│   ├── Controllers/Controllers.cs # DecksController, CardsController, HealthController, AdminController
│   ├── Models/Models.cs           # Request/response records, MongoDB document classes
│   ├── Services/
│   │   ├── ILlmService.cs         # LLM provider interface (ChatAsync, StreamAsync, IsHealthyAsync)
│   │   ├── OllamaService.cs       # OllamaLlmService — local Ollama implementation
│   │   ├── OpenAiLlmService.cs    # Together.ai / OpenAI-compatible implementation
│   │   ├── OllamaEmbedService.cs  # Embedding service (always Ollama, all-minilm 384-dim)
│   │   ├── DeckGenerationService.cs # Orchestrator: candidates → prompt → LLM → parse → save
│   │   ├── CardSearchService.cs   # Qdrant semantic search with format/color/budget filters
│   │   ├── CardIngestionService.cs # Scryfall → MongoDB + Qdrant pipeline
│   │   └── MongoService.cs        # MongoDB CRUD (cards, saved decks, indexes)
│   ├── Program.cs                 # DI wiring, config-driven LLM provider selection
│   ├── Dockerfile                 # Multi-stage .NET 10 build
│   ├── appsettings.json           # Default config (MongoDB, Qdrant, Ollama, LLM)
│   └── MtgForgeAi.csproj
├── scripts/
│   ├── ingest_cards.py            # Python ingestion: Scryfall → MongoDB + Qdrant
│   ├── scrape_decklists.py        # Scrape MTGTop8 tournament decks → JSONL training data
│   ├── export_training_data.py    # Export saved decks from MongoDB → JSONL training data
│   ├── finetune.py                # LoRA fine-tuning with Unsloth (Llama 3.1 8B)
│   └── requirements.txt           # Python dependencies
├── docker-compose.yml             # MongoDB + Qdrant + (optional) API container
├── mtg-forge-ai.sln               # .NET solution file
├── README.md                      # This file
└── LOCAL-LLM-SETUP.md             # Railway architecture and MtgDeckForge integration guide
```

---

## Fine-Tuning Pipeline

The repo includes a full pipeline to fine-tune a model specifically for MTG deck generation.

### Step 1: Gather training data

```bash
cd scripts && pip install -r requirements.txt

# Scrape real tournament decklists from MTGTop8
python scrape_decklists.py --formats commander modern --limit 50

# Or export your own saved decks from MongoDB
python export_training_data.py --format chatml --out ./data/my_decks.jsonl
```

Both scripts output the same JSONL format compatible with the fine-tuning script.

### Step 2: Fine-tune

```bash
pip install unsloth
python finetune.py    # LoRA on Llama 3.1 8B, outputs GGUF for Ollama
```

### Step 3: Use the fine-tuned model

After fine-tuning exports a GGUF file, create an Ollama model from it:
```bash
ollama create mtg-deck-builder -f Modelfile
```

Then update `appsettings.json`:
```json
{ "Ollama": { "Model": "mtg-deck-builder" } }
```

Or to host the fine-tuned model on Together.ai:
1. Upload the model to Together.ai
2. Set `LLM__Provider=openai`, `LLM__Model=your-finetuned-model-id`

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Ollama not reachable | Verify the Ollama service is running (Railway service health, or `ollama serve` for local dev) |
| Embedding model not found | `ollama pull all-minilm` on the host running Ollama |
| Qdrant connection refused | Check Qdrant service is running: `curl http://localhost:6333/healthz` |
| MongoDB auth error | Check `MongoDB__ConnectionString` matches configured credentials |
| Search returns no results | Embedding model mismatch — delete collection and re-ingest (see above) |
| Scryfall ingestion 400 error | Scryfall requires `User-Agent` + `Accept` headers (already configured) |
| Deck generation timeout | Ensure `LLM__Provider=openai` with Together.ai — Ollama in Railway has no GPU and will be slow |
| Qdrant collection not found | Collection name is `mtg_cards` (not `cards`). Run ingestion first. |
