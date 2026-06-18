namespace Backend.Core.Domain;

/// <summary>
/// The classified outcome of a publish attempt (DL-038, DL-039). The publish path classifies from
/// this typed value, never by exception-sniffing: <see cref="TransientFailure"/> drives a bounded
/// Hangfire retry; <see cref="TerminalFailure"/> fails the run with the reason surfaced. Persisted
/// as text on <see cref="PublishRecord"/>; members are append-only.
/// </summary>
public enum PublishStatus
{
    Published,
    TransientFailure,
    TerminalFailure,
}
