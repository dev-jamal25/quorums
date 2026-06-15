using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Multi-query expander backed by a Microsoft.Extensions.AI <see cref="IChatClient"/> (Anthropic/Haiku,
/// S0). The model id is config-bound (<see cref="QueryTransformOptions.Model"/>), seeded from the
/// current Haiku model at build time — never a literal here. This depends only on the
/// <c>IChatClient</c> abstraction (the single Claude-call path the generation slice reuses); the
/// concrete Anthropic client is wired in DI. A parse failure or short reply degrades to the single
/// original query (the caller pools it regardless); a transport failure throws and is caught into a
/// querytransform.failed ToolError by the caller (DL-022).
/// </summary>
public sealed class ChatQueryTransformer : IQueryTransformer
{
    private readonly IChatClient _chat;
    private readonly string _model;

    public ChatQueryTransformer(IChatClient chat, IOptions<QueryTransformOptions> qt)
    {
        _chat = chat;
        _model = qt.Value.Model;
    }

    public async Task<IReadOnlyList<string>> ExpandAsync(
        string query, int variants, CancellationToken cancellationToken = default)
    {
        var n = Math.Max(1, variants);
        var prompt =
            $"Rewrite the search query below as {n} alternative phrasings that surface the same intent " +
            $"with different vocabulary. Output exactly {n} lines, one phrasing per line, no numbering.\n\nQuery: {query}";

        var response = await _chat.GetResponseAsync(
            prompt,
            new ChatOptions { ModelId = _model, MaxOutputTokens = 256 },
            cancellationToken).ConfigureAwait(false);

        var variantsOut = response.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .Take(n)
            .ToList();

        return variantsOut.Count > 0 ? variantsOut : [query];   // defensive — never empty
    }
}
