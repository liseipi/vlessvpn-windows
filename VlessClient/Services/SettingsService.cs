using System.Text.Json;
using VlessClient.Models;

namespace VlessClient.Services;

public class SettingsService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VlessClient");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Settings { get; private set; } = new();

    public SettingsService() => Load();

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, _opts) ?? new();
            }
        }
        catch { Settings = new(); }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(Settings, _opts);
        await File.WriteAllTextAsync(ConfigPath, json);
    }

    public void Save() => SaveAsync().GetAwaiter().GetResult();
}
