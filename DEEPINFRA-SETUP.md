# DeepInfra Setup Guide — MTG Forge AI

This guide walks you through configuring MTG Forge AI to use DeepInfra as its LLM and embedding provider instead of a local Ollama model. DeepInfra gives you access to a wide catalog of open-source models (70B+) with GPU-accelerated inference at very low cost (~$0.01/1M tokens for embeddings) — ideal for this project.

> **Note on embeddings:** When `LLM:Provider` is set to `openai`, both deck generation **and** card embeddings run through DeepInfra. Ollama is no longer required on Railway. For local dev, you can still switch to Ollama by setting `LLM__Provider=ollama`, but note that this changes the active provider for **both** deck generation and embeddings.
>
> **Important:** If you are migrating from the Ollama embed provider to DeepInfra's embed provider, you must **delete the Qdrant `mtg_cards` collection and re-run ingestion**. The embedding models produce different vector dimensions (384-dim `all-minilm` vs 1024-dim `BAAI/bge-m3`), so the stored vectors are incompatible.

---

## Prerequisites

| Tool | Purpose | Install |
|---|---|---|
| Docker Desktop | MongoDB + Qdrant infrastructure (local dev) | https://www.docker.com/products/docker-desktop/ |
| .NET 10 SDK | Build and run the API (local dev) | https://dotnet.microsoft.com/download/dotnet/10 |
| Ollama | Embedding model (local dev only — not needed in Railway) | https://ollama.com/download |
| DeepInfra account | Hosted LLM inference and embeddings | https://deepinfra.com |

---

## Step 1 — Create a DeepInfra Account and Get an API Key

1. Go to [https://deepinfra.com](https://deepinfra.com) and sign up for a free account.
2. After signing in, navigate to **API keys** in the dashboard.
3. Click **Create new API key**, give it a name (e.g. `mtg-forge-dev`), and copy the key immediately.

> DeepInfra gives new accounts free credits to get started. For this project, deck generation costs roughly **$0.001–$0.003 per request** using the default `meta-llama/Llama-3.3-70B-Instruct` model.

---

## Step 2 — Choose a Model (Optional)

The default model is already configured in `appsettings.json`:

```
meta-llama/Llama-3.3-70B-Instruct
```

If you want to try a different model, browse the [DeepInfra model catalog](https://deepinfra.com/models) and filter by **Text Generation**. Some good alternatives:

| Model ID | Input $/1M | Notes |
|---|---|---|
| `meta-llama/Llama-3.3-70B-Instruct` | $0.23 | ✅ Default — strong JSON-following, large context |
| `meta-llama/Meta-Llama-3.1-70B-Instruct` | $0.23 | Slightly older Llama 3.1 variant |
| `Qwen/Qwen2.5-72B-Instruct` | $0.23 | Good alternative with strong instruction-following |
| `mistralai/Mistral-7B-Instruct-v0.3` | $0.06 | Budget option for testing |

Copy the full model ID from the DeepInfra catalog — you will need it in Step 3.

---

## Step 3 — Configure the Application

### Option A: Edit `appsettings.json` (local dev)

Open `MtgForgeAi/appsettings.json` and update the `LLM` block:

```json
{
  "LLM": {
    "Provider": "openai",
    "BaseUrl": "https://api.deepinfra.com/v1/openai",
    "Model": "meta-llama/Llama-3.3-70B-Instruct",
    "EmbedModel": "BAAI/bge-m3",
    "ApiKey": "your-api-key-here"
  }
}
```

> ⚠️ Do **not** commit your API key to source control. For local dev only; use environment variables in all other environments (see Option B).

### Option B: Environment Variables (recommended for all deployments)

Environment variables override `appsettings.json` and use `__` for nested keys:

```bash
export LLM__Provider=openai
export LLM__BaseUrl=https://api.deepinfra.com/v1/openai
export LLM__Model=meta-llama/Llama-3.3-70B-Instruct
export LLM__EmbedModel=BAAI/bge-m3
export LLM__ApiKey=your-api-key-here
```

Then run the API as normal — the env vars will take precedence.

### Option C: Docker Compose Environment Block

If you're running the API via `docker compose up mtgforge`, add the variables to the `environment` block in `docker-compose.yml`:

```yaml
mtgforge:
  environment:
    - LLM__Provider=openai
    - LLM__BaseUrl=https://api.deepinfra.com/v1/openai
    - LLM__Model=meta-llama/Llama-3.3-70B-Instruct
    - LLM__EmbedModel=BAAI/bge-m3
    - LLM__ApiKey=your-api-key-here
```

> ⚠️ Do **not** commit your API key in `docker-compose.yml`. Use a `.env` file or your deployment platform's secret management instead.

---

## Step 4 — Local Dev Only: Pull the Ollama Embedding Model

> **Skip this step for Railway deployments.** When `LLM__Provider=openai`, embeddings run through DeepInfra and Ollama is not required.

For local development only, if you want to run with `LLM__Provider=ollama` instead of DeepInfra, pull the embedding model:

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
- Check the API logs for the error message — DeepInfra returns a descriptive body on auth failure

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

Ingestion embeds cards using the configured `IEmbedService`. When `LLM__Provider=openai`, cards are embedded via DeepInfra's `/embeddings` endpoint using the `LLM__EmbedModel` model (default: `BAAI/bge-m3`, 1024-dim).

> **Migrating from Ollama embeddings?** If you previously ingested cards with Ollama (`all-minilm`, 384-dim), you **must** delete the Qdrant `mtg_cards` collection before re-ingesting. The DeepInfra `BAAI/bge-m3` model produces 1024-dim vectors which are incompatible with the existing 384-dim collection.
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

A successful response includes `sections` (grouped card lists), `estimatedCost`, and `reasoning` from the LLM. Generation typically takes **3–10 seconds** with DeepInfra (versus up to several minutes on CPU-only Ollama).

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

> **Note:** Do not use Ollama as the LLM provider in Railway. Without GPU access, deck generation takes over 5 minutes. DeepInfra is the correct choice for Railway deployments.

---

## Cost Reference

| Model | Input (per 1M tokens) | Output (per 1M tokens) | Cost per deck |
|---|---|---|---|
| `meta-llama/Llama-3.3-70B-Instruct` | ~$0.23 | ~$0.40 | ~$0.002 ✅ Default |
| `Qwen/Qwen2.5-72B-Instruct` | ~$0.23 | ~$0.40 | ~$0.002 |
| `mistralai/Mistral-7B-Instruct-v0.3` | ~$0.06 | ~$0.06 | ~$0.001 |
| `BAAI/bge-m3` (embed) | ~$0.01 | — | ~$0.0001/ingest |

Prices are approximate and subject to DeepInfra's current rates. Check [https://deepinfra.com/models](https://deepinfra.com/models) for up-to-date pricing.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `LLM:ApiKey is required` startup error | `LLM__ApiKey` env var is missing or empty |
| `LLM API error 401` | API key is invalid or expired — regenerate in DeepInfra dashboard |
| `LLM API error 429` | Rate limited — reduce request frequency or add credits to your DeepInfra account |
| `LLM API error 404` on model | Model ID is incorrect — copy the exact ID from the DeepInfra model catalog |
| `llm: false` on health check | API key is set but the `/v1/models` call failed — check firewall/network |
| Embeddings failing (openai provider) | Check `LLM__ApiKey` is set; check API logs for the embed error body |
| Embeddings failing (ollama provider) | Ollama is not running — `ollama serve` (local dev only) |
| Search returns no results after provider switch | Old Qdrant vectors use wrong dimensions — delete the `mtg_cards` collection and re-ingest |
| Deck JSON parse warnings in logs | The model added text around the JSON — the parser strips this automatically; `ParseDeckResponse` handles it |
