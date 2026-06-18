using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Xunit;

namespace Backend.UnitTests.Orchestration;

/// <summary>
/// The keystone Slice-6 proof (DL-041): the server-computed available-actions list is correct for
/// EACH run status, and is the single source the gate endpoints share. AwaitingApproval exposes
/// approve/reject always and regenerate only under the per-run bound; Scheduled exposes only cancel;
/// every in-flight or terminal status exposes nothing.
/// </summary>
public sealed class GateActionPolicyTests
{
    private const int Max = 3;

    [Fact]
    public void Awaiting_approval_under_the_bound_offers_approve_regenerate_reject()
    {
        var actions = GateActionPolicy.Available(RunStatus.AwaitingApproval, regenerateCount: 0, Max);

        Assert.Equal(new[] { GateAction.Approve, GateAction.Regenerate, GateAction.Reject }, actions);
        Assert.Equal(3, GateActionPolicy.RegenerateRemaining(0, Max));
    }

    [Fact]
    public void Awaiting_approval_at_the_bound_drops_regenerate()
    {
        var actions = GateActionPolicy.Available(RunStatus.AwaitingApproval, regenerateCount: Max, Max);

        Assert.Equal(new[] { GateAction.Approve, GateAction.Reject }, actions);
        Assert.DoesNotContain(GateAction.Regenerate, actions);
        Assert.False(GateActionPolicy.RegenerateAllowed(RunStatus.AwaitingApproval, Max, Max));
        Assert.Equal(0, GateActionPolicy.RegenerateRemaining(Max, Max));
    }

    [Fact]
    public void Scheduled_offers_only_cancel()
    {
        var actions = GateActionPolicy.Available(RunStatus.Scheduled, regenerateCount: 0, Max);

        Assert.Equal(new[] { GateAction.Cancel }, actions);
        Assert.True(GateActionPolicy.Allows(GateAction.Cancel, RunStatus.Scheduled, 0, 0));
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Publishing)]
    [InlineData(RunStatus.Done)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Rejected)]
    [InlineData(RunStatus.Cancelled)]
    public void In_flight_and_terminal_statuses_offer_nothing(RunStatus status)
    {
        Assert.Empty(GateActionPolicy.Available(status, regenerateCount: 0, Max));
        Assert.False(GateActionPolicy.Allows(GateAction.Approve, status, 0, Max));
        Assert.False(GateActionPolicy.Allows(GateAction.Cancel, status, 0, Max));
    }

    [Fact]
    public void Cancel_is_never_offered_at_the_gate()
    {
        // Cancel acts only on a Scheduled run (a separate endpoint), never as a gate decision.
        Assert.DoesNotContain(GateAction.Cancel, GateActionPolicy.Available(RunStatus.AwaitingApproval, 0, Max));
    }
}
