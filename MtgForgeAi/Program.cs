using MtgForgeAi.Logging;
using MtgForgeAi.Services;
using MtgForgeAi.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qdrant.Client;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Sinks.GrafanaLoki;

// Surface internal Serilog/Loki sink errors (HTTP failures, queue overflows,
// event size limit drops) so they are visible in the container console instead
// of being silently swallowed.
SelfLog.Enable(msg => Console.Error.WriteLine("[Serilog] {0}", msg));

var builder = WebApplication.CreateBuilder(args);

// ─── Logging (Serilog) ────────────────────────────────────────────────────────

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console();

    var lokiUrl = ctx.Configuration["LOKI_URI"];
    if (!string.IsNullOrWhiteSpace(lokiUrl))
    {
        // Railway (and other platforms) may supply the hostname without a
        // scheme (e.g. "loki.railway.internal:3100").  Uri.TryCreate accepts
        // "host.name:port" as a valid absolute URI because dots are legal in
        // RFC 3986 scheme names, so we must also verify the scheme is http/https.
        if (!Uri.TryCreate(lokiUrl, UriKind.Absolute, out var parsedLokiUri)
            || (parsedLokiUri.Scheme != Uri.UriSchemeHttp && parsedLokiUri.Scheme != Uri.UriSchemeHttps))
            lokiUrl = "http://" + lokiUrl;
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

        // Log to console so the Loki URL is confirmed at startup and the
        // first push to Loki is easy to spot if it fails in SelfLog output.
        Console.WriteLine("[Serilog] Loki sink configured → {0} (env={1}, auth={2})",
            lokiUrl, env, credentials is null ? "none" : "basic");
    }
});

// ─── OpenTelemetry ────────────────────────────────────────────────────────────

// The OTEL SDK reads OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_EXPORTER_OTLP_PROTOCOL /
// OTEL_EXPORTER_OTLP_HEADERS automatically when AddOtlpExporter() is called with
// no arguments, so no manual endpoint wiring is needed here.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(AppTelemetry.ServiceName, serviceVersion: AppTelemetry.ServiceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment.name"] = builder.Environment.EnvironmentName.ToLowerInvariant()
        }))
    .WithTracing(t =>
    {
        t.AddSource(AppTelemetry.ServiceName)
         .AddAspNetCoreInstrumentation(opts =>
         {
             opts.Filter = ctx =>
                 !ctx.Request.Path.StartsWithSegments("/api/health") &&
                 !ctx.Request.Path.StartsWithSegments("/metrics");
         })
         .AddHttpClientInstrumentation(opts =>
         {
             opts.RecordException = true;
         });

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddMeter(AppTelemetry.ServiceName)
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddRuntimeInstrumentation()
         .AddPrometheusExporter();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            m.AddOtlpExporter();
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

// Embed service — config-driven: follows LLM:Provider setting
// When "openai" (Together.ai), embeddings are generated via /v1/embeddings.
// When "ollama" (local dev), embeddings are generated via Ollama /api/embed.
if (llmProvider == "openai")
{
    builder.Services.AddHttpClient<OpenAiEmbedService>();
    builder.Services.AddScoped<IEmbedService, OpenAiEmbedService>();
}
else
{
    builder.Services.AddHttpClient<OllamaEmbedService>();
    builder.Services.AddScoped<IEmbedService, OllamaEmbedService>();
}

// Scryfall — HttpClient for card ingestion
builder.Services.AddHttpClient("Scryfall", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MtgForgeAi/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

// Application services
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
