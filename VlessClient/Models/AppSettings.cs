using System.Collections.Generic;
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

    [JsonPropertyName("httpPort")]
    public int HttpPort { get; set; } = 10809;

    [JsonPropertyName("tunAddress")]
    public string TunAddress { get; set; } = "198.18.0.1";

    [JsonPropertyName("tunPrefix")]
    public int TunPrefix { get; set; } = 15;

    [JsonPropertyName("tunDns")]
    public string TunDns { get; set; } = "1.1.1.1";
}
