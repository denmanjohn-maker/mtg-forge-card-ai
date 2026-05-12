using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MtgForgeAi.Models;
using MtgForgeAi.Services;
using Qdrant.Client;

namespace MtgForgeAi.Tests;

public class DeckCandidateRetrievalBakeoffTests
{
    private const string RunEnv = "RUN_RETRIEVAL_BENCHMARK";
    private const string ApiKeyEnv = "BENCH_LLM_API_KEY";
    private const string BaseUrlEnv = "BENCH_LLM_BASE_URL";
    private const string QdrantHostEnv = "BENCH_QDRANT_HOST";
    private const string QdrantPortEnv = "BENCH_QDRANT_PORT";

    [Fact]
    public async Task Bakeoff_SelectsBestCandidateByRetrievalQuality_AndChecksRegressionThresholds()
    {
        if (!ShouldRun())
            return;

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnv);
        Assert.False(string.IsNullOrWhiteSpace(apiKey), $"{ApiKeyEnv} is required when {RunEnv}=1.");

        var benchmark = await LoadBenchmarkAsync();
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlEnv) ?? "https://api.deepinfra.com/v1/openai";
        var qdrantHost = Environment.GetEnvironmentVariable(QdrantHostEnv) ?? "localhost";
        var qdrantPort = int.TryParse(Environment.GetEnvironmentVariable(QdrantPortEnv), out var parsedPort)
            ? parsedPort
            : 6334;

        var candidateScores = new List<CandidateAggregate>();
        var qdrant = new QdrantClient(qdrantHost, qdrantPort);

        foreach (var candidate in benchmark.Candidates)
        {
            var servicesConfig = BuildConfig(baseUrl, apiKey!, candidate.EmbedModel, candidate.QdrantCollection);
            var http = new HttpClient();
            var embed = new OpenAiEmbedService(http, servicesConfig, NullLogger<OpenAiEmbedService>.Instance);
            var search = new CardSearchService(qdrant, embed, NullLogger<CardSearchService>.Instance, servicesConfig);

            var perQuery = new List<MetricSet>();
            foreach (var query in benchmark.Queries)
            {
                var results = await search.GetDeckCandidatesAsync(query.Request);
                perQuery.Add(Evaluate(query.RelevanceByCardName, results, benchmark.Evaluation.K));
            }

            candidateScores.Add(CandidateAggregate.From(candidate.Name, perQuery));
        }

        Assert.True(candidateScores.Count >= 2, "At least two embedding candidates are required for bake-off.");

        var ordered = candidateScores
            .OrderByDescending(c => c.NdcgAtK)
            .ThenByDescending(c => c.RecallAtK)
            .ThenByDescending(c => c.PrecisionAtK)
            .ToList();

        var winner = ordered[0];
        var runnerUp = ordered[1];
        var deltaPct = winner.NdcgAtK <= 0
            ? 0
            : ((winner.NdcgAtK - runnerUp.NdcgAtK) / winner.NdcgAtK) * 100.0;
        var isStatisticallyClose = deltaPct <= benchmark.Selection.CloseMarginPercent;

        var selected = benchmark.Candidates.Single(c => c.IsSelectedDefault);
        Assert.Equal(selected.Name, winner.Name);

        Assert.True(
            winner.PrecisionAtK >= benchmark.RegressionThresholds.PrecisionAtK,
            $"Precision@{benchmark.Evaluation.K} regression: {winner.PrecisionAtK:F3} < {benchmark.RegressionThresholds.PrecisionAtK:F3}");
        Assert.True(
            winner.RecallAtK >= benchmark.RegressionThresholds.RecallAtK,
            $"Recall@{benchmark.Evaluation.K} regression: {winner.RecallAtK:F3} < {benchmark.RegressionThresholds.RecallAtK:F3}");
        Assert.True(
            winner.NdcgAtK >= benchmark.RegressionThresholds.NdcgAtK,
            $"NDCG@{benchmark.Evaluation.K} regression: {winner.NdcgAtK:F3} < {benchmark.RegressionThresholds.NdcgAtK:F3}");
        Assert.True(
            winner.TypeDiversityAtK >= benchmark.RegressionThresholds.TypeDiversityAtK,
            $"Type diversity@{benchmark.Evaluation.K} regression: {winner.TypeDiversityAtK:F3} < {benchmark.RegressionThresholds.TypeDiversityAtK:F3}");

        var summary =
            $"Winner={winner.Name} NDCG@{benchmark.Evaluation.K}={winner.NdcgAtK:F3}; " +
            $"RunnerUp={runnerUp.Name} NDCG@{benchmark.Evaluation.K}={runnerUp.NdcgAtK:F3}; " +
            $"Delta={deltaPct:F2}% (close={isStatisticallyClose})";
        Console.WriteLine(summary);
    }

    private static bool ShouldRun() =>
        string.Equals(Environment.GetEnvironmentVariable(RunEnv), "1", StringComparison.OrdinalIgnoreCase);

    private static IConfiguration BuildConfig(string baseUrl, string apiKey, string embedModel, string collectionName) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LLM:BaseUrl"] = baseUrl,
                ["LLM:ApiKey"] = apiKey,
                ["LLM:EmbedModel"] = embedModel,
                ["Qdrant:CollectionName"] = collectionName
            })
            .Build();

    private static async Task<BenchmarkSpec> LoadBenchmarkAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "deck-candidate-retrieval-benchmark.json");
        await using var stream = File.OpenRead(path);
        var benchmark = await JsonSerializer.DeserializeAsync<BenchmarkSpec>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return benchmark ?? throw new InvalidOperationException("Failed to deserialize retrieval benchmark specification.");
    }

    private static MetricSet Evaluate(
        Dictionary<string, int> relevanceByCardName,
        List<CardSearchResult> results,
        int k)
    {
        var top = results
            .OrderByDescending(r => r.Score)
            .Take(k)
            .ToList();

        var relevantTotal = relevanceByCardName.Values.Count(v => v > 0);
        var hits = 0;
        var dcg = 0.0;
        var typeBuckets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < top.Count; i++)
        {
            var card = top[i];
            var rel = relevanceByCardName.TryGetValue(card.Name, out var relevance)
                ? relevance
                : 0;

            if (rel > 0)
                hits++;

            if (rel > 0)
                dcg += ((1 << rel) - 1) / Math.Log2(i + 2);

            var bucket = GetTypeBucket(card.TypeLine);
            if (!string.IsNullOrWhiteSpace(bucket))
                typeBuckets.Add(bucket);
        }

        var ideal = relevanceByCardName.Values
            .Where(v => v > 0)
            .OrderByDescending(v => v)
            .Take(k)
            .ToList();

        var idcg = 0.0;
        for (var i = 0; i < ideal.Count; i++)
            idcg += ((1 << ideal[i]) - 1) / Math.Log2(i + 2);

        var precision = top.Count == 0 ? 0 : (double)hits / top.Count;
        var recall = relevantTotal == 0 ? 0 : (double)hits / relevantTotal;
        var ndcg = idcg == 0 ? 0 : dcg / idcg;
        return new MetricSet(precision, recall, ndcg, typeBuckets.Count);
    }

    private static string GetTypeBucket(string? typeLine)
    {
        if (string.IsNullOrWhiteSpace(typeLine))
            return "";

        var major = typeLine.Split('—', StringSplitOptions.TrimEntries)[0];
        if (major.Contains("Land", StringComparison.OrdinalIgnoreCase)) return "Land";
        if (major.Contains("Creature", StringComparison.OrdinalIgnoreCase)) return "Creature";
        if (major.Contains("Instant", StringComparison.OrdinalIgnoreCase)) return "Instant";
        if (major.Contains("Sorcery", StringComparison.OrdinalIgnoreCase)) return "Sorcery";
        if (major.Contains("Artifact", StringComparison.OrdinalIgnoreCase)) return "Artifact";
        if (major.Contains("Enchantment", StringComparison.OrdinalIgnoreCase)) return "Enchantment";
        if (major.Contains("Planeswalker", StringComparison.OrdinalIgnoreCase)) return "Planeswalker";
        if (major.Contains("Battle", StringComparison.OrdinalIgnoreCase)) return "Battle";
        return major.Trim();
    }

    private sealed record BenchmarkSpec(
        SelectionSpec Selection,
        RegressionThresholdSpec RegressionThresholds,
        EvaluationSpec Evaluation,
        List<CandidateSpec> Candidates,
        List<QuerySpec> Queries);

    private sealed record SelectionSpec(string PrimaryMetric, double CloseMarginPercent);

    private sealed record RegressionThresholdSpec(
        double PrecisionAtK,
        double RecallAtK,
        double NdcgAtK,
        double TypeDiversityAtK);

    private sealed record EvaluationSpec(int K);

    private sealed record CandidateSpec(
        string Name,
        string EmbedModel,
        string QdrantCollection,
        bool IsSelectedDefault);

    private sealed record QuerySpec(
        string Name,
        DeckRequest Request,
        Dictionary<string, int> RelevanceByCardName);

    private sealed record MetricSet(
        double PrecisionAtK,
        double RecallAtK,
        double NdcgAtK,
        double TypeDiversityAtK);

    private sealed record CandidateAggregate(
        string Name,
        double PrecisionAtK,
        double RecallAtK,
        double NdcgAtK,
        double TypeDiversityAtK)
    {
        public static CandidateAggregate From(string name, List<MetricSet> values)
        {
            var denom = values.Count == 0 ? 1 : values.Count;
            return new CandidateAggregate(
                Name: name,
                PrecisionAtK: values.Sum(v => v.PrecisionAtK) / denom,
                RecallAtK: values.Sum(v => v.RecallAtK) / denom,
                NdcgAtK: values.Sum(v => v.NdcgAtK) / denom,
                TypeDiversityAtK: values.Sum(v => v.TypeDiversityAtK) / denom
            );
        }
    }
}
