using System.Text.Json.Serialization;
using System.Web;

namespace VlessClient.Models;

/// <summary>
/// VLESS 连接配置，支持从 vless:// URI 解析
/// </summary>
public class VlessConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "未命名配置";

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 443;

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("sni")]
    public string Sni { get; set; } = "";

    [JsonPropertyName("wsHost")]
    public string WsHost { get; set; } = "";

    [JsonPropertyName("security")]
    public string Security { get; set; } = "tls";

    [JsonPropertyName("encryption")]
    public string Encryption { get; set; } = "none";

    [JsonPropertyName("network")]
    public string Network { get; set; } = "ws";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; } = 10808;

    [JsonPropertyName("rejectUnauthorized")]
    public bool RejectUnauthorized { get; set; } = true;

    [JsonPropertyName("enableTun")]
    public bool EnableTun { get; set; } = false;

    [JsonPropertyName("tunAddress")]
    public string TunAddress { get; set; } = "198.18.0.1";

    [JsonPropertyName("shareOverLan")]
    public bool ShareOverLan { get; set; } = false;

    // ── 解析 vless:// URI ─────────────────────────────────────────────────

    public static bool TryParse(string uri, out VlessConfig config, out string error)
    {
        config = new VlessConfig();
        error = "";
        try
        {
            uri = uri.Trim();
            if (!uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            {
                error = "不是有效的 VLESS URI（需以 vless:// 开头）";
                return false;
            }

            // vless://UUID@HOST:PORT?params#name
            var withoutScheme = uri["vless://".Length..];

            // 分离 Fragment（#name）
            var hashIdx = withoutScheme.IndexOf('#');
            string fragment = "";
            if (hashIdx >= 0)
            {
                fragment = Uri.UnescapeDataString(withoutScheme[(hashIdx + 1)..]);
                withoutScheme = withoutScheme[..hashIdx];
            }

            // 分离 Query
            var qIdx = withoutScheme.IndexOf('?');
            string query = "";
            if (qIdx >= 0)
            {
                query = withoutScheme[(qIdx + 1)..];
                withoutScheme = withoutScheme[..qIdx];
            }

            // 分离 UUID @ HOST:PORT
            var atIdx = withoutScheme.IndexOf('@');
            if (atIdx < 0) { error = "URI 格式错误：缺少 @"; return false; }

            config.Uuid = withoutScheme[..atIdx];
            var hostPort = withoutScheme[(atIdx + 1)..];

            // 处理 IPv6
            if (hostPort.StartsWith('['))
            {
                var closeBracket = hostPort.IndexOf(']');
                if (closeBracket < 0) { error = "IPv6 地址格式错误"; return false; }
                config.Server = hostPort[1..closeBracket];
                var rest = hostPort[(closeBracket + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) config.Port = p6;
            }
            else
            {
                var lastColon = hostPort.LastIndexOf(':');
                if (lastColon >= 0 && int.TryParse(hostPort[(lastColon + 1)..], out var p))
                {
                    config.Server = hostPort[..lastColon];
                    config.Port = p;
                }
                else
                {
                    config.Server = hostPort;
                }
            }

            // 解析 Query 参数
            var qs = HttpUtility.ParseQueryString(query);
            config.Security = qs["security"] ?? "none";
            config.Encryption = qs["encryption"] ?? "none";
            config.Sni = qs["sni"] ?? config.Server;
            config.Network = qs["type"] ?? "ws";
            config.WsHost = qs["host"] ?? config.Server;

            var rawPath = qs["path"] ?? "/";
            // ed 参数重组
            var ed = qs["ed"];
            config.Path = string.IsNullOrEmpty(ed)
                ? rawPath
                : $"{rawPath}?ed={ed}";

            // 使用 fragment 作为名称
            config.Name = string.IsNullOrWhiteSpace(fragment) ? config.Server : fragment;

            if (string.IsNullOrWhiteSpace(config.Uuid)) { error = "UUID 为空"; return false; }
            if (string.IsNullOrWhiteSpace(config.Server)) { error = "服务器地址为空"; return false; }

            return true;
        }
        catch (Exception ex)
        {
            error = $"解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>导出为 vless:// URI</summary>
    public string ToUri()
    {
        var qs = new System.Collections.Specialized.NameValueCollection
        {
            ["encryption"] = Encryption,
            ["security"] = Security,
            ["sni"] = Sni,
            ["type"] = Network,
            ["host"] = WsHost,
        };

        // 拆解 path?ed=xxx
        var pathPart = Path;
        string edPart = "";
        var edIdx = Path.IndexOf("?ed=", StringComparison.OrdinalIgnoreCase);
        if (edIdx >= 0)
        {
            pathPart = Path[..edIdx];
            edPart = Path[(edIdx + 1)..]; // ed=xxx
            var edVal = edPart.Split('=').Length > 1 ? edPart.Split('=')[1] : "";
            qs["path"] = pathPart;
            qs["ed"] = edVal;
        }
        else
        {
            qs["path"] = Path;
        }

        var qsStr = string.Join("&", Array.ConvertAll(qs.AllKeys, k => $"{k}={Uri.EscapeDataString(qs[k!] ?? "")}"));
        var encodedName = Uri.EscapeDataString(Name);
        return $"vless://{Uuid}@{Server}:{Port}?{qsStr}#{encodedName}";
    }

    public VlessConfig Clone() => (VlessConfig)MemberwiseClone();
}
