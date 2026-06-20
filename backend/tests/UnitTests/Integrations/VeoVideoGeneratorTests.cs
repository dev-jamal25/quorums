using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.UnitTests.Integrations;

/// <summary>
/// The DL-058 audit-#1 adversarial proof: the Veo async core is <b>submit-or-resume idempotent</b> on the
/// deterministic <c>assetId</c>. Veo is paid + asynchronous, so a node retry must resume the in-flight
/// operation (recorded in <see cref="VeoOperationStore"/> the instant submit returns) and bill ZERO new
/// jobs. A fake Veo client exposes a submit counter; the generator + a real store drive the real path.
/// Also proves the bounded poll loop degrades a never-finishing operation to a thrown
/// <see cref="VeoGenerationException"/> (the Media node turns that into caption-only).
/// </summary>
public sealed class VeoVideoGeneratorTests
{
    private static readonly MediaPromptBrief _videoBrief = new(
        Subject: "a pour-over cup with rising steam",
        Style: "warm editorial",
        Composition: "centered",
        Palette: "kraft tones",
        Mood: "calm",
        Negative: null,
        AspectRatio: "9:16",
        Modality: "video",
        DurationSec: 5);

    private static VeoVideoGenerator Generator(IVeoClient client, VeoOperationStore store, VeoOptions? options = null) =>
        new(client, store, Options.Create(options ?? FastOptions()), NullLogger<VeoVideoGenerator>.Instance);

    private static VeoOptions FastOptions() => new()
    {
        Model = "veo-test",
        MaxDurationSec = 5,
        PollTimeout = TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    [Fact]
    public async Task A_node_retry_resumes_the_in_flight_operation_and_submits_zero_new_jobs()
    {
        var store = new VeoOperationStore();
        var client = new FakeVeoClient(submitOpName: "models/veo/operations/op-1");
        var generator = Generator(client, store);
        var assetId = Guid.NewGuid();

        // First attempt: submits exactly once and records the op (the generator does NOT evict — the node
        // does, after committing the asset, so the op is present for an in-flight retry).
        var first = await generator.GenerateAsync(_videoBrief, seedImage: null, assetId);
        Assert.Equal(1, client.SubmitCount);
        Assert.True(store.TryGet(assetId, out var recorded));
        Assert.Equal("models/veo/operations/op-1", recorded);

        // Simulated Hangfire/Polly retry of the node (same assetId): resumes the recorded op, never submits.
        var second = await generator.GenerateAsync(_videoBrief, seedImage: null, assetId);

        Assert.Equal(1, client.SubmitCount);                       // ZERO new Veo jobs on retry
        Assert.All(client.PolledOperations, op => Assert.Equal("models/veo/operations/op-1", op));
        Assert.Equal("video/mp4", first.MimeType);
        Assert.Equal("video/mp4", second.MimeType);
        Assert.Equal(5, second.DurationSec);
    }

    [Fact]
    public async Task A_pre_recorded_operation_is_resumed_without_any_submit()
    {
        var store = new VeoOperationStore();
        store.Set(default, "ignored"); // unrelated entry must not interfere
        var assetId = Guid.NewGuid();
        store.Set(assetId, "models/veo/operations/already-running");
        var client = new FakeVeoClient(submitOpName: "models/veo/operations/should-not-be-used");
        var generator = Generator(client, store);

        var result = await generator.GenerateAsync(_videoBrief, seedImage: null, assetId);

        Assert.Equal(0, client.SubmitCount); // the op already existed → never submit (audit-#1)
        Assert.All(client.PolledOperations, op => Assert.Equal("models/veo/operations/already-running", op));
        Assert.Equal("video/mp4", result.MimeType);
    }

    [Fact]
    public async Task An_operation_that_never_finishes_within_the_poll_timeout_throws()
    {
        var store = new VeoOperationStore();
        var client = new FakeVeoClient(neverCompletes: true);
        var options = new VeoOptions
        {
            Model = "veo-test",
            MaxDurationSec = 5,
            PollTimeout = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(5),
        };
        var generator = Generator(client, store, options);

        await Assert.ThrowsAsync<VeoGenerationException>(
            () => generator.GenerateAsync(_videoBrief, seedImage: null, Guid.NewGuid()));

        Assert.Equal(1, client.SubmitCount); // submitted once; the timeout is in the poll loop, not a re-submit
    }

    [Fact]
    public async Task A_terminal_operation_failure_throws()
    {
        var store = new VeoOperationStore();
        var client = new FakeVeoClient(failTerminally: true);
        var generator = Generator(client, store);

        await Assert.ThrowsAsync<VeoGenerationException>(
            () => generator.GenerateAsync(_videoBrief, seedImage: null, Guid.NewGuid()));
    }

    /// <summary>A fake Veo long-running-operation client: a submit counter + scriptable poll outcome.</summary>
    private sealed class FakeVeoClient : IVeoClient
    {
        private readonly string _submitOpName;
        private readonly bool _neverCompletes;
        private readonly bool _failTerminally;

        public FakeVeoClient(
            string submitOpName = "models/veo/operations/new", bool neverCompletes = false, bool failTerminally = false)
        {
            _submitOpName = submitOpName;
            _neverCompletes = neverCompletes;
            _failTerminally = failTerminally;
        }

        public int SubmitCount { get; private set; }

        public List<string> PolledOperations { get; } = [];

        public Task<string> SubmitAsync(VeoSubmitRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(_submitOpName);
        }

        public Task<VeoOperation> PollAsync(string operationName, CancellationToken cancellationToken = default)
        {
            PolledOperations.Add(operationName);
            if (_failTerminally)
            {
                return Task.FromResult(new VeoOperation(VeoOperationStatus.Failed, Error: "safety-blocked"));
            }

            return Task.FromResult(_neverCompletes
                ? new VeoOperation(VeoOperationStatus.Pending)
                : new VeoOperation(VeoOperationStatus.Succeeded, DownloadUri: "https://veo.example/clip.mp4"));
        }

        public Task<byte[]> DownloadAsync(string downloadUri, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]>([0x00, 0x00, 0x00, 0x18]);
    }
}
