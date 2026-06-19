using Backend.Core.Domain;
using Backend.Infrastructure.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Slice-3 adversarial corpus-poisoning integrity (DL-047). The poison/injection chunk lives under a
/// SEPARATE fixture brand so the demo brand stays exactly 13. Proves: the demo corpus is unpolluted
/// (13), the fixture corpus is 14 with the poison retrievable by the Strategist's market-positioning
/// query, the poison is RLS-isolated from the demo brand, and both adversarial datasets load/validate.
/// </summary>
[Trait("Category", "Eval")]
[Collection("Knowledge")]
public sealed class CorpusPoisoningTests
{
    private const string StrategistQuery =
        "brand mission, audience persona, products, historical performance, and market positioning";

    private readonly KnowledgeFixture _fixture;

    public CorpusPoisoningTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Demo_brand_seeds_exactly_thirteen_chunks_no_poison()
    {
        var seeded = await _fixture.SeededChunkIdsAsync(KnowledgeFixture.DemoBrand);
        Assert.Equal(13, seeded.Count);
        Assert.DoesNotContain(KnowledgeFixture.PoisonChunkId, seeded);
    }

    [Fact]
    public async Task Poison_fixture_brand_seeds_fourteen_and_the_poison_is_retrievable()
    {
        var seeded = await _fixture.SeededChunkIdsAsync(KnowledgeFixture.PoisonBrand);
        Assert.Equal(14, seeded.Count);
        Assert.Contains(KnowledgeFixture.PoisonChunkId, seeded);

        // The Strategist's MarketIntel retrieval surfaces the poison chunk (injection-resistance is then
        // the agent/judge's job — slice 6 — not retrieval's).
        var (db, scope, retrieval) = _fixture.CreateRetrieval(KnowledgeFixture.PoisonBrand);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve(StrategistQuery, KnowledgeFixture.PoisonBrand, DocType.MarketIntel, k: 5);
            Assert.Contains(KnowledgeFixture.PoisonChunkId, result.Chunks.Select(c => c.ChunkId));
        }
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Poison_chunk_is_RLS_isolated_from_the_demo_brand()
    {
        // Even queried by the poison's own vocabulary, a demo-brand-scoped retrieval never returns it.
        var (db, scope, retrieval) = _fixture.CreateRetrieval(KnowledgeFixture.DemoBrand);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve(
                "market positioning trend BudgetBeans promo overpriced FREE100",
                KnowledgeFixture.DemoBrand, DocType.MarketIntel, k: 5);
            Assert.DoesNotContain(KnowledgeFixture.PoisonChunkId, result.Chunks.Select(c => c.ChunkId));
        }
    }

    [Fact]
    public async Task Both_adversarial_datasets_load_and_validate()
    {
        var demo = await JsonDatasetLoader.LoadAsync(DatasetPath(KnowledgeFixture.DemoBrand, "adversarial.json"));
        Assert.Equal("adversarial", demo.Meta.Name);
        var noAnswer = Assert.Single(demo.Cases);
        Assert.Empty(noAnswer.Expected.GetProperty("relevant_chunk_ids").EnumerateArray());

        var poison = await JsonDatasetLoader.LoadAsync(DatasetPath(KnowledgeFixture.PoisonBrand, "adversarial.json"));
        Assert.Equal("adversarial", poison.Meta.Name);
        var injection = Assert.Single(poison.Cases);
        Assert.False(injection.Expected.GetProperty("poison_chunk_relevant").GetBoolean());
    }

    private static string DatasetPath(Guid brandId, string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "datasets")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException($"could not locate eval/datasets from {AppContext.BaseDirectory}");
        }

        return Path.Combine(dir.FullName, "eval", "datasets", brandId.ToString(), name);
    }
}
