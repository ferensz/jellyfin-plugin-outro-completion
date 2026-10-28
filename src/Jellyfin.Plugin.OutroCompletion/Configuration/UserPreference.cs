using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.OutroCompletion.Configuration;

public sealed record UserPreference
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>Null inherits the administrator's default.</summary>
    [JsonPropertyName("requireNextEpisode")]
    public bool? RequireNextEpisode { get; init; }
}
