using Backend.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Isolation;

/// <summary>
/// The mandatory two-brand RLS leakage gate (DL-002, DL-007). Proves brand
/// isolation is enforced by Postgres Row-Level Security plus the transaction-local
/// <c>set_config</c> binding — never by an application-layer WHERE clause. Every
/// case runs through the real <see cref="Backend.Infrastructure.Persistence.AppDbContext"/>
/// and the real <see cref="Backend.Core.Multitenancy.IBrandScope"/>, connected as a
/// non-superuser role that is fully subject to RLS.
/// </summary>
[Trait("Category", "Isolation")]
public sealed class RlsLeakageTests : IClassFixture<RlsLeakageFixture>
{
    private readonly RlsLeakageFixture _fixture;

    public RlsLeakageTests(RlsLeakageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Brand_A_context_sees_only_brand_A_rows()
    {
        var (db, scope) = _fixture.CreateBrandScopedContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            // No WHERE on brand_id anywhere — RLS does the filtering.
            var profiles = await db.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.NotEmpty(profiles);
            Assert.All(profiles, profile => Assert.Equal(_fixture.BrandA, profile.BrandId));

            // The new brand-scoped column (DL-034 R7) rides the same policy: Brand A sees only
            // its own pillars, never Brand B's. Covers ContentPillars under Category=Isolation.
            Assert.All(profiles, profile => Assert.Equal(["Origin", "Craft", "Ritual"], profile.ContentPillars));

            // A second table proves it is the policy, not a one-off.
            var runs = await db.AgentRuns.AsNoTracking().ToListAsync();
            Assert.NotEmpty(runs);
            Assert.All(runs, run => Assert.Equal(_fixture.BrandA, run.BrandId));
        }
    }

    [Fact]
    public async Task Brand_B_context_sees_only_brand_B_rows()
    {
        var (db, scope) = _fixture.CreateBrandScopedContext(_fixture.BrandB);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var profiles = await db.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.NotEmpty(profiles);
            Assert.All(profiles, profile => Assert.Equal(_fixture.BrandB, profile.BrandId));
        }
    }

    [Fact]
    public async Task Without_brand_context_zero_rows_are_visible()
    {
        // No scope opened: app.current_brand is unset, so the policy predicate is
        // NULL and the table yields zero rows — fail closed, never every brand's rows.
        await using var db = _fixture.CreateAppContext();

        var profiles = await db.BrandProfiles.AsNoTracking().ToListAsync();
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task Cross_brand_row_is_invisible_even_by_primary_key()
    {
        var (db, scope) = _fixture.CreateBrandScopedContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var ownRow = await db.BrandProfiles.AsNoTracking()
                .FirstOrDefaultAsync(profile => profile.Id == _fixture.ProfileA);
            Assert.NotNull(ownRow);

            // A direct primary-key lookup of Brand B's row must still come back empty.
            var foreignRow = await db.BrandProfiles.AsNoTracking()
                .FirstOrDefaultAsync(profile => profile.Id == _fixture.ProfileB);
            Assert.Null(foreignRow);
        }
    }

    [Fact]
    public async Task Audit_tables_are_brand_scoped_brand_A_sees_only_its_own_rows()
    {
        // The Phase-6 audit tables (DL-040) ride the same RLS policy as every brand-scoped table:
        // no WHERE on brand_id, RLS does the filtering. Brand A sees exactly its own audit rows.
        var (db, scope) = _fixture.CreateBrandScopedContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var approvals = await db.ApprovalActions.AsNoTracking().ToListAsync();
            Assert.NotEmpty(approvals);
            Assert.All(approvals, a => Assert.Equal(_fixture.BrandA, a.BrandId));

            var publishes = await db.PublishRecords.AsNoTracking().ToListAsync();
            Assert.NotEmpty(publishes);
            Assert.All(publishes, p => Assert.Equal(_fixture.BrandA, p.BrandId));
        }
    }

    [Fact]
    public async Task Audit_write_cannot_escape_the_brand_scope()
    {
        // Under Brand A's scope, a write that smuggles Brand B's id must be rejected by the policy's
        // WITH CHECK clause — isolation guards writes, not just reads.
        var (db, scope) = _fixture.CreateBrandScopedContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            db.PublishRecords.Add(new PublishRecord
            {
                Id = Guid.NewGuid(),
                BrandId = _fixture.BrandB,          // foreign brand — must not be writable from A's scope
                AgentRunId = Guid.NewGuid(),
                ContentItemId = Guid.NewGuid(),
                Status = PublishStatus.Published,
                ExternalRef = "mock://meta/escape",
                AttemptCount = 1,
                OccurredAt = DateTimeOffset.UtcNow,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Transaction_local_binding_does_not_bleed_across_pooled_connections()
    {
        // Brand A unit of work: sees only A.
        var (dbA, scopeA) = _fixture.CreateBrandScopedContext(_fixture.BrandA);
        await using (dbA)
        {
            await using var handleA = await scopeA.BeginAsync();
            var aRows = await dbA.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.NotEmpty(aRows);
            Assert.All(aRows, profile => Assert.Equal(_fixture.BrandA, profile.BrandId));
        }

        // Reusing the same pooled role connection with NO scope: had the binding been
        // session-scoped (set_config(..., false)) it would bleed Brand A here. Because
        // it is transaction-local it reset at commit/rollback — so zero rows.
        await using (var dbNone = _fixture.CreateAppContext())
        {
            var leaked = await dbNone.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.Empty(leaked);
        }

        // Brand B unit of work on the pooled connection: sees only B, never A.
        var (dbB, scopeB) = _fixture.CreateBrandScopedContext(_fixture.BrandB);
        await using (dbB)
        {
            await using var handleB = await scopeB.BeginAsync();
            var bRows = await dbB.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.NotEmpty(bRows);
            Assert.All(bRows, profile => Assert.Equal(_fixture.BrandB, profile.BrandId));
        }
    }
}
