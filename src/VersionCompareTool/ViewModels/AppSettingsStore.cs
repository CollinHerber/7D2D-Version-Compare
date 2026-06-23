using System.Text.Json;

namespace VersionCompareTool.ViewModels;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
    }

    public static AppSettingsStore CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.Combine(AppContext.BaseDirectory, ".settings")
            : localApplicationData;

        return new AppSettingsStore(Path.Combine(root, "7D2D-Version-Compare", "settings.json"));
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                }

                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch
        {
            // Settings persistence should never block launching or using the diff tool.
        }
    }
}
