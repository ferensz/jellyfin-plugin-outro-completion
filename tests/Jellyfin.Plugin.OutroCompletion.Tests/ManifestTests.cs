using System.Text.Json;
using MediaBrowser.Model.Updates;

namespace Jellyfin.Plugin.OutroCompletion.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void ManifestDeserializesUsingJellyfinsActualPackageModel()
    {
        var packages = JsonSerializer.Deserialize<PackageInfo[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
        var package = Assert.Single(packages!);
        Assert.Equal(Plugin.PluginId, package.Id);
        Assert.Equal("Outro Completion", package.Name);
        Assert.Equal("ferensz", package.Owner);
        Assert.NotEmpty(package.Description);
        Assert.NotEmpty(package.Overview);
        Assert.NotNull(package.Versions);
    }

    [Fact]
    public void PublishedVersionsHaveValidCompatibilityChecksumsAndReleaseUrls()
    {
        var packages = JsonSerializer.Deserialize<PackageInfo[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
        var versions = Assert.Single(packages!).Versions;
        Assert.Equal(versions.Count, versions.Select(version => version.VersionNumber).Distinct().Count());
        Assert.Equal(versions.OrderByDescending(version => version.VersionNumber), versions);
        Assert.All(versions, version =>
        {
            Assert.True(Version.TryParse(version.TargetAbi, out var abi));
            Assert.True(abi >= new Version(12, 2, 0, 0));
            Assert.Matches("^[a-fA-F0-9]{32}$", version.Checksum!);
            Assert.StartsWith("https://github.com/ferensz/jellyfin-plugin-outro-completion/releases/download/v", version.SourceUrl);
            Assert.EndsWith($"/OutroCompletion_{version.Version}.zip", version.SourceUrl);
            Assert.True(DateTimeOffset.TryParse(version.Timestamp, out _));
        });
    }
}
