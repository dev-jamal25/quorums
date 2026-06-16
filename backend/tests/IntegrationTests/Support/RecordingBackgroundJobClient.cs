using System.Globalization;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// Records Hangfire dispatch so a gate-endpoint test can assert the intent without a running server:
/// <see cref="IBackgroundJobClient.Create"/> backs the <c>Enqueue</c>/<c>Schedule</c> extensions
/// (distinguished by the queued <see cref="IState"/>) and returns a deterministic id;
/// <see cref="IBackgroundJobClient.ChangeState"/> backs <c>Delete</c> (a <see cref="DeletedState"/>).
/// </summary>
public sealed class RecordingBackgroundJobClient : IBackgroundJobClient
{
    private int _sequence;

    public List<(Job Job, IState State)> Created { get; } = [];

    public List<(string JobId, IState State)> StateChanges { get; } = [];

    public int EnqueueCount => Created.Count(c => c.State is EnqueuedState);

    public int ScheduleCount => Created.Count(c => c.State is ScheduledState);

    public IReadOnlyList<string> DeletedJobIds =>
        StateChanges.Where(s => s.State is DeletedState).Select(s => s.JobId).ToList();

    public string Create(Job job, IState state)
    {
        var id = (++_sequence).ToString(CultureInfo.InvariantCulture);
        Created.Add((job, state));
        return id;
    }

    public bool ChangeState(string jobId, IState state, string expectedState)
    {
        StateChanges.Add((jobId, state));
        return true;
    }
}
