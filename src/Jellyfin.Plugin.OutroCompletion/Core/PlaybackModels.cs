namespace Jellyfin.Plugin.OutroCompletion.Core;

/// <summary>An immutable snapshot: Jellyfin reuses and clears its mutable SessionInfo.</summary>
public sealed record PlaybackReport(
    Guid ItemId,
    IReadOnlyList<Guid> UserIds,
    string SessionId,
    string DeviceId,
    string? PlaySessionId,
    string? MediaSourceId,
    long? PositionTicks,
    DateTimeOffset OccurredAt);

/// <param name="Id">The primary, logical episode (for ordering and confirmation).</param>
/// <param name="ProgressItemId">The actual played version (for segments and resume state).</param>
/// <param name="SeriesId">The episode's Jellyfin series ID.</param>
/// <param name="RunTimeTicks">The played version's duration, if known.</param>
public sealed record EpisodePlayback(Guid Id, Guid ProgressItemId, Guid SeriesId, long? RunTimeTicks);

public readonly record struct OutroRange(long StartTicks, long EndTicks)
{
    public bool Contains(long positionTicks, long? runTimeTicks) =>
        StartTicks >= 0
        && EndTicks > StartTicks
        && (!runTimeTicks.HasValue || (runTimeTicks > 0 && EndTicks <= runTimeTicks))
        && positionTicks >= StartTicks
        && positionTicks <= EndTicks;
}

public sealed record CompletionSettings(bool Enabled, bool RequireNextEpisode, TimeSpan ConfirmationWindow);

public interface ICompletionSettingsProvider
{
    CompletionSettings GetForUser(Guid userId);
}

public interface IPlaybackRepository
{
    EpisodePlayback? ResolveEpisode(PlaybackReport report, Guid userId);

    Task<IReadOnlyList<OutroRange>> GetOutroRangesAsync(EpisodePlayback episode, CancellationToken cancellationToken);

    Guid? GetNextEpisodeId(EpisodePlayback episode, Guid userId);

    void MarkCompleted(EpisodePlayback episode, Guid userId);
}
