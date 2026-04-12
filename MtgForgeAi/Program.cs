using MtgForgeAi.Services;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);

// ─── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "MTG Forge AI", Version = "v1" });
});

// MongoDB
builder.Services.AddSingleton<MongoService>();

// Qdrant
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var host   = config["Qdrant:Host"] ?? "localhost";
    var port   = int.Parse(config["Qdrant:Port"] ?? "6334");
    return new QdrantClient(host, port);
});

// LLM provider — config-driven: set LLM:Provider to "ollama" or "openai" (Together.ai, etc.)
var llmProvider = builder.Configuration["LLM:Provider"]?.ToLowerInvariant() ?? "ollama";
if (llmProvider == "openai")
{
    builder.Services.AddHttpClient<OpenAiLlmService>();
    builder.Services.AddScoped<ILlmService, OpenAiLlmService>();
}
else
{
    builder.Services.AddHttpClient<OllamaLlmService>();
    builder.Services.AddScoped<ILlmService, OllamaLlmService>();
}

// Ollama embeddings (always Ollama — small model, fast on CPU)
builder.Services.AddHttpClient<OllamaEmbedService>();

// Scryfall — HttpClient for card ingestion
builder.Services.AddHttpClient("Scryfall", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MtgForgeAi/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

// Application services
builder.Services.AddScoped<OllamaEmbedService>();
builder.Services.AddScoped<CardSearchService>();
builder.Services.AddScoped<DeckGenerationService>();
builder.Services.AddScoped<CardIngestionService>();

// CORS — open for local dev
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// ─── App ──────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MTG Forge AI v1"));

app.UseCors();
app.MapControllers();

app.Run();
