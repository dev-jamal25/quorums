using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;

namespace Backend.Infrastructure.Jobs;

public sealed class ResumeRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ResumeRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
    }

    public Task ExecuteAsync(Guid runId, Guid brandId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("ResumeRunJob not yet implemented");
}
