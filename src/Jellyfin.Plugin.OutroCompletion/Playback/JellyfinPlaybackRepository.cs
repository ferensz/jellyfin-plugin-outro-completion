using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OutroCompletion.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OutroCompletion.Playback;

public sealed class JellyfinPlaybackRepository(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    IMediaSegmentManager segmentManager,
    ILogger<JellyfinPlaybackRepository> logger) : IPlaybackRepository
{
    public EpisodePlayback? ResolveEpisode(PlaybackReport report, Guid userId)
    {
        if (libraryManager.GetItemById<BaseItem>(report.ItemId, userId) is not Episode episode
            || episode.IsMissingEpisode
            || episode.ExtraType.HasValue)
        {
            return null;
        }

        // This mirrors SessionManager.GetProgressItem in Jellyfin 12.2. Only accept
        // a media-source GUID after verifying it really belongs to this video.
        Video progressItem = episode;
        if (Guid.TryParse(report.MediaSourceId, out var versionId) && versionId != episode.Id)
        {
            var version = episode.GetAlternateVersion(versionId);
            if (version is null)
            {
                // An unrecognized source must not borrow another version's timestamps.
                return null;
            }

            progressItem = version;
        }

        var seriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.FindSeriesId();
        return seriesId == Guid.Empty ? null : new EpisodePlayback(
            episode.PrimaryVersionId ?? episode.Id,
            progressItem.Id,
            seriesId,
            progressItem.RunTimeTicks);
    }

    public async Task<IReadOnlyList<OutroRange>> GetOutroRangesAsync(
        EpisodePlayback episode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = libraryManager.GetItemById(episode.ProgressItemId);
        if (item is null)
        {
            return [];
        }

        // Match the enabled providers for the played version's library. No direct DB access
        // and no fallback to the primary version, whose timestamps may differ.
        var segments = await segmentManager.GetSegmentsAsync(
            item,
            [MediaSegmentType.Outro],
            libraryManager.GetLibraryOptions(item),
            filterByProvider: true).ConfigureAwait(false);
        return segments.Where(segment => segment.Type == MediaSegmentType.Outro)
            .Select(segment => new OutroRange(segment.StartTicks, segment.EndTicks))
            .ToArray();
    }

    public Guid? GetNextEpisodeId(EpisodePlayback episode, Guid userId)
    {
        var user = userManager.GetUserById(userId);
        if (user is null || libraryManager.GetItemById(episode.SeriesId) is not Series series)
        {
            return null;
        }

        // This is the same ordering used by Jellyfin's /Shows/{id}/Episodes endpoint.
        // Unlike Next Up, it is not confused by the still-unwatched episode being fixed.
        var orderedIds = series.GetEpisodes(user, new DtoOptions(), shouldIncludeMissingEpisodes: false)
            .OfType<Episode>()
            .Where(item => !item.IsMissingEpisode && !item.ExtraType.HasValue)
            .Select(item => item.PrimaryVersionId ?? item.Id)
            .Distinct()
            .ToArray();
        var index = Array.IndexOf(orderedIds, episode.Id);
        return index >= 0 && index + 1 < orderedIds.Length ? orderedIds[index + 1] : null;
    }

    public void MarkCompleted(EpisodePlayback episode, Guid userId)
    {
        var user = userManager.GetUserById(userId);
        if (user is null || libraryManager.GetItemById(episode.ProgressItemId) is not Video video)
        {
            return;
        }

        var data = userDataManager.GetUserData(user, video);
        if (data is null || (data.Played && data.PlaybackPositionTicks == 0))
        {
            return;
        }

        // Only patch these two fields: never increment PlayCount or overwrite favorites,
        // stream selections, ratings, or LastPlayedDate already recorded by Jellyfin.
        userDataManager.SaveUserData(user, video, new UpdateUserItemDataDto
        {
            Played = true,
            PlaybackPositionTicks = 0
        }, UserDataSaveReason.PlaybackFinished);
        video.PropagatePlayedState(user, played: true, resetPosition: true);
        logger.LogInformation(
            "Completed episode {ItemId} (played version {VersionId}) for user {UserId} after an Outro stop",
            episode.Id, episode.ProgressItemId, userId);
    }
}
