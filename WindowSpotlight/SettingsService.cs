using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace WindowSpotlight;

internal sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowSpotlight",
            "settings.json");
    }

    public PersistedSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PersistedSettings();
            }

            var settings = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                           ?? new PersistedSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PersistedSettings();
        }
    }

    public void Save(PersistedSettings settings)
    {
        settings.Normalize();
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
    }
}
