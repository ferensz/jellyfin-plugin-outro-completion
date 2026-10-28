using Jellyfin.Plugin.OutroCompletion.Core;

namespace Jellyfin.Plugin.OutroCompletion.Configuration;

public sealed class SettingsService(Func<PluginConfiguration> getConfiguration, UserPreferenceStore preferences)
    : ICompletionSettingsProvider
{
    public CompletionSettings GetForUser(Guid userId)
    {
        var configuration = getConfiguration();
        var preference = preferences.Get(userId);
        return new CompletionSettings(
            configuration.Enabled && preference.Enabled,
            preference.RequireNextEpisode ?? configuration.DefaultRequireNextEpisode,
            TimeSpan.FromSeconds(Math.Clamp(configuration.ConfirmationWindowSeconds, 5, 600)));
    }

    public UserPreference GetPreference(Guid userId) => preferences.Get(userId);

    public void SetPreference(Guid userId, UserPreference preference) => preferences.Set(userId, preference);

    public bool ServerEnabled => getConfiguration().Enabled;

    public bool DefaultRequireNextEpisode => getConfiguration().DefaultRequireNextEpisode;
}
