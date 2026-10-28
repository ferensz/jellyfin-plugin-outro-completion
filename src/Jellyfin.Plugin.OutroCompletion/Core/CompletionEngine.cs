using System.Collections.Concurrent;

namespace Jellyfin.Plugin.OutroCompletion.Core;

/// <summary>
/// Serializes decisions, not playback. No timers, polling, seeks, or artificial playback reports.
/// Start observations also cover clients whose next-start report overtakes the previous stop.
/// </summary>
public sealed class CompletionEngine(
    IPlaybackRepository repository,
    ICompletionSettingsProvider settingsProvider,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReorderingGrace = TimeSpan.FromSeconds(5);
    private const int MaximumActors = 4096;
    private const int MaximumRecentStarts = 16;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Actor, ActorState> _actors = [];
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), DateTimeOffset> _invalidations = [];
    private long _sequence;

    public async Task OnStartedAsync(PlaybackReport report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(report.SessionId))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune();
            foreach (var userId in report.UserIds.Where(id => id != Guid.Empty).Distinct())
            {
                var state = GetState(new Actor(userId, report.SessionId, report.DeviceId));
                var episode = repository.ResolveEpisode(report, userId);
                var observation = new StartObservation(++_sequence, report, episode);
                // A duplicate start with a known play-session ID is not another transition.
                if (!string.IsNullOrEmpty(report.PlaySessionId)
                    && state.Starts.LastOrDefault() is { } latest
                    && SamePlayback(latest.Report, report))
                {
                    continue;
                }

                state.Starts.Add(observation);
                if (state.Starts.Count > MaximumRecentStarts)
                {
                    state.Starts.RemoveAt(0);
                }

                if (episode is not null)
                {
                    // A replay on ANY device must not have its resume point cleared by an older stop.
                    Invalidate(userId, episode.Id, report.OccurredAt);
                    Invalidate(userId, episode.ProgressItemId, report.OccurredAt);
                }

                if (state.Pending is not { } pending)
                {
                    continue;
                }

                // The first different playback is the only possible confirmation. A movie,
                // skipped episode, different series, or replay cancels rather than postpones it.
                state.Pending = null;
                if (Confirms(pending, observation) && CanComplete(pending, userId))
                {
                    repository.MarkCompleted(pending.Episode, userId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnStoppedAsync(PlaybackReport report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(report.SessionId) || report.PositionTicks is null or < 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune();
            foreach (var userId in report.UserIds.Where(id => id != Guid.Empty).Distinct())
            {
                var settings = settingsProvider.GetForUser(userId);
                if (!settings.Enabled)
                {
                    continue;
                }

                var episode = repository.ResolveEpisode(report, userId);
                if (episode is null)
                {
                    continue;
                }

                var state = GetState(new Actor(userId, report.SessionId, report.DeviceId));
                var anchor = state.Starts.LastOrDefault(start =>
                    start.Report.ItemId == report.ItemId
                    && start.Report.OccurredAt <= report.OccurredAt
                    && (string.IsNullOrEmpty(report.PlaySessionId) || SamePlayback(start.Report, report)));
                var stopKey = !string.IsNullOrEmpty(report.PlaySessionId)
                    ? $"{report.ItemId:N}:{report.PlaySessionId}:{anchor?.Sequence ?? 0}"
                    : $"{report.ItemId:N}:{report.MediaSourceId}:{anchor?.Sequence ?? 0}:{report.PositionTicks}";
                if (state.SeenStops.ContainsKey(stopKey))
                {
                    continue;
                }

                // Deduplicate before any writes, including a stop which does not qualify.
                state.SeenStops[stopKey] = timeProvider.GetUtcNow();
                var ranges = await repository.GetOutroRangesAsync(episode, cancellationToken).ConfigureAwait(false);
                if (!ranges.Any(range => range.Contains(report.PositionTicks.Value, episode.RunTimeTicks)))
                {
                    continue;
                }

                var pending = new PendingCompletion(
                    episode,
                    report,
                    settings.RequireNextEpisode ? repository.GetNextEpisodeId(episode, userId) : null,
                    report.OccurredAt + settings.ConfirmationWindow);
                if (!CanComplete(pending, userId))
                {
                    continue;
                }

                if (!settings.RequireNextEpisode)
                {
                    repository.MarkCompleted(episode, userId);
                    continue;
                }

                if (pending.NextEpisodeId is null)
                {
                    continue;
                }

                // Do not let a delayed older stop replace a newer pending episode.
                if (state.Pending is { } newer && newer.Report.OccurredAt > report.OccurredAt)
                {
                    continue;
                }

                var following = anchor is not null
                    ? state.Starts.FirstOrDefault(start => start.Sequence > anchor.Sequence)
                    : state.Starts.FirstOrDefault(start => start.Report.OccurredAt >= report.OccurredAt);
                if (following is not null)
                {
                    if (Confirms(pending, following) && CanComplete(pending, userId))
                    {
                        repository.MarkCompleted(episode, userId);
                    }
                }
                else
                {
                    state.Pending = pending;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called for explicit user-data edits, without waiting on the decision semaphore.</summary>
    public void Invalidate(Guid userId, Guid itemId, DateTimeOffset? at = null)
    {
        var timestamp = at ?? timeProvider.GetUtcNow();
        _invalidations.AddOrUpdate((userId, itemId), timestamp, (_, previous) => timestamp > previous ? timestamp : previous);
    }

    public void Dispose() => _gate.Dispose();

    private bool CanComplete(PendingCompletion pending, Guid userId) =>
        settingsProvider.GetForUser(userId).Enabled
        && timeProvider.GetUtcNow() <= pending.ExpiresAt
        && !IsInvalidated(userId, pending.Episode.Id, pending.Report.OccurredAt)
        && !IsInvalidated(userId, pending.Episode.ProgressItemId, pending.Report.OccurredAt);

    private bool IsInvalidated(Guid userId, Guid itemId, DateTimeOffset stoppedAt) =>
        _invalidations.TryGetValue((userId, itemId), out var changedAt) && changedAt > stoppedAt;

    private static bool Confirms(PendingCompletion pending, StartObservation start) =>
        start.Episode is { } episode
        && episode.Id == pending.NextEpisodeId
        && episode.SeriesId == pending.Episode.SeriesId
        && start.Report.OccurredAt >= pending.Report.OccurredAt - ReorderingGrace
        && start.Report.OccurredAt <= pending.ExpiresAt;

    private static bool SamePlayback(PlaybackReport left, PlaybackReport right) =>
        left.ItemId == right.ItemId
        && string.Equals(left.PlaySessionId, right.PlaySessionId, StringComparison.Ordinal);

    private ActorState GetState(Actor actor)
    {
        if (!_actors.TryGetValue(actor, out var state))
        {
            if (_actors.Count >= MaximumActors)
            {
                var oldest = _actors.MinBy(pair => pair.Value.LastTouched).Key;
                _actors.Remove(oldest);
            }

            state = new ActorState();
            _actors.Add(actor, state);
        }

        state.LastTouched = timeProvider.GetUtcNow();
        return state;
    }

    private void Prune()
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - Retention;
        foreach (var (actor, state) in _actors.ToArray())
        {
            if (state.LastTouched < cutoff)
            {
                _actors.Remove(actor);
                continue;
            }

            if (state.Pending?.ExpiresAt < now)
            {
                state.Pending = null;
            }

            state.Starts.RemoveAll(start => start.Report.OccurredAt < cutoff);
            foreach (var key in state.SeenStops.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            {
                state.SeenStops.Remove(key);
            }
        }

        foreach (var (key, changedAt) in _invalidations)
        {
            if (changedAt < cutoff)
            {
                _invalidations.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct Actor(Guid UserId, string SessionId, string DeviceId);

    private sealed class ActorState
    {
        public DateTimeOffset LastTouched { get; set; }

        public List<StartObservation> Starts { get; } = [];

        public Dictionary<string, DateTimeOffset> SeenStops { get; } = new(StringComparer.Ordinal);

        public PendingCompletion? Pending { get; set; }
    }

    private sealed record StartObservation(long Sequence, PlaybackReport Report, EpisodePlayback? Episode);

    private sealed record PendingCompletion(EpisodePlayback Episode, PlaybackReport Report, Guid? NextEpisodeId, DateTimeOffset ExpiresAt);
}
