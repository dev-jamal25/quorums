using System.Net.Http.Json;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Tracing;

/// <summary>
/// Langfuse-backed <see cref="ITrace"/>: assembles the trace locally (so the checkpoint always carries
/// the spans, surviving the durable ExecuteRun → ResumeRun seam) and additionally posts to the Langfuse
/// ingestion API best-effort. A failed post is logged and swallowed — tracing degrades, the run never
/// fails (the Vault-style optional-dependency rule, DL-040).
/// <para>Follows the Langfuse instrumentation baseline: the FIRST span of a run also emits a
/// <c>trace-create</c> so the trace is findable (a descriptive <c>name</c>), attributed to the brand
/// (<c>userId</c> — multi-tenant cost/quality breakdown), and tagged. Subsequent spans (incl. those
/// from the resumed publish segment, which reuse the persisted trace id) append to that trace. There is
/// deliberately no <c>sessionId</c>: a run is one trace, not a multi-turn conversation — only the
/// fields that fit this app are added. Content is sent unless <see cref="LangfuseOptions.MaskContent"/>.</para>
/// </summary>
public sealed partial class LangfuseTrace : ITrace
{
    private const string TraceName = "content-generation";

    private readonly HttpClient _http;
    private readonly LangfuseOptions _options;
    private readonly ILogger<LangfuseTrace> _logger;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Langfuse span post failed for run {RunId}; trace degraded to local recording.")]
    private partial void LogSpanPostFailed(Guid runId, Exception exception);

    public LangfuseTrace(HttpClient http, IOptions<LangfuseOptions> options, ILogger<LangfuseTrace> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TraceRefs> RecordAsync(
        TraceRefs current,
        Guid runId,
        Guid brandId,
        string node,
        string? tool,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? errorMessage,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var isNewTrace = string.IsNullOrEmpty(current.TraceId);
        var refs = TraceAssembler.Append(current, runId, node, tool, status, startedAt, endedAt, errorMessage, detail);
        var span = refs.Spans[^1];

        try
        {
            var batch = new List<object>();

            // Create the trace once, on its first span: a findable name, the brand as the user (so cost
            // and quality break down per tenant), run metadata, and tags.
            if (isNewTrace)
            {
                batch.Add(new
                {
                    id = Guid.NewGuid().ToString("N"),
                    type = "trace-create",
                    timestamp = startedAt,
                    body = new
                    {
                        id = refs.TraceId,
                        timestamp = startedAt,
                        name = TraceName,
                        userId = brandId.ToString(),
                        tags = Tags(),
                        metadata = new { runId, brandId },
                    },
                });
            }

            batch.Add(new
            {
                id = span.SpanId,
                type = "span-create",
                timestamp = endedAt,
                body = new
                {
                    id = span.SpanId,
                    traceId = refs.TraceId,
                    name = tool is null ? node : $"{node}:{tool}",
                    startTime = startedAt,
                    endTime = endedAt,
                    level = errorMessage is null ? "DEFAULT" : "ERROR",
                    statusMessage = errorMessage ?? status,
                    metadata = new { runId, brandId, node, tool, detail = _options.MaskContent ? null : detail },
                },
            });

            using var response = await _http
                .PostAsJsonAsync("api/public/ingestion", new { batch }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSpanPostFailed(runId, ex);
        }

        return refs;
    }

    private string[] Tags() =>
        string.IsNullOrWhiteSpace(_options.Environment)
            ? [TraceName]
            : [TraceName, _options.Environment];

    public async Task RecordGenerationAsync(
        Guid runId,
        Guid brandId,
        string name,
        string? model,
        long? inputTokens,
        long? outputTokens,
        string? input,
        string? output,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // One generation observation on the run's trace (trace id derived from the run id, so it
            // matches the spans). model + usage let Langfuse compute cost automatically.
            var generation = new
            {
                id = Guid.NewGuid().ToString("N"),
                type = "generation-create",
                timestamp = endedAt,
                body = new
                {
                    id = Guid.NewGuid().ToString("N"),
                    traceId = TraceAssembler.TraceId(runId),
                    name,
                    startTime = startedAt,
                    endTime = endedAt,
                    model,
                    usage = new { input = inputTokens, output = outputTokens, unit = "TOKENS" },
                    input = _options.MaskContent ? null : input,
                    output = _options.MaskContent ? null : output,
                    metadata = new { runId, brandId },
                },
            };

            using var response = await _http
                .PostAsJsonAsync("api/public/ingestion", new { batch = new[] { generation } }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSpanPostFailed(runId, ex);
        }
    }
}
