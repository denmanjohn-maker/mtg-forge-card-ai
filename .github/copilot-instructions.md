# MTG Forge AI — Copilot Instructions

## Build, test, and lint commands

```bash
# Infrastructure only (MongoDB + Qdrant)
docker compose up -d
docker compose ps

# Build the solution
dotnet build mtg-forge-ai.sln

# Current test command
dotnet test mtg-forge-ai.sln

# Run the API locally
dotnet run --project MtgForgeAi
```

There is currently no separate test project in the solution, so there is no meaningful single-test command yet. There is also no dedicated lint command or formatter configuration checked into the repo.

Useful runtime checks and data-loading commands from the repo docs:

```bash
# Health check
curl http://localhost:5000/api/health

# Ingest through the admin API
curl -X POST http://localhost:5000/api/admin/ingest -H "Content-Type: application/json" -d '{}'
curl -X POST http://localhost:5000/api/admin/ingest -H "Content-Type: application/json" -d '{"limit": 1000}'

# Ingest through Python
cd scripts
pip install -r requirements.txt
python ingest_cards.py
python ingest_cards.py --limit 1000
python ingest_cards.py --skip-download
python ingest_cards.py --mongo-only
python ingest_cards.py --qdrant-only
```

## High-level architecture

The repo is an API-only local RAG system for MTG deck generation. `MtgForgeAi` is the only .NET project in the solution; it exposes controllers for deck generation, semantic card search, ingestion, health, and saved-deck CRUD. The API depends on MongoDB for cards and saved decks, Qdrant for vector search, and Ollama for both chat completions and embeddings.

Two ingestion paths feed the same runtime data model:

- `POST /api/admin/ingest` uses `CardIngestionService` to download Scryfall data, upsert cards into MongoDB, embed legal cards through Ollama, and upsert vectors into Qdrant.
- `scripts/ingest_cards.py` is the local-development ingestion script and must stay aligned with the .NET embedding/search setup.

The main runtime path is:

1. `DecksController.Generate` validates format and Commander requirements, then calls `DeckGenerationService.GenerateDeckAsync`.
2. `DeckGenerationService` asks `CardSearchService.GetDeckCandidatesAsync` for a 200-card candidate pool from Qdrant.
3. `BuildUserPrompt` groups and trims candidates for context, then `OllamaService.ChatAsync` generates JSON.
4. `ParseDeckResponse` strips code fences or preamble, deserializes the LLM output, enriches chosen cards from the candidate pool, computes cost, and persists the result through `MongoService`.

Format support is cross-cutting. `DeckRequest.Format` is not just validation input: it selects the Qdrant legality field, deck-size and copy-limit rules, prompt categories, and the budget-per-card heuristic used during candidate retrieval.

## Key conventions

- Ollama runs natively on macOS, not in Docker. When the API runs in Docker, it reaches Ollama via `host.docker.internal:11434`.
- Embedding dimensions must stay aligned across ingestion and query-time search. Python ingestion uses `all-MiniLM-L6-v2`, while the .NET services use Ollama `all-minilm`; both are 384-dimensional. If the embedding model changes, re-ingest Qdrant data.
- The Qdrant collection stores all supported formats in one place. Filtering happens by payload fields like `legality_commander`, `legality_standard`, `legality_modern`, `legality_legacy`, `legality_pioneer`, `legality_pauper`, and `legality_vintage`.
- Color filtering is exclusion-based, not inclusion-based: `CardSearchService` builds `MustNot` conditions for every color outside the requested identity so colorless, mono-color, and exact-match cards remain eligible.
- Service lifetimes matter: `MongoService` and `QdrantClient` are singletons; `OllamaService`, `OllamaEmbedService`, `CardSearchService`, `DeckGenerationService`, and `CardIngestionService` are scoped.
- `OllamaService` and `OllamaEmbedService` each get their own typed `HttpClient`; the named `"Scryfall"` client is reserved for ingestion and uses a 5-minute timeout.
- Request/response API shapes use C# `record` types. MongoDB documents use mutable `class` types with BSON attributes. The LLM-only deserialization models are private nested classes inside `DeckGenerationService`.
- `DeckGenerationService.ParseDeckResponse` is intentionally defensive: it removes markdown fences, extracts the first JSON object if the model adds text around it, and falls back to the raw LLM response as `Reasoning` if deserialization fails.
- `CardIngestionService` builds deterministic Qdrant point IDs from Scryfall IDs with SHA-256 instead of `GetHashCode`, so vector IDs remain stable across runs.
- Changing supported formats or legality payload fields requires re-ingestion because Qdrant payloads are produced during ingestion, not at query time.
