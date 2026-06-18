using Backend.Core.Domain;
using Xunit;

namespace Backend.UnitTests.Domain;

/// <summary>
/// The central run state-machine guard (DL-006, DL-036, DL-037). Proves the four new Phase-6 edges
/// are permitted, the regenerate back-edge (AwaitingApproval -> Running) specifically is allowed,
/// the pre-existing forward edges still pass (no regression from wiring the guard in), and a
/// representative illegal transition (Done -> Running) is still rejected.
/// </summary>
public sealed class RunStatusTransitionTests
{
    public static TheoryData<RunStatus, RunStatus> AllowedEdges() => new()
    {
        // Pre-existing edges the run pipeline already performed.
        { RunStatus.Queued, RunStatus.Running },
        { RunStatus.Running, RunStatus.AwaitingApproval },
        { RunStatus.Running, RunStatus.Failed },
        { RunStatus.AwaitingApproval, RunStatus.Publishing },
        { RunStatus.AwaitingApproval, RunStatus.Rejected },
        { RunStatus.Publishing, RunStatus.Done },
        { RunStatus.Publishing, RunStatus.Failed },          // terminal/exhausted publish failure (Slice 4)
        // New Phase-6 edges.
        { RunStatus.AwaitingApproval, RunStatus.Scheduled },
        { RunStatus.Scheduled, RunStatus.Publishing },
        { RunStatus.Scheduled, RunStatus.Cancelled },
        { RunStatus.AwaitingApproval, RunStatus.Running },   // regenerate back-edge (DL-036)
    };

    public static TheoryData<RunStatus, RunStatus> IllegalEdges() => new()
    {
        { RunStatus.Done, RunStatus.Running },               // terminal cannot reopen (the representative case)
        { RunStatus.Queued, RunStatus.Publishing },          // cannot skip the generation + gate
        { RunStatus.AwaitingApproval, RunStatus.Done },      // cannot bypass Publishing
        { RunStatus.Rejected, RunStatus.Running },           // terminal
        { RunStatus.Cancelled, RunStatus.Publishing },       // terminal
        { RunStatus.Publishing, RunStatus.Scheduled },       // no backward to a pre-gate state
    };

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void Allowed_edges_pass_the_guard(RunStatus from, RunStatus to)
    {
        Assert.True(RunStatusTransition.IsAllowed(from, to));

        // And the entity-level helper applies it without throwing, stamping UpdatedAt.
        var at = DateTimeOffset.UtcNow;
        var run = new AgentRun { Id = Guid.NewGuid(), BrandId = Guid.NewGuid(), Status = from };
        run.TransitionTo(to, at);
        Assert.Equal(to, run.Status);
        Assert.Equal(at, run.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public void Illegal_edges_are_rejected(RunStatus from, RunStatus to)
    {
        Assert.False(RunStatusTransition.IsAllowed(from, to));

        var run = new AgentRun { Id = Guid.NewGuid(), BrandId = Guid.NewGuid(), Status = from };
        var ex = Assert.Throws<InvalidRunStatusTransitionException>(() => run.TransitionTo(to, DateTimeOffset.UtcNow));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
        Assert.Equal(from, run.Status);   // unchanged on rejection
    }

    [Fact]
    public void Regenerate_back_edge_awaiting_to_running_is_allowed()
    {
        Assert.True(RunStatusTransition.IsAllowed(RunStatus.AwaitingApproval, RunStatus.Running));
    }

    [Fact]
    public void Identity_transition_is_a_no_op_re_set_not_a_throw()
    {
        // Preserves the idempotent ExecuteRun retry that re-sets Running on re-entry.
        var run = new AgentRun { Id = Guid.NewGuid(), BrandId = Guid.NewGuid(), Status = RunStatus.Running };
        run.TransitionTo(RunStatus.Running, DateTimeOffset.UtcNow);
        Assert.Equal(RunStatus.Running, run.Status);
    }
}
