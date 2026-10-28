using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OutroCompletion.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool DefaultRequireNextEpisode { get; set; }

    public int ConfirmationWindowSeconds { get; set; } = 120;
}
