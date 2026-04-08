# MTG Forge Local — Copilot Instructions

## Architecture

Full-stack Commander deck generator running entirely locally (no cloud APIs after setup):

```
.NET 10 ASP.NET Core API (port 5000)
    ├──► MongoDB (port 27017)     — card catalog + saved decks
    ├──► Qdrant (port 6333/6334)  — vector embeddings for semantic card search
    └──► Ollama (port 11434)      — local LLM (llama3.1:8b) + embeddings (all-minilm)

Python scripts/
    └── ingest_cards.py           — one-time pipeline: Scryfall → MongoDB + Qdrant
```

**Critical**: Ollama runs natively on macOS (Metal GPU), NOT in Docker. When running the .NET API inside Docker, it reaches Ollama via `host.docker.internal:11434`.

### Deck Generation Pipeline (the core flow)

`DeckGenerationService.GenerateDeckAsync` orchestrates:
1. `CardSearchService.GetDeckCandidatesAsync` → 200 cards from Qdrant via semantic search
2. `BuildUserPrompt` → groups candidates by card type, trims to 150 (context window), formats for LLM
3. `OllamaService.ChatAsync` → sends system + user prompt, expects JSON back
4. `ParseDeckResponse` → strips markdown fences, extracts `{...}`, deserializes `LlmDeckOutput`
5. Price enrichment from the candidate pool's `priceMap`, then save to MongoDB

### Embedding Model Constraint

**The Python ingestion script and the .NET embed service must use the same vector dimensions.**

- `scripts/ingest_cards.py` uses `all-MiniLM-L6-v2` via sentence-transformers → **384-dim**
- `MtgForgeLocal/Services/OllamaEmbedService.cs` uses Ollama's `all-minilm` → **384-dim**
- `appsettings.json` sets `"EmbedModel": "all-minilm"` (384-dim) — matches the Python ingestion script

If you change the embed model in either place, you must change both and re-run ingestion.

### Multi-Format Support

The API supports: `commander`, `standard`, `modern`, `legacy`, `pioneer`, `pauper`, `vintage`.

`DeckRequest.Format` drives everything downstream:
- `CardSearchService.GetLegalityField(format)` → picks the right Qdrant payload field (`legality_standard`, `legality_commander`, etc.)
- `DeckGenerationService.GetFormatRules(format)` → deck size (60 or 100), singleton flag, max copies, prompt categories
- `CardSearchService.GetDeckCandidatesAsync` → adjusts budget-per-card split (70 slots for Commander, 36 for 60-card formats)

**Important:** The Qdrant collection must be re-ingested after the format support update. Old collections only have `legality_commander`; new ones have `legality_{format}` for all supported formats. Run:
```bash
cd scripts && python ingest_cards.py  # Drops and recreates collection with all legality fields
```

---

## Running the Project

### Infrastructure (Docker)
```bash
# From project root
docker compose up -d          # MongoDB + Qdrant
docker compose ps             # verify healthy
```

### .NET API
```bash
cd MtgForgeLocal
dotnet run                    # http://localhost:5000
# Swagger UI at http://localhost:5000/swagger
```

### Python Ingestion (one-time setup)
```bash
cd scripts
pip install -r requirements.txt
python ingest_cards.py                # full ~27k cards, 5-15 min
python ingest_cards.py --limit 1000  # quick test subset
python ingest_cards.py --skip-download  # reuse cached Scryfall file
python ingest_cards.py --mongo-only  # skip Qdrant
python ingest_cards.py --qdrant-only # skip MongoDB
```

---

## Key Conventions

### Service Registration
- `MongoService` and `QdrantClient` → **Singleton** (registered in `Program.cs`)
- `OllamaService`, `OllamaEmbedService`, `CardSearchService`, `DeckGenerationService`, `CardIngestionService` → **Scoped**
- `OllamaService` and `OllamaEmbedService` each get their own named `HttpClient` via `AddHttpClient<T>()`
- A named `"Scryfall"` `HttpClient` (5-minute timeout) is registered for `CardIngestionService`

### Models
- **Request/response DTOs** use C# `record` types (`DeckRequest`, `DeckResponse`, `CardSearchResult`, etc.)
- **MongoDB documents** use `class` with `[BsonElement]` attributes (`MtgCard`, `SavedDeck`)
- Internal LLM deserialization shapes (`LlmDeckOutput`, `LlmSection`, `LlmCard`) are **private nested classes** inside `DeckGenerationService`

### Configuration
All external service URLs/credentials come from `appsettings.json` (or environment variables in Docker):
- `MongoDB:ConnectionString`, `MongoDB:DatabaseName`
- `Qdrant:Host`, `Qdrant:Port` (gRPC port 6334, not HTTP 6333)
- `Ollama:BaseUrl`, `Ollama:Model`, `Ollama:EmbedModel`

### Qdrant Payload Fields
Filterable fields stored in Qdrant (set by `ingest_cards.py::build_payload`):
- `color_identity` (array of color strings: `"B"`, `"G"`, etc.)
- `legality_commander`, `legality_standard`, `legality_modern`, `legality_legacy`, `legality_pioneer`, `legality_pauper`, `legality_vintage` (each `"legal"` or `"not_legal"`)
- `price_usd` (float)
- `type_line`, `name`, `oracle_text`, `mana_cost`, `cmc`, `image_uri`, `scryfall_uri`

The Qdrant collection `mtg_cards` stores cards for all supported formats. Cards are filtered by the appropriate legality field at query time.

### LLM Response Parsing
The LLM is prompted to return pure JSON. `ParseDeckResponse` defensively:
1. Strips ` ```json ` / ` ``` ` markdown fences
2. Finds the first `{` and last `}` to extract JSON even if the LLM adds preamble
3. Falls back to returning `raw` as the `Reasoning` string if `JsonSerializer.Deserialize` fails

### Ollama HTTP Client
- `OllamaService` uses `JsonNamingPolicy.SnakeCaseLower` + `DefaultIgnoreCondition.WhenWritingNull`
- Timeout is 5 minutes (LLM can be slow on first token); embed timeout is 30 seconds
- `OllamaService.StreamAsync` returns `IAsyncEnumerable<string>` for streaming token-by-token output (not yet wired to any endpoint — available for future use)

### Budget Calculation
In `CardSearchService.GetDeckCandidatesAsync`:
- Per-card budget = `Budget / 70.0` (leaves headroom vs strict 100-card split)
- Qdrant filter uses `MaxPrice = perCardBudget * 3` to allow some expensive staples in the candidate pool
