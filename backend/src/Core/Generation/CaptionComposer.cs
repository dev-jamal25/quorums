namespace Backend.Core.Generation;

/// <summary>
/// Composes the single caption string published to Instagram / Facebook Page (DL-055). Neither channel
/// has a separate hashtags parameter — hashtags only become clickable when they live INSIDE the caption
/// text — so the wire caption is the caption, a blank line, then the space-joined (#-prefixed) hashtags.
/// Used by the publish node to build <c>PublishRequest.Caption</c> and by the gate validator to length-
/// check the COMBINED result against the caption cap (so a near-limit caption plus hashtags fails fast,
/// not at Meta).
/// </summary>
public static class CaptionComposer
{
    public static string Compose(string caption, IReadOnlyList<string>? hashtags)
    {
        caption ??= string.Empty;

        var tags = hashtags is null
            ? []
            : hashtags
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.StartsWith('#') ? h : $"#{h}")
                .ToList();

        if (tags.Count == 0)
        {
            return caption;
        }

        var joined = string.Join(' ', tags);
        return caption.Length == 0 ? joined : $"{caption}\n\n{joined}";
    }
}
