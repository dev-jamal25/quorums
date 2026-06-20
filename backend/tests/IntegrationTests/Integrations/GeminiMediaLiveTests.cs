using Backend.Core.Common;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Gemini;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Backend.IntegrationTests.Integrations;

/// <summary>
/// Opt-in live round-trip for the real Gemini media backend (STEP C). Runs the generation graph
/// (deterministic Claude mock â†’ a real CD-stamped <c>MediaPromptBrief</c>) with the <b>live</b>
/// <see cref="LiveGeminiMediaTool"/> and asserts a genuine image lands in storage behind a
/// <see cref="MediaAssetRef"/>. Key-gated on <c>Gemini__ApiKey</c> â€” absent (as in CI, which runs
/// the mock) it no-ops, so neither the daily nor the 10-RPM free-tier limit ever touches CI. It
/// fires exactly ONE Gemini request (well within 10 RPM); idempotency-on-retry is structural (the
/// node's deterministic asset id) and proven on the mock in <c>StorageTests</c>.
/// </summary>
[Trait("Category", "LiveGemini")]
public sealed class GeminiMediaLiveTests
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";
    private const string DefaultModel = "gemini-2.5-flash-image";

    private readonly ITestOutputHelper _output;

    public GeminiMediaLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Live_brief_generates_a_real_image_into_storage()
    {
        var apiKey = Environment.GetEnvironmentVariable("Gemini__ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // xUnit v2 has no dynamic Assert.Skip; the key gate keeps this opt-in (CI runs the mock).
            _output.WriteLine("SKIP: no Gemini__ApiKey â€” opt-in live Gemini test.");
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("Gemini__BaseUrl") ?? DefaultBaseUrl;
        var model = Environment.GetEnvironmentVariable("Gemini__Model") ?? DefaultModel;

        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        var options = Options.Create(new GeminiOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Model = model,
            ApiVersion = "v1beta",
        });
        var live = new LiveGeminiMediaTool(http, options, NullLogger<LiveGeminiMediaTool>.Instance);
        var capturing = new CapturingMediaTool(live);

        var brandId = Guid.NewGuid();
        var state = TestGeneration.Seed(Guid.NewGuid(), brandId);
        var storage = new InMemoryStorageService();
        var orchestrator = TestGeneration.Orchestrator(
            TestGeneration.Deps(media: capturing, storage: storage));

        // ONE generation = ONE Gemini call site (the node's bounded retry may re-issue it).
        var result = await orchestrator.RunGenerationAsync(state);

        // A quota/rate-limit (429) is an environment limit, not a code regression: the free-tier
        // image quota can be 0/day. Log it as PENDING (round-trip needs a billing-enabled key)
        // rather than red-failing â€” the tool already mapped it to a structured ToolError.
        if (result.FatalError is { Code: "media.generation_failed" } err &&
            (err.Message.Contains("429", StringComparison.Ordinal) ||
             err.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal)))
        {
            _output.WriteLine(
                "PENDING: Gemini quota exhausted (free-tier daily image limit). Enable billing / use a " +
                $"paid-tier key to prove the live round-trip. Detail: {err.Message}");
            return;
        }

        Assert.Null(result.FatalError);
        Assert.Equal(1, capturing.Calls);

        // The tool returned genuine image bytes (not the 70-byte mock PNG).
        Assert.NotNull(capturing.Last);
        Assert.StartsWith("image/", capturing.Last!.MimeType, StringComparison.Ordinal);
        Assert.True(capturing.Last.Bytes.Length > 1000, $"expected a real image, got {capturing.Last.Bytes.Length} bytes");
        Assert.True(IsPngOrJpeg(capturing.Last.Bytes), "stored bytes are not a recognizable PNG/JPEG");

        // â€¦wired through the node into a MediaAssetRef + a single brand-prefixed object (DL-022).
        Assert.NotNull(result.Media);
        Assert.StartsWith("image/", result.Media!.MimeType, StringComparison.Ordinal);
        Assert.StartsWith(StorageKeys.AssetPrefix(brandId), result.Media.StorageKey, StringComparison.Ordinal);
        Assert.True(await storage.ExistsAsync(result.Media.StorageKey));
        var assetId = DeterministicGuid.From(state.RunId, "asset");
        Assert.Equal(
            StorageKeys.ForAsset(brandId, assetId, result.Media.MimeType.Split('/')[^1]),
            result.Media.StorageKey);
        Assert.Single(await storage.ListAsync(StorageKeys.AssetPrefix(brandId)));

        _output.WriteLine(
            $"LIVE OK â€” model={model} mime={result.Media.MimeType} bytes={capturing.Last.Bytes.Length} key={result.Media.StorageKey}");
    }

    private static bool IsPngOrJpeg(byte[] bytes) =>
        (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) || // PNG
        (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF);                       // JPEG

    /// <summary>Delegates to the real tool while capturing the result, so the single live request is asserted on.</summary>
    private sealed class CapturingMediaTool(IMediaGenerationTool inner) : IMediaGenerationTool
    {
        public int Calls { get; private set; }
        public MediaResult? Last { get; private set; }

        public async Task<MediaResult> GenerateAsync(
            MediaGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = await inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
            return Last;
        }
    }
}
