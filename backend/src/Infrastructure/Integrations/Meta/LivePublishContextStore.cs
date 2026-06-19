using System.Collections.Concurrent;
using Backend.Core.Domain;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Process-wide cache of the per-creation publish context (channel, target id, token, caption) that the
/// frozen <c>Poll</c>/<c>Publish(channel, creationId)</c> signatures do not carry (DL-055 live recovery
/// seam, DL-042). Registered as a <b>singleton</b> so it survives across the <b>transient</b>
/// <see cref="LiveMetaIntegration"/> instances a Hangfire retry resolves — the typed <c>HttpClient</c>
/// client is transient, so a per-instance map would be empty on every retry (e.g. after an Instagram
/// "container still processing" poll), losing the committed container forever. Only a true cross-process
/// worker restart loses this cache (the documented limit, flagged for the live smoke).
/// </summary>
public sealed class LivePublishContextStore
{
    private readonly ConcurrentDictionary<string, LivePublishContext> _contexts = new();

    /// <summary>Record the context captured at create time, keyed by the container/photo creation id.</summary>
    public void Set(string creationId, LivePublishContext context) => _contexts[creationId] = context;

    /// <summary>Recover the context for a committed creation id; false (and default) if not present.</summary>
    public bool TryGet(string creationId, out LivePublishContext context) =>
        _contexts.TryGetValue(creationId, out context);
}

/// <summary>The publish state poll/publish need but their <c>(channel, creationId)</c> signatures omit.</summary>
public readonly record struct LivePublishContext(PublishChannel Channel, string TargetId, string Token, string Caption);
