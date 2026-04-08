using MtgForgeLocal.Services;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);

// ─── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "MTG Forge Local", Version = "v1" });
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

// Ollama — two named HttpClients (chat and embed may have different timeouts)
builder.Services.AddHttpClient<OllamaService>();
builder.Services.AddHttpClient<OllamaEmbedService>();

// Scryfall — HttpClient for card ingestion
builder.Services.AddHttpClient("Scryfall");

// Application services
builder.Services.AddScoped<OllamaEmbedService>();
builder.Services.AddScoped<OllamaService>();
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
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MTG Forge Local v1"));

app.UseCors();
app.MapControllers();

app.Run();
