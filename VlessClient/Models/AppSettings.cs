using System.Text.Json.Serialization;

namespace VlessClient.Models;

public class AppSettings
{
    [JsonPropertyName("configs")]
    public List<VlessConfig> Configs { get; set; } = new();

    [JsonPropertyName("selectedIndex")]
    public int SelectedIndex { get; set; } = 0;

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; } = false;

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = false;

    [JsonPropertyName("enableTun")]
    public bool EnableTun { get; set; } = false;

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; } = 10808;

    [JsonPropertyName("shareOverLan")]
    public bool ShareOverLan { get; set; } = false;

    [JsonPropertyName("enableSystemProxy")]
    public bool EnableSystemProxy { get; set; } = false;
}
