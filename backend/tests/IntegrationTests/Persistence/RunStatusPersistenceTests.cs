using Backend.Core.Domain;
using Backend.IntegrationTests.Durability;
using Npgsql;
using Xunit;

namespace Backend.IntegrationTests.Persistence;

/// <summary>
/// RunStatus enum-persistence round-trip (DL-037). Proves the two appended members (Scheduled,
/// Cancelled) persist and re-read, and that EVERY pre-existing value still stores under its original
/// representation — the member NAME, since <c>RunStatus</c> is mapped <c>HasConversion&lt;string&gt;()</c>.
/// Reading the raw column guards against an accidental renumber/rename that would silently break
/// existing rows.
/// </summary>
public sealed class RunStatusPersistenceTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public RunStatusPersistenceTests(DurabilityFixture fixture) => _fixture = fixture;

    public static TheoryData<RunStatus> AllStatuses() => new()
    {
        RunStatus.Queued,
        RunStatus.Running,
        RunStatus.AwaitingApproval,
        RunStatus.Publishing,
        RunStatus.Done,
        RunStatus.Failed,
        RunStatus.Rejected,
        RunStatus.Scheduled,
        RunStatus.Cancelled,
    };

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task Run_status_round_trips_and_stores_under_its_member_name(RunStatus status)
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, status);

        // Re-read through EF under Brand A's RLS scope: the enum survives the round-trip.
        var readBack = await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA);
        Assert.Equal(status, readBack);

        // The raw persisted representation is the member NAME, not an int — catches a renumber/rename.
        await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM agent_runs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", runId);
        var stored = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal(status.ToString(), stored);
    }

    [Fact]
    public async Task Appended_scheduled_and_cancelled_persist_and_reread()
    {
        var scheduled = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Scheduled);
        var cancelled = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Cancelled);

        Assert.Equal(RunStatus.Scheduled, await _fixture.ReadRunStatusAsync(scheduled, _fixture.BrandA));
        Assert.Equal(RunStatus.Cancelled, await _fixture.ReadRunStatusAsync(cancelled, _fixture.BrandA));
    }
}
