// RlsLeakageTests.cs — two-brand RLS leakage test SCAFFOLD.
//
// Purpose: prove (DL-002 / DL-007) that brand isolation is enforced by Postgres
// Row-Level Security + the transaction-scoped set_config interceptor — NOT by an
// application-layer WHERE clause. This test MUST PASS before any feature work
// proceeds past Day 2 of the build order.
//
// Place this file in backend/tests/IntegrationTests/. Fill the marked TODOs with
// your real DbContext, entity, and IBrandContext types. It uses Testcontainers to
// spin up a disposable pgvector-enabled Postgres so the run is hermetic.
//
// Required test packages (add to the IntegrationTests project):
//   xunit, Testcontainers.PostgreSql, Microsoft.EntityFrameworkCore,
//   Npgsql.EntityFrameworkCore.PostgreSQL, Pgvector.EntityFrameworkCore
//
// Run via scripts/run-rls-leakage-test.sh (or `dotnet test`).

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class RlsLeakageTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        // Use a pgvector image so KnowledgeChunk vector columns are supported.
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    private string _connString = string.Empty;

    // Two brands seeded for the leakage proof.
    private static readonly Guid BrandA = Guid.NewGuid();
    private static readonly Guid BrandB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _connString = _pg.GetConnectionString();

        // TODO: build a DbContext against _connString, run EF migrations so the
        // RLS policies (created via migrationBuilder.Sql) are applied, then seed
        // one brand-scoped row for BrandA and one for BrandB in a table such as
        // BrandProfile or ContentItem.
        //
        // await using var ctx = CreateContext(brandId: BrandA);
        // await ctx.Database.MigrateAsync();
        // ... seed rows for BrandA and BrandB ...
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    // CreateContext must register the DbConnectionInterceptor that runs
    //   SELECT set_config('app.current_brand', @brandId, true)
    // on connection open, with @brandId taken from an IBrandContext bound to the
    // supplied brandId. The `true` (transaction-scoped) is the whole point: it is
    // connection-pool-safe and resets at commit.
    //
    // private AppDbContext CreateContext(Guid brandId) => /* TODO */ throw new NotImplementedException();

    [Fact]
    public async Task Brand_scoped_query_returns_only_current_brand_rows()
    {
        // ARRANGE: a context bound to BrandA.
        // await using var ctx = CreateContext(BrandA);

        // ACT: query the brand-scoped table with NO WHERE clause on brand_id.
        // var rows = await ctx.BrandProfiles.ToListAsync();

        // ASSERT: RLS filtered to BrandA only; BrandB rows are invisible.
        // Assert.NotEmpty(rows);
        // Assert.All(rows, r => Assert.Equal(BrandA, r.BrandId));

        Assert.True(false, "TODO: implement CreateContext + seeding, then enable asserts above.");
    }

    [Fact]
    public async Task Cross_brand_row_is_invisible_even_by_primary_key()
    {
        // ARRANGE: a context bound to BrandA; take a known BrandB row id.
        // ACT: attempt to load the BrandB row by id from the BrandA-bound context.
        // ASSERT: it is null — RLS hides it even on a direct key lookup.

        Assert.True(false, "TODO: assert a BrandB-owned row id returns null under a BrandA-bound context.");
    }

    [Fact]
    public async Task Session_variable_does_not_bleed_across_pooled_connections()
    {
        // Regression guard for the classic pitfall: set_config(..., false) would
        // leak the brand setting across pooled connections. With (..., true) it is
        // transaction-scoped and resets, so interleaved BrandA / BrandB contexts
        // must never observe each other's rows.

        Assert.True(false, "TODO: interleave BrandA and BrandB contexts; assert no cross-brand row is ever returned.");
    }

    // Optional but recommended (DL-009 / DL-011 isolation layers):
    // [Fact] storage path for an asset is brands/{brand_id}/... and never crosses.
    // [Fact] a BrandMetaConnection ciphertext for BrandB is not queryable under BrandA.
}
