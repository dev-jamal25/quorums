using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Infrastructure.Persistence;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Persists an <see cref="EvalRun"/> + its <see cref="EvalResultRow"/>s to the RLS-scoped run store
/// (DL-051/052). The write runs inside an explicit brand scope: <see cref="IBrandScope.BeginAsync"/>
/// issues the transaction-local <c>set_config('app.current_brand', …, true)</c> as the first statement,
/// so the policy's WITH CHECK guards every row — never a manual <c>WHERE brand_id</c>.
/// </summary>
public sealed class EvalRunPersistence
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;

    public EvalRunPersistence(AppDbContext db, IBrandScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task PersistAsync(EvalRun run, IReadOnlyList<EvalResultRow> results, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(results);

        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);

        _db.EvalRuns.Add(run);
        _db.EvalResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
