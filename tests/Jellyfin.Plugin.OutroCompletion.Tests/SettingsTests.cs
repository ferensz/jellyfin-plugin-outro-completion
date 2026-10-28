using System.Text.Json;
using Jellyfin.Plugin.OutroCompletion.Configuration;

namespace Jellyfin.Plugin.OutroCompletion.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opencode", "outro-completion-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private readonly PluginConfiguration _configuration = new();

    [Fact]
    public void NewUsersInheritServerDefault()
    {
        var settings = CreateSettings();
        Assert.True(settings.GetForUser(_user).Enabled);
        Assert.False(settings.GetForUser(_user).RequireNextEpisode);
        _configuration.DefaultRequireNextEpisode = true;
        Assert.True(settings.GetForUser(_user).RequireNextEpisode);
    }

    [Fact]
    public void UserOverrideSurvivesServerDefaultChangesAndReload()
    {
        var settings = CreateSettings();
        settings.SetPreference(_user, new UserPreference { RequireNextEpisode = false });
        _configuration.DefaultRequireNextEpisode = true;
        var reloaded = CreateSettings();
        Assert.False(reloaded.GetForUser(_user).RequireNextEpisode);
        Assert.True(reloaded.GetForUser(Guid.NewGuid()).RequireNextEpisode);
    }

    [Fact]
    public void NullOverrideRestoresInheritance()
    {
        var settings = CreateSettings();
        settings.SetPreference(_user, new UserPreference { RequireNextEpisode = true });
        settings.SetPreference(_user, new UserPreference { RequireNextEpisode = null });
        Assert.False(settings.GetForUser(_user).RequireNextEpisode);
        _configuration.DefaultRequireNextEpisode = true;
        Assert.True(settings.GetForUser(_user).RequireNextEpisode);
    }

    [Fact]
    public void UserCanDisableButCannotOverrideServerDisable()
    {
        var settings = CreateSettings();
        settings.SetPreference(_user, new UserPreference { Enabled = false });
        Assert.False(settings.GetForUser(_user).Enabled);
        Assert.True(settings.GetForUser(Guid.NewGuid()).Enabled);
        _configuration.Enabled = false;
        Assert.False(settings.GetForUser(Guid.NewGuid()).Enabled);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 5)]
    [InlineData(120, 120)]
    [InlineData(10000, 600)]
    public void InvalidConfirmationWindowsAreClamped(int input, int expected)
    {
        _configuration.ConfirmationWindowSeconds = input;
        Assert.Equal(expected, CreateSettings().GetForUser(_user).ConfirmationWindow.TotalSeconds);
    }

    [Fact]
    public async Task ConcurrentPreferenceWritesDoNotLoseOtherUsers()
    {
        var settings = CreateSettings();
        var users = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(users.Select(user => Task.Run(() => settings.SetPreference(user, new UserPreference { RequireNextEpisode = true }))));
        var reloaded = CreateSettings();
        Assert.All(users, user => Assert.True(reloaded.GetForUser(user).RequireNextEpisode));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"00000000-0000-0000-0000-000000000000\":{}}")]
    public void CorruptPreferencesAreNotSilentlyOverwritten(string corrupt)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencePath, corrupt);
        var settings = CreateSettings();
        Assert.Throws<JsonException>(() => settings.GetForUser(_user));
        Assert.Throws<JsonException>(() => settings.SetPreference(_user, new UserPreference()));
        Assert.Equal(corrupt, File.ReadAllText(PreferencePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            // Only this test instance's newly-created files, never an existing shared path.
            foreach (var file in Directory.GetFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }

    private string PreferencePath => Path.Combine(_directory, "users.json");

    private SettingsService CreateSettings() => new(() => _configuration, new UserPreferenceStore(PreferencePath));
}
