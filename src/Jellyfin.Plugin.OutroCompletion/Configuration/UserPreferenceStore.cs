using System.Text.Json;

namespace Jellyfin.Plugin.OutroCompletion.Configuration;

/// <summary>
/// User settings are separate from the administrator's XML configuration so dashboard
/// saves cannot overwrite them. Load failures are surfaced, never silently replaced.
/// </summary>
public sealed class UserPreferenceStore(string path)
{
    private readonly object _sync = new();
    private Dictionary<Guid, UserPreference>? _preferences;

    public UserPreference Get(Guid userId)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _preferences!.GetValueOrDefault(userId) ?? new UserPreference();
        }
    }

    public void Set(Guid userId, UserPreference preference)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A real user ID is required.", nameof(userId));
        }

        lock (_sync)
        {
            EnsureLoaded();
            var updated = new Dictionary<Guid, UserPreference>(_preferences!) { [userId] = preference };
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            // The temporary file is in the same directory for an atomic same-filesystem rename.
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, updated);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
                _preferences = updated;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_preferences is not null)
        {
            return;
        }

        FileStream stream;
        try
        {
            stream = File.OpenRead(path);
        }
        catch (FileNotFoundException)
        {
            _preferences = [];
            return;
        }
        catch (DirectoryNotFoundException)
        {
            _preferences = [];
            return;
        }

        using (stream)
        {
            _preferences = JsonSerializer.Deserialize<Dictionary<Guid, UserPreference>>(stream)
                ?? throw new JsonException("The user preference file cannot contain null.");
        }
        if (_preferences.Any(pair => pair.Key == Guid.Empty || pair.Value is null))
        {
            _preferences = null;
            throw new JsonException("The user preference file contains an invalid user or preference.");
        }
    }
}
