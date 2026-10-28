using Jellyfin.Plugin.OutroCompletion.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.OutroCompletion.Playback;

public sealed class UserDataInvalidationService(IUserDataManager userDataManager, CompletionEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs args)
    {
        if (args.SaveReason is UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserData or UserDataSaveReason.Import)
        {
            engine.Invalidate(args.UserId, args.Item.Id);
        }
    }
}
