namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// A Veo operation timed out (exceeded <c>Veo:PollTimeout</c>) or failed terminally (DL-058). Thrown by
/// <see cref="VeoVideoGenerator"/>; the Media node catches it and — because the run is video — degrades
/// to caption-only rather than failing the run (DL-022/023). Never propagated into the MAF graph.
/// </summary>
public sealed class VeoGenerationException : Exception
{
    public VeoGenerationException(string message)
        : base(message)
    {
    }

    public VeoGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
