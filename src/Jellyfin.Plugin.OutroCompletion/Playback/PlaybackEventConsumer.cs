using Jellyfin.Plugin.OutroCompletion.Core;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OutroCompletion.Playback;

public sealed class PlaybackEventConsumer(
    CompletionEngine engine,
    TimeProvider timeProvider,
    ILogger<PlaybackEventConsumer> logger)
    : IEventConsumer<PlaybackStartEventArgs>, IEventConsumer<PlaybackStopEventArgs>
{
    public Task OnEvent(PlaybackStartEventArgs eventArgs) => HandleAsync(Snapshot(eventArgs), stopped: false);

    public Task OnEvent(PlaybackStopEventArgs eventArgs) => HandleAsync(Snapshot(eventArgs), stopped: true);

    private PlaybackReport Snapshot(PlaybackProgressEventArgs args) => new(
        args.Item?.Id ?? Guid.Empty,
        args.Users.Select(user => user.Id).Distinct().ToArray(),
        args.Session?.Id ?? string.Empty,
        args.DeviceId ?? string.Empty,
        args.PlaySessionId,
        args.MediaSourceId,
        args.PlaybackPositionTicks,
        timeProvider.GetUtcNow());

    private async Task HandleAsync(PlaybackReport report, bool stopped)
    {
        try
        {
            if (stopped)
            {
                await engine.OnStoppedAsync(report).ConfigureAwait(false);
            }
            else
            {
                await engine.OnStartedAsync(report).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // A configuration, provider, or DB error must never fail the playback request.
            logger.LogError(exception, "Outro Completion could not process playback for item {ItemId}", report.ItemId);
        }
    }
}
