using System.Net.Http.Json;
using Backend.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Backend.Infrastructure.Tracing;

/// <summary>
/// Langfuse-backed <see cref="ITrace"/>: assembles the trace locally (so the
/// checkpoint always carries the spans) and additionally posts each span to the
/// Langfuse ingestion API best-effort. A failed post is logged and swallowed —
/// tracing degrades, the run never fails (the Vault-style optional-dependency rule).
/// The typed <see cref="HttpClient"/> carries the base address, basic auth, and a
/// short timeout, configured at registration.
/// </summary>
public sealed partial class LangfuseTrace : ITrace
{
    private readonly HttpClient _http;
    private readonly ILogger<LangfuseTrace> _logger;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Langfuse span post failed for run {RunId}; trace degraded to local recording.")]
    private partial void LogSpanPostFailed(Guid runId, Exception exception);

    public LangfuseTrace(HttpClient http, ILogger<LangfuseTrace> logger)
    {
        _http = http;
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
        CancellationToken cancellationToken = default)
    {
        var refs = TraceAssembler.Append(current, node, tool, status, startedAt, endedAt, errorMessage);
        var span = refs.Spans[^1];

        try
        {
            // Minimal Langfuse ingestion batch: one span-create event per node/tool.
            var payload = new
            {
                batch = new object[]
                {
                    new
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
                            metadata = new { runId, brandId, node, tool },
                        },
                    },
                },
            };

            using var response = await _http
                .PostAsJsonAsync("api/public/ingestion", payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSpanPostFailed(runId, ex);
        }

        return refs;
    }
}
