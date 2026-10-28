using Jellyfin.Plugin.OutroCompletion.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OutroCompletion;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin Instance { get; private set; } = null!;

    public static Guid PluginId { get; } = new("0c220e70-bcdf-4af5-83a3-36f656694392");

    public override Guid Id => PluginId;

    public override string Name => "Outro Completion";

    public override string Description => "Mark episodes watched when stopped in credits, optionally after the next episode starts.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "outro-completion",
            EmbeddedResourcePath = "Jellyfin.Plugin.OutroCompletion.Web.config.html"
        };
    }
}
