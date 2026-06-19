using Backend.Infrastructure.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The JSON dataset loader validates the <c>_meta</c> block, not just deserializes it (DL-047): a missing
/// block or a <c>size</c> that disagrees with the actual case count (the bump-rule field) is rejected
/// loudly, never loaded silently.
/// </summary>
[Trait("Category", "Eval")]
public sealed class JsonDatasetLoaderTests
{
    [Fact]
    public async Task Rejects_a_dataset_with_no_meta_block()
    {
        await using var dataset = await TempDatasetAsync("{ \"cases\": [] }");
        await Assert.ThrowsAsync<InvalidOperationException>(() => JsonDatasetLoader.LoadAsync(dataset.Path));
    }

    [Fact]
    public async Task Rejects_a_dataset_whose_meta_size_disagrees_with_the_case_count()
    {
        await using var dataset = await TempDatasetAsync(
            "{ \"_meta\": { \"name\": \"x\", \"version\": \"1.0.0\", \"size\": 5 }, \"cases\": [] }");
        await Assert.ThrowsAsync<InvalidOperationException>(() => JsonDatasetLoader.LoadAsync(dataset.Path));
    }

    private static async Task<TempDataset> TempDatasetAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eval-dataset-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return new TempDataset(path);
    }

    private sealed class TempDataset(string path) : IAsyncDisposable
    {
        public string Path { get; } = path;

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
