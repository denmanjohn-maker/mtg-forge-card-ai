# Together.ai Setup Guide — MTG Forge AI

This guide walks you through configuring MTG Forge AI to use Together.ai as its LLM provider instead of a local Ollama model. Together.ai gives you access to large hosted models (70B+) with GPU-accelerated inference at very low cost — ideal for this proof of concept.

> **Note on embeddings:** When `LLM:Provider` is set to `openai`, both deck generation **and** card embeddings run through Together.ai. Ollama is no longer required on Railway. For local dev, you can still use Ollama by setting `LLM__Provider=ollama`, which will use the local `all-minilm` model for embeddings.
>
> **Important:** If you are migrating from the Ollama embed provider to Together.ai's embed provider, you must **delete the Qdrant `mtg_cards` collection and re-run ingestion**. The embedding models produce different vector dimensions (384-dim `all-minilm` vs 768-dim `BAAI/bge-base-en-v1.5`), so the stored vectors are incompatible.

---

## Prerequisites

| Tool | Purpose | Install |
|---|---|---|
| Docker Desktop | MongoDB + Qdrant infrastructure (local dev) | https://www.docker.com/products/docker-desktop/ |
| .NET 10 SDK | Build and run the API (local dev) | https://dotnet.microsoft.com/download/dotnet/10 |
| Ollama | Embedding model (local dev only — not needed in Railway) | https://ollama.com/download |
| Together.ai account | Hosted LLM inference and embeddings | https://api.together.ai |

---

## Step 1 — Create a Together.ai Account and Get an API Key

1. Go to [https://api.together.ai](https://api.together.ai) and sign up for a free account.
2. After signing in, navigate to **Settings → API Keys**.
3. Click **Create API Key**, give it a name (e.g. `mtg-forge-dev`), and click **Create**.
4. Copy the key immediately — it is only shown once.

> Together.ai gives new accounts free credits to get started. For this project, deck generation costs roughly **$0.002–$0.003 per request** using the default `meta-llama/Llama-3.3-70B-Instruct-Turbo` model.

---

## Step 2 — Choose a Model (Optional)

The default model is already configured in `appsettings.json`:

```
meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8
```

This model gives GPT-4o class JSON-following behavior at 3× lower cost than the previous default. If you want to try a different model, browse the [Together.ai model list](https://api.together.ai/models) and filter by **Chat** capability. Some good alternatives:

| Model ID | Input $/1M | Notes |
|---|---|---|
| `meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8` | $0.27 | ✅ Default — best quality/cost; 1M context; JSON mode |
| `meta-llama/Llama-3.3-70B-Instruct-Turbo` | $0.88 | Previous default — still solid, now superseded by Maverick |
| `Qwen/Qwen3.5-9B` | $0.10 | Budget option for testing |
| `deepseek-ai/DeepSeek-V4-Pro` | $2.10 | Only needed for very long context workloads |

Copy the full model ID from the Together.ai UI — you will need it in Step 3.

---

## Step 3 — Configure the Application

### Option A: Edit `appsettings.json` (local dev)

Open `MtgForgeAi/appsettings.json` and update the `LLM` block:

```json
{
  "LLM": {
    "Provider": "openai",
    "BaseUrl": "https://api.together.xyz",
    "Model": "meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8",
    "EmbedModel": "BAAI/bge-base-en-v1.5",
    "ApiKey": "your-api-key-here"
  }
}
```

> ⚠️ Do **not** commit your API key to source control. For local dev only; use environment variables in all other environments (see Option B).

### Option B: Environment Variables (recommended for all deployments)

Environment variables override `appsettings.json` and use `__` for nested keys:

```bash
export LLM__Provider=openai
export LLM__BaseUrl=https://api.together.xyz
export LLM__Model=meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8
export LLM__EmbedModel=BAAI/bge-base-en-v1.5
export LLM__ApiKey=your-api-key-here
```

Then run the API as normal — the env vars will take precedence.

### Option C: Docker Compose Environment Block

If you're running the API via `docker compose up mtgforge`, add the variables to the `environment` block in `docker-compose.yml`:

```yaml
mtgforge:
  environment:
    - LLM__Provider=openai
    - LLM__BaseUrl=https://api.together.xyz
    - LLM__Model=meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8
    - LLM__EmbedModel=BAAI/bge-base-en-v1.5
    - LLM__ApiKey=your-api-key-here
```

> ⚠️ Do **not** commit your API key in `docker-compose.yml`. Use a `.env` file or your deployment platform's secret management instead.

---

## Step 4 — Local Dev Only: Pull the Ollama Embedding Model

> **Skip this step for Railway deployments.** When `LLM__Provider=openai`, embeddings run through Together.ai and Ollama is not required.

For local development only, if you want to run with `LLM__Provider=ollama` instead of Together.ai, pull the embedding model:

```bash
# Embedding model — small (~23 MB), fast on CPU
ollama pull all-minilm

# Verify it is available
ollama list
```

Make sure Ollama is running before starting the API:

```bash
ollama serve
```

---

## Step 5 — Start Infrastructure

```bash
# From the repo root
docker compose up -d mongodb qdrant

# Verify both are healthy
docker compose ps
```

MongoDB will be available on port `27017` and Qdrant on ports `6333` (HTTP) and `6334` (gRPC).

---

## Step 6 — Start the API

### Local (dotnet run)

```bash
dotnet run --project MtgForgeAi
```

The API starts at `http://localhost:5000`. Swagger UI is at `http://localhost:5000/swagger`.

### Docker

```bash
docker compose up mtgforge
```

The API is mapped to `http://localhost:5001` (internal port `8080`). Swagger is at `http://localhost:5001/swagger`.

---

## Step 7 — Verify the Setup

Call the health endpoint and confirm all three services are healthy:

```bash
curl http://localhost:5000/api/health
```

Expected response:

```json
{
  "status": "healthy",
  "llm": true,
  "mongodb": true,
  "qdrant": true
}
```

If `llm` is `false`:
- Confirm `LLM__Provider` is set to `openai`
- Confirm `LLM__ApiKey` is set and correct
- Check the API logs for the error message — Together.ai returns a descriptive body on auth failure

If `mongodb` or `qdrant` is `false`:
- Run `docker compose ps` and confirm both containers are running
- Run `docker compose up -d` to restart them if needed

---

## Step 8 — Ingest Card Data

If this is a fresh setup, you need to populate MongoDB and Qdrant with card data. The API must be running first.

```bash
# Quick test with 1000 cards (~1-2 min)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{"limit": 1000}'

# Full ingestion — all ~37k cards (5-30 min depending on embedding speed)
curl -X POST http://localhost:5000/api/admin/ingest \
  -H "Content-Type: application/json" \
  -d '{}'
```

Ingestion embeds cards using the configured `IEmbedService`. When `LLM__Provider=openai`, cards are embedded via Together.ai's `/v1/embeddings` endpoint using the `LLM__EmbedModel` model (default: `BAAI/bge-base-en-v1.5`).

> **Migrating from Ollama embeddings?** If you previously ingested cards with Ollama (`all-minilm`, 384-dim), you **must** delete the Qdrant `mtg_cards` collection before re-ingesting. The Together.ai embed model produces 768-dim vectors which are incompatible with the existing 384-dim collection.
>
> ```bash
> # Delete the existing collection via Qdrant HTTP API
> curl -X DELETE http://localhost:6333/collections/mtg_cards
> # Then re-run ingestion
> curl -X POST http://localhost:5000/api/admin/ingest -H "Content-Type: application/json" -d '{}'
> ```

If this is a fresh setup with no existing Qdrant data, no action is needed — the collection will be created automatically with the correct dimensions during ingestion.

---

## Step 9 — Generate a Test Deck

```bash
curl -X POST http://localhost:5000/api/decks/generate \
  -H "Content-Type: application/json" \
  -d '{
    "format": "commander",
    "commander": "Meren of Clan Nel Toth",
    "theme": "sacrifice and reanimation",
    "colorIdentity": ["B", "G"],
    "budget": 50.00,
    "powerLevel": 6
  }'
```

A successful response includes `sections` (grouped card lists), `estimatedCost`, and `reasoning` from the LLM. Generation typically takes **3–10 seconds** with Together.ai (versus up to several minutes on CPU-only Ollama).

---

## Switching Back to Ollama (Local Dev Only)

To switch to local Ollama inference for development, set:

```bash
export LLM__Provider=ollama
```

Or in `appsettings.json`:

```json
{
  "LLM": {
    "Provider": "ollama"
  }
}
```

The `LLM__BaseUrl`, `LLM__Model`, `LLM__EmbedModel`, and `LLM__ApiKey` values are ignored when `Provider` is `ollama`. The Ollama LLM model is controlled by `Ollama__Model` and the embed model by `Ollama__EmbedModel`.

> **Note:** Do not use Ollama as the LLM provider in Railway. Without GPU access, deck generation takes over 5 minutes. Together.ai is the correct choice for Railway deployments.

---

## Cost Reference

| Model | Input (per 1M tokens) | Output (per 1M tokens) | Cost per deck |
|---|---|---|---|
| `Llama-4-Maverick-17B-128E-Instruct-FP8` | ~$0.27 | ~$0.85 | ~$0.0035 ✅ Default |
| `Llama-3.3-70B-Instruct-Turbo` | ~$0.88 | ~$0.88 | ~$0.006 |
| `Qwen3.5-9B` | ~$0.10 | ~$0.15 | ~$0.001 |

Prices are approximate and subject to Together.ai's current rates. Check [https://api.together.ai/models](https://api.together.ai/models) for up-to-date pricing.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `LLM:ApiKey is required` startup error | `LLM__ApiKey` env var is missing or empty |
| `LLM API error 401` | API key is invalid or expired — regenerate in Together.ai dashboard |
| `LLM API error 429` | Rate limited — reduce request frequency or upgrade Together.ai plan |
| `LLM API error 404` on model | Model ID is incorrect — copy the exact ID from the Together.ai model list |
| `llm: false` on health check | API key is set but the `/v1/models` call failed — check firewall/network |
| Embeddings failing (openai provider) | Check `LLM__ApiKey` is set; check API logs for the embed error body |
| Embeddings failing (ollama provider) | Ollama is not running — `ollama serve` (local dev only) |
| Search returns no results after provider switch | Old Qdrant vectors use wrong dimensions — delete the `mtg_cards` collection and re-ingest |
| Deck JSON parse warnings in logs | The model added text around the JSON — the parser strips this automatically; `ParseDeckResponse` handles it |
