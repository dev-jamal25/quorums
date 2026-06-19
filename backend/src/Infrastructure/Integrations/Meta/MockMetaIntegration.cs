using System.Collections.Concurrent;
using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Deterministic, network-free Meta publisher (DL-004, DL-038, DL-055). One channel-aware client that
/// models BOTH real two-step shapes — Instagram (media container → poll → publish) and Facebook Page
/// (unpublished photo → poll → feed post) — with in-memory state mirroring Meta's server-side
/// behaviour: a unique container/photo id per create (so a crashed create leaves a real orphan), and a
/// re-publish of an already-published container deduped to the SAME media id (no second post, DL-039).
/// The published media id is keyed on <c>(ContentItemId, Channel)</c>, so it is stable across retries
/// and DISTINCT per channel.
/// <para>For CI it exposes deterministic injection: both failure classes (returned typed, never thrown)
/// and one-shot crash points in the two durability windows the robust mechanism guards — after create
/// (before the component persists the <c>CreationId</c>) and after publish (before the component records
/// the <c>ExternalRef</c>). Crash points are addressable per channel (<see cref="CrashAfterCreateOnChannel"/>
/// / <see cref="CrashAfterPublishOnChannel"/>) so one channel can crash while the other completes, and
/// channel-agnostic (<see cref="CrashAfterCreateOnce"/> / <see cref="CrashAfterPublishOnce"/>) for the
/// single-channel proof. A crash throws AFTER the mock's side effect, exactly as a process death would.</para>
/// </summary>
public sealed class MockMetaIntegration : IMetaIntegration
{
    private readonly ConcurrentDictionary<string, ContainerState> _containers = new();   // creationId -> (contentItemId, channel)
    private readonly ConcurrentDictionary<string, byte> _published = new();               // creationIds that have been published

    // Deterministic test injection (defaults: clean two-step success on both channels).
    public PublishStatus? FailCreateWith { get; set; }
    public PublishStatus? FailPollWith { get; set; }
    public PublishStatus? FailPublishWith { get; set; }

    // Channel-agnostic one-shot crash points (single-channel proof).
    public bool CrashAfterCreateOnce { get; set; }
    public bool CrashAfterPublishOnce { get; set; }

    // Channel-targeted one-shot crash points (dual-channel proof: crash one channel, leave the other).
    public PublishChannel? CrashAfterCreateOnChannel { get; set; }
    public PublishChannel? CrashAfterPublishOnChannel { get; set; }

    /// <summary>Distinct published media across all channels — the "exactly one post" assertion.</summary>
    public int PublishedMediaCount => _published.Count;

    /// <summary>Distinct containers created across all channels — proves a crashed create leaves an orphan.</summary>
    public int ContainerCount => _containers.Count;

    /// <summary>Total publish-container calls regardless of outcome — pins the retry/attempt count.</summary>
    public int PublishAttemptCount => _publishAttempts;

    /// <summary>The last create request — lets a test assert the EFFECTIVE caption/hashtags (the overlay).</summary>
    public PublishRequest? LastRequest { get; private set; }

    private int _publishAttempts;

    /// <summary>Distinct published media on one channel — the per-channel "exactly one post" assertion.</summary>
    public int PublishedMediaCountFor(PublishChannel channel) =>
        _published.Keys.Count(id => _containers.TryGetValue(id, out var state) && state.Channel == channel);

    /// <summary>Distinct containers created on one channel — proves a crashed create left exactly one orphan.</summary>
    public int ContainerCountFor(PublishChannel channel) =>
        _containers.Values.Count(state => state.Channel == channel);

    public Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        var channel = request.Channel;

        if (FailCreateWith is { } failure)
        {
            return Task.FromResult(new ContainerResult(null, failure, $"mock create {failure}"));
        }

        // A real create returns a fresh container/photo id every call, so a crash before the CreationId
        // is persisted leaves a distinct orphan (never published). The prefix names the surface.
        var prefix = channel == PublishChannel.FacebookPage ? "mock-fb-photo" : "mock-ig-container";
        var creationId = $"{prefix}-{Guid.NewGuid():N}";
        _containers[creationId] = new ContainerState(request.ContentItemId, channel);

        if (ShouldCrashAfterCreate(channel))
        {
            throw new InvalidOperationException(
                $"Simulated crash after create on {channel}, before CreationId persisted.");
        }

        return Task.FromResult(new ContainerResult(creationId, null, null));
    }

    public Task<ContainerStatus> PollContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        if (FailPollWith is { } failure)
        {
            return Task.FromResult(new ContainerStatus(false, failure, $"mock poll {failure}"));
        }

        // Facebook photos are immediate-ready (no processing poll); Instagram containers are ready in
        // the mock. Either way: processed.
        return Task.FromResult(new ContainerStatus(true, null, null));
    }

    public Task<PublishResult> PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _publishAttempts);

        if (FailPublishWith is { } failure)
        {
            return Task.FromResult(new PublishResult(failure, null, $"mock publish {failure}", null));
        }

        if (!_containers.TryGetValue(creationId, out var state))
        {
            return Task.FromResult(new PublishResult(
                PublishStatus.TerminalFailure, null, $"unknown container '{creationId}'", null));
        }

        // The published media id is keyed on (contentItemId, channel), so it is stable across retries
        // and distinct per channel.
        var mediaId = DeterministicGuid.From(state.ContentItemId, $"{channel}:meta").ToString();
        var externalRef = $"mock://meta/{channel}/{mediaId}";

        // Idempotent: a re-publish of an already-published container is deduped — no second post.
        _published.TryAdd(creationId, 0);

        if (ShouldCrashAfterPublish(channel))
        {
            throw new InvalidOperationException(
                $"Simulated crash after publish on {channel}, before ExternalRef recorded.");
        }

        return Task.FromResult(new PublishResult(
            PublishStatus.Published, externalRef, null, new EngagementKeys(mediaId, null)));
    }

    // One-shot crash for the create→persist-CreationId window: fire (and consume) if the channel-agnostic
    // flag is set or the channel-targeted flag matches this channel.
    private bool ShouldCrashAfterCreate(PublishChannel channel)
    {
        if (CrashAfterCreateOnce)
        {
            CrashAfterCreateOnce = false;
            return true;
        }

        if (CrashAfterCreateOnChannel == channel)
        {
            CrashAfterCreateOnChannel = null;
            return true;
        }

        return false;
    }

    // One-shot crash for the publish→finalize window, same agnostic-or-targeted semantics.
    private bool ShouldCrashAfterPublish(PublishChannel channel)
    {
        if (CrashAfterPublishOnce)
        {
            CrashAfterPublishOnce = false;
            return true;
        }

        if (CrashAfterPublishOnChannel == channel)
        {
            CrashAfterPublishOnChannel = null;
            return true;
        }

        return false;
    }

    private readonly record struct ContainerState(Guid ContentItemId, PublishChannel Channel);
}
