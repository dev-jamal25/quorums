namespace Backend.Core.Multitenancy;

/// <summary>
/// Opens a brand-scoped unit of work: an explicit database transaction whose
/// FIRST statement binds <c>app.current_brand</c> with a transaction-local
/// <c>set_config(..., true)</c>. Every query/command issued on the scoped
/// <c>DbContext</c> until the handle completes is constrained to that brand by
/// Postgres RLS. The binding resets when the transaction commits or rolls back,
/// so it can never bleed across pooled connections (DL-007).
/// </summary>
/// <remarks>
/// The binding MUST NOT be issued on connection-open: a transaction-local GUC set
/// there commits in its own implicit transaction and does not apply to later
/// queries — a silent no-op that yields zero rows or unscoped behaviour.
/// </remarks>
public interface IBrandScope
{
    /// <summary>Begins the unit of work and binds the current brand as the first statement.</summary>
    Task<IBrandScopeHandle> BeginAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Handle for an open brand scope. Call <see cref="CompleteAsync"/> to commit
/// pending writes; disposing without completing rolls the unit of work back.
/// Either way the brand binding is reset.
/// </summary>
public interface IBrandScopeHandle : IAsyncDisposable
{
    /// <summary>Commits the unit of work.</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}
