using System.Text.Json;

namespace Backend.Core.Evaluation;

/// <summary>
/// One labelled dataset row (DL-047, deck S22). Loaded from versioned JSON under
/// <c>eval/datasets/&lt;brandId&gt;/&lt;name&gt;.json</c>. <see cref="Input"/> is what the system
/// receives (a query or a brief) and <see cref="Expected"/> is what good looks like (e.g. relevant
/// chunk ids) — both kept as raw <see cref="JsonElement"/> so the schema is owned by the dataset, not
/// a C# DTO that would couple every evaluator to one shape. The provenance fields support the
/// hand-labelled, hold-out discipline (a human writes <c>expected</c>).
/// </summary>
public sealed record EvalCase(
    string Id,
    JsonElement Input,
    JsonElement Expected,
    IReadOnlyList<string> Tags,
    string? Notes,
    string? AddedBy,
    string? AddedAt);
