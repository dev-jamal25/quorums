using Backend.Core.Multitenancy;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Backend.Infrastructure.Multitenancy;

/// <summary>
/// Binds the current brand to Postgres for a unit of work. Opens an explicit
/// transaction on the scoped <see cref="AppDbContext"/> and, as its FIRST statement,
/// runs <c>set_config('app.current_brand', @brandId, true)</c>. Because the binding
/// is transaction-local it applies to every query in the unit and resets at
/// commit/rollback — it can never bleed across pooled connections (DL-007).
/// </summary>
internal sealed class BrandScope : IBrandScope
{
    private readonly AppDbContext _dbContext;
    private readonly IBrandContext _brandContext;

    public BrandScope(AppDbContext dbContext, IBrandContext brandContext)
    {
        _dbContext = dbContext;
        _brandContext = brandContext;
    }

    public async Task<IBrandScopeHandle> BeginAsync(CancellationToken cancellationToken = default)
    {
        var brandId = _brandContext.RequireBrandId();

        var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // FIRST statement inside the open transaction. The is_local flag (true)
            // makes the GUC transaction-local; issuing it on connection-open instead
            // would commit in its own implicit transaction and silently no-op.
            await _dbContext.Database
                .ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('app.current_brand', {brandId.ToString()}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new BrandScopeHandle(transaction);
    }

    private sealed class BrandScopeHandle : IBrandScopeHandle
    {
        private readonly IDbContextTransaction _transaction;
        private bool _completed;

        public BrandScopeHandle(IDbContextTransaction transaction) => _transaction = transaction;

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            // A scope disposed without CompleteAsync (a read-only unit, or an error)
            // rolls back. Either path resets the transaction-local brand binding.
            if (!_completed)
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
