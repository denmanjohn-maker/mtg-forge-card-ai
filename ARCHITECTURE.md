# mtg-forge — System Architecture

mtg-forge is a Magic: The Gathering deck management and AI generation platform built from three repositories that run as independent services, deployed on Railway or locally via Docker Compose.

---

## This service: forge-ai-api

**Role:** The RAG pipeline service. Responsible for card data ingestion from Scryfall into MongoDB and Qdrant, semantic card search, and LLM-based deck generation. forge-app delegates all deck generation to this service when configured in `Rag` mode.

- **Port:** 8080 (Railway) / mapped to 5001 locally
- **Databases:** MongoDB (cards, saved decks in `mtgforge` db) + Qdrant (384-dimensional vectors using Ollama `all-minilm`)
- **LLM backends:** `OllamaLlmService` for local development (Ollama running natively on macOS host); `OpenAiLlmService` for production (Together.ai or any OpenAI-compatible API). Selected via `LLM:Provider` config (`"ollama"` or `"openai"`).
- **Embeddings:** Always via `OllamaEmbedService` using Ollama `all-minilm` (384-dim). Never changes at query time without re-ingesting Qdrant.

---

## All services

### forge-app (`../forge-app`)
The primary user-facing service. Serves a REST API, Razor Pages, and a vanilla JS SPA from a single ASP.NET Core process. Owns auth (JWT + ASP.NET Identity), deck CRUD, pricing, CSV import/export, and user/group management. Calls **this service** for deck generation when `LlmProvider = Rag`.

- **Port:** 5000
- **Databases:** MongoDB (decks, users, groups in `mtgdeckforge` db) + PostgreSQL (Identity + MTGJSON pricing)

### forge-ai-api (`this repo` / `../forge-ai-api`)
See above.

### forge-observability (`../forge-observability`)
The shared observability stack. Collects telemetry from both application services.

- **Components:** Grafana Alloy (collector/router), Tempo (traces), Loki (logs), Prometheus (metrics), Grafana (dashboards)
- **Ingest:** Alloy receives OTLP on port 4317 (gRPC) and 4318 (HTTP)

---

## Service connections

### forge-app → this service

| | Local | Railway |
|-|-------|---------|
| Endpoint called | `{RagPipeline:BaseUrl}/api/decks/generate` | `http://mtg-forge-ai.railway.internal:8080/api/decks/generate` |
| Caller env var | `RagPipeline__BaseUrl` | `RagPipeline__BaseUrl` |
| Protocol | HTTP POST | HTTP POST |

forge-app's `RagPipelineService` sends a `DeckGenerationRequest` JSON body and receives a complete `DeckConfiguration` JSON response. This service handles the full RAG + LLM pipeline; forge-app then overlays prices from its own PostgreSQL cache and saves to its own MongoDB.

> **Note:** forge-app also calls Together.ai **directly** (not through this service) for deck analysis, budget replacement suggestions, and CSV import descriptions.

### This service → forge-observability

| Signal | Local | Railway |
|--------|-------|---------|
| Traces (OTLP gRPC) | `OTEL__Endpoint=http://tempo:4317` (direct to Tempo) | `OTEL__Endpoint=http://alloy.railway.internal:4317` |
| Logs (Loki sink) | `Loki:Url=http://loki:3100` | `Loki:Url=http://loki.railway.internal:3100` |
| Metrics | Prometheus scrapes `http://mtgforge:8080/metrics` | Alloy scrapes the Railway internal URL |

### This service → external dependencies

| Dependency | Purpose | Config |
|-----------|---------|--------|
| Ollama | Embeddings (always) + LLM chat (local mode) | `Ollama__BaseUrl` — default `http://host.docker.internal:11434`. Runs **natively on the Mac host**, not in Docker. |
| Together.ai / OpenAI-compatible | LLM chat (production) | `LLM__Provider=openai`, `LLM__ApiKey`, `LLM__BaseUrl=https://api.together.xyz` |
| Scryfall API | Bulk card data download during ingestion | Named `HttpClient("Scryfall")`, 5-minute timeout, hardcoded `https://api.scryfall.com` |
| Qdrant | Vector upsert (ingestion) + ANN search (generation) | `Qdrant__Host`, `Qdrant__Port=6334` (gRPC) |
| MongoDB | Card storage + saved deck persistence | `MongoDB__ConnectionString`, `MongoDB__DatabaseName` |

---

## Data flows

### Deck generation (called by forge-app)

```
forge-app RagPipelineService
  → POST /api/decks/generate        (this service :8080)
    → DecksController
      → DeckGenerationService.GenerateDeckAsync
          → CardSearchService.GetDeckCandidatesAsync
              → OllamaEmbedService → Ollama :11434 (embed the user prompt)
              → Qdrant ANN search (200-card pool, filtered by format legality + color identity)
          → BuildUserPrompt (groups/trims candidates into LLM context)
          → ILlmService.ChatAsync → Ollama or Together.ai (generate deck JSON)
          → ParseDeckResponse (strips markdown fences, deserializes, enriches from candidate pool)
          → MongoService.SaveDeckAsync (persist in this service's MongoDB)
        ← DeckConfiguration JSON
    ← response to forge-app
```

### Card ingestion

```
GitHub Action (.github/workflows/ingest.yml) or admin curl
  → POST /api/admin/ingest          (this service :8080)
    → CardIngestionService
      → Scryfall bulk data API (download AtomicCards JSON)
      → MongoService (upsert all cards)
      → filter legal cards (Commander, Standard, Modern, Legacy, Pioneer, Pauper, Vintage)
      → OllamaEmbedService → Ollama :11434 (embed each legal card, 384-dim)
          point ID = SHA-256(scryfallId) — deterministic, stable across re-ingestion
      → Qdrant (upsert vectors with payload: legality_*, colors, cmc, type, price)
```

> Qdrant payload fields (`legality_commander`, `legality_standard`, etc., and color fields) are set **only during ingestion**. Changing supported formats or legality fields requires a full re-ingest.

### Observability pipeline (Railway)

```
forge-app    ──OTLP gRPC──► Alloy :4317 ─┬─► Tempo       (traces)
             ──Loki HTTP──► Loki  :3100   ├─► Prometheus  (metrics)
             ◄──scrape /metrics── Alloy   └─► Loki        (logs)

forge-ai-api ──OTLP gRPC──► Alloy :4317       ↓
(this svc)   ──Loki HTTP──► Loki  :3100    Grafana queries Prometheus / Tempo / Loki
             ◄──scrape /metrics── Alloy
```

---

## Deployment

### Railway (production)

All three repos are deployed as independent Railway services. Services communicate over Railway's private network via `*.railway.internal` DNS — these names are **not** reachable from the public internet or local machines.

**This service (forge-ai-api) Railway env vars:**

| Variable | Value |
|----------|-------|
| `LLM__Provider` | `openai` |
| `LLM__ApiKey` | Together.ai API key |
| `LLM__BaseUrl` | `https://api.together.xyz` |
| `LLM__Model` | e.g. `meta-llama/Llama-3.3-70B-Instruct-Turbo` |
| `OTEL__Endpoint` | `http://alloy.railway.internal:4317` |
| `Loki__Url` | `http://loki.railway.internal:3100` |
| `MongoDB__ConnectionString` | MongoDB connection string |
| `Qdrant__Host` | Qdrant Railway internal hostname |
| `Qdrant__Port` | `6334` (gRPC) |

See `LOCAL-LLM-SETUP.md` for Railway + local Ollama hybrid topology and `TOGETHER-AI-SETUP.md` for full Together.ai configuration.

### Local development

Ollama must be running natively on the host before starting Docker services.

| Command | What starts |
|---------|-------------|
| `docker compose up -d` | MongoDB (:27017), Qdrant (:6333/:6334), API (:5001), Prometheus (:9090), Tempo (:3200/:4317/:4318), Grafana (:3000) |
| `dotnet run --project MtgForgeAi` | API only (needs MongoDB + Qdrant already running) |
| `curl -X POST http://localhost:5001/api/admin/ingest` | Trigger full card ingestion (downloads ~100k cards from Scryfall) |
