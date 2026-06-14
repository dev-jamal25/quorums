using Backend.Core.Orchestration;
using Backend.Infrastructure.Tracing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.IntegrationTests.Tracing;

/// <summary>
/// Task 5 / Step 4a: the Copywriting ∥ Media fork issues two <see cref="ITrace.RecordAsync"/>
/// calls in parallel, so the <em>production</em> tracer (not just the in-memory
/// <see cref="LocalTraceRecorder"/>) must survive concurrent writes. <see cref="LangfuseTrace"/>
/// is safe by construction — it captures no <c>DbContext</c> and no shared mutable field; it
/// folds via the pure <c>TraceAssembler</c> and posts through a thread-safe typed
/// <see cref="HttpClient"/>. These tests prove both branches succeed under concurrency, and that
/// a best-effort post failure degrades (never throws) even when the calls overlap.
/// </summary>
[Trait("Category", "Trace")]
public sealed class TraceConcurrencyTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;
        private int _calls;

        public StubHandler(Func<HttpResponseMessage> responder) => _responder = responder;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_responder());
        }
    }

    private static LangfuseTrace NewTracer(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://langfuse.test/") },
            NullLogger<LangfuseTrace>.Instance);

    /// <summary>Builds the shared post-creative trace both fork branches start from.</summary>
    private static async Task<TraceRefs> PostCreativeTraceAsync(Guid runId, Guid brandId)
    {
        var now = DateTimeOffset.UtcNow;
        var seed = new LocalTraceRecorder();
        var trace = await seed.RecordAsync(
            new TraceRefs(string.Empty, [], []), runId, brandId, "strategy", null, "ok", now, now, null);
        return await seed.RecordAsync(trace, runId, brandId, "creative", null, "ok", now, now, null);
    }

    [Fact]
    public async Task Production_tracer_records_concurrent_fork_branches_without_corruption()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var current = await PostCreativeTraceAsync(runId, brandId);
        var handler = new StubHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var tracer = NewTracer(handler);
        var now = DateTimeOffset.UtcNow;

        // Both branches fold off the SAME pre-fork snapshot, concurrently — exactly the fan-out.
        var results = await Task.WhenAll(
            tracer.RecordAsync(current, runId, brandId, "copywriting", null, "ok", now, now, null),
            tracer.RecordAsync(current, runId, brandId, "media", "minio.put", "ok", now, now, null));

        // Each branch produced an independent, intact trace: the two shared spans plus its own.
        Assert.All(results, r => Assert.Equal(3, r.Spans.Count));
        Assert.All(results, r => Assert.Equal(r.Spans.Count, r.SpanIds.Count));
        Assert.Contains(results, r => r.Spans.Any(s => s.Node == "copywriting"));
        Assert.Contains(results, r => r.Spans.Any(s => s.Node == "media" && s.Tool == "minio.put"));
        Assert.Equal(2, handler.Calls); // both spans posted
    }

    [Fact]
    public async Task Production_tracer_degrades_not_throws_when_langfuse_unreachable_under_concurrency()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var current = await PostCreativeTraceAsync(runId, brandId);
        var handler = new StubHandler(() => throw new HttpRequestException("langfuse unreachable"));
        var tracer = NewTracer(handler);
        var now = DateTimeOffset.UtcNow;

        // A failed best-effort post must be swallowed even when the two calls overlap.
        var results = await Task.WhenAll(
            tracer.RecordAsync(current, runId, brandId, "copywriting", null, "ok", now, now, null),
            tracer.RecordAsync(current, runId, brandId, "media", "minio.put", "ok", now, now, null));

        // The trace still assembled locally — the run never fails on a tracing outage.
        Assert.All(results, r => Assert.Equal(3, r.Spans.Count));
        Assert.Contains(results, r => r.Spans.Any(s => s.Node == "copywriting"));
        Assert.Contains(results, r => r.Spans.Any(s => s.Node == "media" && s.Tool == "minio.put"));
    }
}
