using MtgForgeAi.Logging;
using MtgForgeAi.Services;
using MtgForgeAi.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qdrant.Client;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.GrafanaLoki;

var builder = WebApplication.CreateBuilder(args);

// ─── Logging (Serilog) ────────────────────────────────────────────────────────

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console();

    var lokiUrl = ctx.Configuration["Loki:Url"];
    if (!string.IsNullOrWhiteSpace(lokiUrl))
    {
        var lokiUser     = ctx.Configuration["Loki:Username"] ?? string.Empty;
        var lokiPassword = ctx.Configuration["Loki:Password"] ?? string.Empty;
        var env          = ctx.HostingEnvironment.EnvironmentName;

        var credentials = string.IsNullOrWhiteSpace(lokiUser)
            ? null
            : new GrafanaLokiCredentials { User = lokiUser, Password = lokiPassword };

        // Build the Loki sink via a sub-logger, then wrap it with
        // LokiLabelSink to strip ASP.NET Core request-scope properties
        // before they become Loki stream labels (limit: 15).
        var lokiInner = (ILogEventSink)new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.GrafanaLoki(
                lokiUrl,
                credentials: credentials,
                labels: new Dictionary<string, string>
                {
                    ["app"] = "mtg-forge-ai",
                    ["env"] = env
                })
            .CreateLogger();

        cfg.WriteTo.Sink(new LokiLabelSink(lokiInner));
    }
});

// ─── OpenTelemetry ────────────────────────────────────────────────────────────

var otlpEndpoint = builder.Configuration["OTEL:Endpoint"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(AppTelemetry.ServiceName, serviceVersion: AppTelemetry.ServiceVersion))
    .WithTracing(t =>
    {
        t.AddSource(AppTelemetry.ServiceName)
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(m =>
    {
        m.AddMeter(AppTelemetry.ServiceName)
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddRuntimeInstrumentation()
         .AddPrometheusExporter();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });

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
builder.Services.AddSingleton<IMetaSignalRepository>(sp => sp.GetRequiredService<MongoService>());

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
builder.Services.AddScoped<ThemedSetService>();

// Tournament meta signals — reads from MongoDB collection populated by
// scripts/compute_meta_signals.py. Singleton so its in-memory cache is shared.
builder.Services.AddSingleton<MetaSignalService>();

// Ingestion status — singleton so all scoped CardIngestionService instances
// and the AdminController share the same live state.
builder.Services.AddSingleton<IngestionStatusService>();

// CORS — open for local dev
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// ─── App ──────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MTG Forge AI v1"));

app.UseCors();
app.MapControllers();
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
