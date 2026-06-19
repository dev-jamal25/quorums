using Backend.Infrastructure.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Slice-3 golden retrieval set (DL-047): the committed
/// <c>eval/datasets/552732e7-…/golden-retrieval.json</c> loads + validates via
/// <see cref="JsonDatasetLoader"/>, and every hand-labeled <c>relevant_chunk_id</c> resolves to a real
/// seeded chunk under the demo brand (no phantom / typo / wrong-byte-order ids).
/// </summary>
[Trait("Category", "Eval")]
[Collection("Knowledge")]
public sealed class GoldenRetrievalDatasetTests
{
    private readonly KnowledgeFixture _fixture;

    public GoldenRetrievalDatasetTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Golden_set_loads_validates_and_every_relevant_chunk_id_is_a_real_seeded_chunk()
    {
        // Loader enforces _meta, semver version, unique non-empty ids, and size == case count.
        var dataset = await JsonDatasetLoader.LoadAsync(
            DatasetPath(KnowledgeFixture.DemoBrand, "golden-retrieval.json"));

        Assert.Equal("golden-retrieval", dataset.Meta.Name);
        Assert.Equal(10, dataset.Meta.Size);
        Assert.Equal(dataset.Meta.Size, dataset.Cases.Count);

        // The 13 chunk ids actually seeded for the demo brand (read under its RLS scope).
        var seeded = await _fixture.SeededChunkIdsAsync(KnowledgeFixture.DemoBrand);
        Assert.Equal(13, seeded.Count);

        foreach (var evalCase in dataset.Cases)
        {
            var ids = evalCase.Expected.GetProperty("relevant_chunk_ids")
                .EnumerateArray()
                .Select(e => Guid.Parse(e.GetString()!))
                .ToList();

            Assert.NotEmpty(ids); // every golden query has at least one relevant chunk
            foreach (var id in ids)
            {
                Assert.Contains(id, seeded); // resolves to a real seeded chunk — no phantom id
            }
        }
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
