# MTG Forge AI
> Card API and deck generator using a local LLM (Ollama), MongoDB, and Qdrant vector search.
> Accepts requests from the main MtgDeckForge app — no frontend included.

---

## Architecture

```
MtgDeckForge App
    │
    ▼
.NET 10 API  ──►  Ollama (local LLM + embeddings)
    │                mistral / all-minilm
    ├──►  MongoDB (card catalog + saved decks)
    └──►  Qdrant  (vector search — semantic card retrieval)
```

Card ingestion can be done via:
- **Admin API endpoint**: `POST /api/admin/ingest` (recommended — no Python needed)
- **Python script**: `scripts/ingest_cards.py` (for local development)

---

## Prerequisites

| Tool | Install |
|---|---|
| Docker Desktop | https://www.docker.com/products/docker-desktop/ |
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10 |
| Ollama | https://ollama.com/download |
| Python 3.11+ (optional) | https://www.python.org/downloads/ |

---

## Step 1 — Start Infrastructure

```bash
# From project root
docker compose up -d

# Verify services
docker compose ps
```

Services started:
- MongoDB on port `27017`
- Qdrant on ports `6333` (HTTP) and `6334` (gRPC)

---

## Step 2 — Set Up Ollama (runs natively, NOT in Docker)

```bash
# Install Ollama from https://ollama.com/download, then:

# Pull the LLM (default: mistral)
ollama pull mistral:latest

# Pull the embedding model — ~90MB
ollama pull all-minilm

# Verify Ollama is running
ollama list
curl http://localhost:11434/api/tags
```

> Ollama runs as a native service and uses GPU acceleration (Metal on Apple Silicon).
> It does NOT run inside Docker — this is intentional for performance.

---

## Step 3 — Ingest Card Data

### Option A: Admin API Endpoint (recommended)

After starting the .NET API (Step 4), trigger ingestion via the admin endpoint:

```bash
# Full ingestion (~27k cards — takes 5-30 min depending on embedding speed)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{}'

# Quick test with 1000 cards
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{"limit": 1000}'

# MongoDB only (skip Qdrant embedding)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{"mongoOnly": true}'

# Qdrant only (skip MongoDB)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{"qdrantOnly": true}'
```

### Option B: Python Script (local development)

```bash
cd scripts

# Install Python dependencies
pip install -r requirements.txt

# Run full ingestion (~27k cards, takes 5-15 min on first run)
python ingest_cards.py

# Quick test with 1000 cards
python ingest_cards.py --limit 1000

# Re-use cached download (skip Scryfall fetch)
python ingest_cards.py --skip-download
```

The ingestion pipeline will:
1. Download Scryfall oracle card bulk data (~80MB)
2. Store all cards in MongoDB
3. Embed format-legal cards into Qdrant using the configured embedding model

---

## Step 4 — Run the .NET API

```bash
cd MtgForgeAi
dotnet run
```

Or with Docker:
```bash
docker compose up mtgforge
```

> **Note:** When running via Docker, the API is exposed on port **5001** (mapped from internal port 8080):
> http://localhost:5001 / Swagger UI: http://localhost:5001/swagger

---

## Embedding Model Alignment

**CRITICAL**: The embedding model used for ingestion and the model used for search queries
must produce the same vector dimensions. A mismatch will cause search failures.

| Component | Model | Dimensions |
|---|---|---|
| Python ingestion (`ingest_cards.py`) | `all-MiniLM-L6-v2` | 384 |
| Admin API ingestion (`/api/admin/ingest`) | Ollama `all-minilm` | 384 |
| .NET search queries (`OllamaEmbedService`) | Ollama `all-minilm` | 384 |

If you change the embedding model in `appsettings.json` (e.g. to `nomic-embed-text` at 768-dim),
you **must**:
1. Delete the Qdrant collection: `curl -X DELETE http://localhost:6333/collections/mtg_cards`
2. Re-run ingestion using the same model to recreate the collection with correct dimensions

---

## API Endpoints

### Generate a Deck
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

### Search Cards
```bash
curl -X POST http://localhost:5000/api/cards/search \
  -H "Content-Type: application/json" \
  -d '{
    "query": "sacrifice a creature to draw cards",
    "colors": ["B", "G"],
    "maxPrice": 5.00,
    "limit": 20,
    "format": "commander"
  }'
```

### List Saved Decks
```bash
curl http://localhost:5000/api/decks
```

### Health Check
```bash
curl http://localhost:5000/api/health
```

### Ingest Cards (Admin)
```bash
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{"limit": 1000}'
```

---

## Memory Usage (16GB RAM)

| Component | RAM |
|---|---|
| mistral (4-bit) | ~4.5 GB |
| MongoDB | ~200 MB |
| Qdrant (~10k vectors) | ~100 MB |
| .NET API | ~80 MB |
| **Total** | **~5 GB** |

You'll have ~10GB free for your OS and other apps — should be comfortable.

---

## Fine-Tuning (Optional — Advanced)

Once you have the basic RAG pipeline working and have generated several decks,
you can improve quality by fine-tuning on your best outputs:

```bash
# Install Unsloth for efficient LoRA fine-tuning
pip install unsloth

# Export your saved decks as training data
python scripts/export_training_data.py

# Fine-tune (requires ~8GB RAM for 4-bit quantized Llama 3.1 8B)
python scripts/finetune.py
```

See `scripts/finetune.py` for the full pipeline.

---

## Troubleshooting

**Ollama not reachable:**
```bash
# Check if Ollama is running
ollama list
# If not, start it:
ollama serve
```

**Model not found:**
```bash
ollama pull mistral:latest
ollama pull all-minilm
```

**Qdrant connection refused:**
```bash
docker compose up -d qdrant
curl http://localhost:6333/healthz
```

**MongoDB auth error:**
```bash
# Check connection string in appsettings.json matches docker-compose.yml credentials
```

**Embedding dimension mismatch (search returns no results):**
```bash
# If you changed the embedding model, delete the Qdrant collection and re-ingest:
curl -X DELETE http://localhost:6333/collections/mtg_cards

# Then re-run ingestion via API:
curl -X POST http://localhost:5000/api/admin/ingest -H "Content-Type: application/json" -d '{}'

# Or via Python script:
cd scripts && python ingest_cards.py
```

**Ingestion fails on embedding:**
```bash
# The all-MiniLM model downloads on first use (~90MB)
# If it fails, pre-download manually:
python -c "from sentence_transformers import SentenceTransformer; SentenceTransformer('all-MiniLM-L6-v2')"
```
