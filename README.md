dock# MTG Forge Local
> Full-stack Commander deck generator using a local LLM (Ollama), MongoDB, and Qdrant vector search.
> Optimized for Apple Silicon (M2) — no cloud API required after initial setup.

---

## Architecture

```
Browser (index.html)
    │
    ▼
.NET 8 API  ──►  Ollama (local LLM, Metal GPU)
    │               llama3.1:8b
    ├──►  MongoDB (card catalog + saved decks)
    └──►  Qdrant  (vector search — semantic card retrieval)

Python scripts
    └──►  Scryfall API → MongoDB + Qdrant (one-time ingestion)
```

---

## Prerequisites

| Tool | Install |
|---|---|
| Docker Desktop | https://www.docker.com/products/docker-desktop/ |
| .NET 8 SDK | https://dotnet.microsoft.com/download/dotnet/8 |
| Python 3.11+ | https://www.python.org/downloads/ |
| Ollama | https://ollama.com/download |

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

## Step 2 — Set Up Ollama (runs natively on your Mac, NOT in Docker)

```bash
# Install Ollama from https://ollama.com/download, then:

# Pull the LLM — ~4.7GB download
ollama pull llama3.1:8b

# Pull the embedding model — ~90MB
ollama pull all-minilm

# Verify Ollama is running
ollama list
curl http://localhost:11434/api/tags
```

> Ollama runs as a native macOS service and uses Metal GPU acceleration on M2.
> It does NOT run inside Docker — this is intentional for performance.

---

## Step 3 — Ingest Card Data

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

The script will:
1. Download Scryfall oracle card bulk data (~80MB)
2. Store all cards in MongoDB
3. Embed commander-legal cards into Qdrant using `all-MiniLM-L6-v2`

---

## Step 4 — Run the .NET API

```bash
cd MtgForgeLocal
dotnet run
```

Or with Docker:
```bash
docker compose up mtgforge
```

API available at: http://localhost:5000
Swagger UI: http://localhost:5000/swagger

---

## Step 5 — Open the UI

Navigate to: http://localhost:5000

---

## API Endpoints

### Generate a Deck
```bash
curl -X POST http://localhost:5000/api/decks/generate \
  -H "Content-Type: application/json" \
  -d '{
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
    "limit": 20
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

---

## Memory Usage (M2 16GB)

| Component | RAM |
|---|---|
| llama3.1:8b (4-bit) | ~5.5 GB |
| MongoDB | ~200 MB |
| Qdrant (~10k vectors) | ~100 MB |
| .NET API | ~80 MB |
| **Total** | **~6 GB** |

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
ollama pull llama3.1:8b
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

**Ingestion fails on embedding:**
```bash
# The all-MiniLM model downloads on first use (~90MB)
# If it fails, pre-download manually:
python -c "from sentence_transformers import SentenceTransformer; SentenceTransformer('all-MiniLM-L6-v2')"
```
