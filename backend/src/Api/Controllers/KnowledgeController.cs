using System.Text.Json;
using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Persistence;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Controllers;

/// <summary>
/// Manager-editable brand-knowledge CRUD. Create/Update persist the doc under brand scope
/// then ENQUEUE a Hangfire ingest job (chunk → embed → upsert) — never inline, since
/// embedding is slow. Delete purges chunks + the doc synchronously in one transaction
/// (no FK cascade exists, so cleanup must be in-request). Brand comes from X-Brand-Id.
/// </summary>
[ApiController]
[Route("knowledge")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IBackgroundJobClient _jobs;
    private readonly IKnowledgeIngestService _ingest;

    public KnowledgeController(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IBackgroundJobClient jobs,
        IKnowledgeIngestService ingest)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _jobs = jobs;
        _ingest = ingest;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateKnowledgeDocResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateKnowledgeDocResponse>> Create(
        CreateKnowledgeDocRequest request,
        CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        var brandId = _brandContext.RequireBrandId();
        var now = DateTimeOffset.UtcNow;

        var doc = new KnowledgeDoc
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            DocType = request.DocType,
            Facet = request.Facet,
            Title = request.Title,
            Source = request.Source,
            Content = request.Content,
            Metadata = request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using (var handle = await _scope.BeginAsync(cancellationToken))
        {
            _db.KnowledgeDocs.Add(doc);
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);
        }

        _jobs.Enqueue<IngestKnowledgeDocJob>(job => job.ExecuteAsync(doc.Id, brandId, CancellationToken.None));

        return Accepted($"/knowledge/{doc.Id}", new CreateKnowledgeDocResponse(doc.Id));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(CreateKnowledgeDocResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreateKnowledgeDocResponse>> Update(
        Guid id,
        UpdateKnowledgeDocRequest request,
        CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        var brandId = _brandContext.RequireBrandId();

        await using (var handle = await _scope.BeginAsync(cancellationToken))
        {
            var doc = await _db.KnowledgeDocs.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (doc is null)
            {
                await handle.CompleteAsync(cancellationToken);
                return NotFound();
            }

            doc.DocType = request.DocType;
            doc.Facet = request.Facet;
            doc.Title = request.Title;
            doc.Source = request.Source;
            doc.Content = request.Content;
            doc.Metadata = request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata);
            doc.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);
        }

        // Re-ingest replaces this doc's chunks (idempotent upsert keyed by chunk id).
        _jobs.Enqueue<IngestKnowledgeDocJob>(job => job.ExecuteAsync(id, brandId, CancellationToken.None));

        return Accepted($"/knowledge/{id}", new CreateKnowledgeDocResponse(id));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var doc = await _db.KnowledgeDocs.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null)
        {
            await handle.CompleteAsync(cancellationToken);
            return NotFound();
        }

        // No FK cascade on knowledge_chunks (this repo avoids FKs; RLS is the relationship).
        // Purge chunks AND remove the doc in the SAME brand-scoped transaction — synchronous,
        // atomic, no orphan window. Ingest is async (it embeds); purge is a fast delete.
        await _ingest.PurgeAsync(id, cancellationToken);
        _db.KnowledgeDocs.Remove(doc);
        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);

        return NoContent();
    }
}
