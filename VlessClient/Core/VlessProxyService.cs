using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VlessClient.Models;

namespace VlessClient.Core;

/// <summary>
/// VLESS over WebSocket 混合代理（SOCKS5 + HTTP CONNECT + HTTP）
/// 基于上传的 VlessProxyClient.cs 核心逻辑
/// </summary>
public sealed class VlessProxyService : IDisposable
{
    private VlessConfig _cfg;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _activeConnections;
    private bool _disposed;

    public event Action<string>? LogMessage;
    public event Action<int>? ConnectionCountChanged;

    public bool IsRunning => _acceptLoop != null && !_acceptLoop.IsCompleted;
    public int ActiveConnections => _activeConnections;

    public VlessProxyService(VlessConfig cfg)
    {
        _cfg = cfg;
    }

    public void UpdateConfig(VlessConfig cfg)
    {
        _cfg = cfg;
    }

    // ── 启动 / 停止 ──────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Loopback, _cfg.ListenPort);
        _listener.Start();
        Log($"SOCKS5 代理已启动 → socks5://127.0.0.1:{_cfg.ListenPort}");

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* timeout ok */ }
        }
        _listener = null;
        Log("代理已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"Accept 错误: {ex.Message}"); break; }

            Interlocked.Increment(ref _activeConnections);
            ConnectionCountChanged?.Invoke(_activeConnections);
            _ = Task.Run(() => HandleClientAsync(client, ct), ct)
                    .ContinueWith(_ =>
                    {
                        Interlocked.Decrement(ref _activeConnections);
                        ConnectionCountChanged?.Invoke(_activeConnections);
                    });
        }
    }

    // ── 连接分发 ──────────────────────────────────────────────────────────────

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var buf = new byte[1];
        try
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n == 0) { client.Close(); return; }
        }
        catch { client.Close(); return; }

        byte first = buf[0];
        if (first == 0x05)
            await HandleSocks5Async(client, stream, ct);
        else if ((first >= 0x41 && first <= 0x5A) || (first >= 0x61 && first <= 0x7A))
            await HandleHttpAsync(client, stream, first, ct);
        else
        {
            client.Close();
        }
    }

    // ── SOCKS5 ───────────────────────────────────────────────────────────────

    private async Task HandleSocks5Async(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var tmp = new byte[1];
            await ReadExactAsync(stream, tmp, 0, 1, ct);
            int nMethods = tmp[0];
            await ReadExactAsync(stream, new byte[nMethods], 0, nMethods, ct);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            var req = new byte[4];
            await ReadExactAsync(stream, req, 0, 4, ct);
            if (req[0] != 0x05 || req[1] != 0x01)
            {
                client.Close(); return;
            }

            string host; int port;
            switch (req[3])
            {
                case 0x01:
                    var ip4 = new byte[4];
                    await ReadExactAsync(stream, ip4, 0, 4, ct);
                    host = new IPAddress(ip4).ToString();
                    port = await ReadUInt16BEAsync(stream, ct);
                    break;
                case 0x03:
                    await ReadExactAsync(stream, tmp, 0, 1, ct);
                    var dom = new byte[tmp[0]];
                    await ReadExactAsync(stream, dom, 0, dom.Length, ct);
                    host = Encoding.UTF8.GetString(dom);
                    port = await ReadUInt16BEAsync(stream, ct);
                    break;
                case 0x04:
                    var ip6 = new byte[16];
                    await ReadExactAsync(stream, ip6, 0, 16, ct);
                    host = new IPAddress(ip6).ToString();
                    port = await ReadUInt16BEAsync(stream, ct);
                    break;
                default:
                    client.Close(); return;
            }

            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);

            var pending = new List<ArraySegment<byte>>();
            var earlyTask = CollectEarlyDataAsync(stream, pending, 100, ct);

            ClientWebSocket ws;
            try { ws = await OpenTunnelAsync(ct); }
            catch (Exception ex) { Log($"隧道连接失败 [{host}:{port}]: {ex.Message}"); client.Close(); return; }

            await earlyTask;

            var vlessHdr = BuildVlessHeader(_cfg.Uuid, host, port);
            var firstPkt = pending.Count > 0
                ? ConcatArraySegments(vlessHdr, pending)
                : vlessHdr;
            await ws.SendAsync(firstPkt, WebSocketMessageType.Binary, true, ct);
            await RelayAsync(stream, ws, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"SOCKS5 错误: {ex.Message}"); }
        finally { client.Close(); }
    }

    // ── HTTP CONNECT + HTTP ───────────────────────────────────────────────────

    private async Task HandleHttpAsync(TcpClient client, NetworkStream stream, byte firstByte, CancellationToken ct)
    {
        try
        {
            // 用 BufferedStream 避免逐字节 ReadAsync，改为批量读取后在 buffer 中查找 \r\n\r\n
            var headerBuf = new byte[8192];
            headerBuf[0]  = firstByte;
            int totalRead = 1;
            int eoh       = -1; // end-of-header index

            while (totalRead < headerBuf.Length)
            {
                int n = await stream.ReadAsync(headerBuf.AsMemory(totalRead), ct);
                if (n == 0) break;
                totalRead += n;

                // 在新读入的数据里查找 \r\n\r\n（从安全偏移开始）
                int searchFrom = Math.Max(0, totalRead - n - 3);
                eoh = FindEndOfHeader(headerBuf, searchFrom, totalRead);
                if (eoh >= 0) break;
            }

            if (eoh < 0) { client.Close(); return; } // 请求头过大或格式异常

            string raw   = Encoding.ASCII.GetString(headerBuf, 0, eoh);
            // eoh + 4 之后是请求体（对 CONNECT 无意义，对普通 HTTP 可能有 body）
            int bodyStart = eoh + 4;
            int preReadBodyLen = totalRead - bodyStart;

            var lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var rl    = lines[0].Split(' ');
            if (rl.Length < 2) { client.Close(); return; }

            string method = rl[0].ToUpperInvariant();
            string url    = rl[1];

            if (method == "CONNECT")
            {
                int idx  = url.LastIndexOf(':');
                string h = idx > 0 ? url[..idx] : url;
                int    p = (idx > 0 && int.TryParse(url[(idx + 1)..], out var pp)) ? pp : 443;

                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);

                var pending = new List<ArraySegment<byte>>();
                var earlyTask = CollectEarlyDataAsync(stream, pending, 100, ct);

                ClientWebSocket ws;
                try { ws = await OpenTunnelAsync(ct); }
                catch (Exception ex) { Log($"隧道连接失败 [{h}:{p}]: {ex.Message}"); client.Close(); return; }

                await earlyTask;

                var vlessHdr = BuildVlessHeader(_cfg.Uuid, h, p);
                var firstPkt = pending.Count > 0
                    ? ConcatArraySegments(vlessHdr, pending)
                    : vlessHdr;
                await ws.SendAsync(firstPkt, WebSocketMessageType.Binary, true, ct);
                await RelayAsync(stream, ws, ct);
            }
            else
            {
                string baseUrl = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? url : $"http://{GetHeader(lines, "host")}{url}";
                Uri u;
                try { u = new Uri(baseUrl); }
                catch { client.Close(); return; }

                string host = u.Host;
                int    port = u.Port > 0 ? u.Port : (u.Scheme == "https" ? 443 : 80);

                // 读取剩余 body（请求头之后可能已预读了部分 body）
                int bodyLen = 0;
                string cl = GetHeader(lines, "content-length");
                if (!string.IsNullOrEmpty(cl)) int.TryParse(cl, out bodyLen);

                byte[] body = new byte[bodyLen];
                if (bodyLen > 0)
                {
                    // 先使用预读部分
                    int alreadyHave = Math.Min(preReadBodyLen, bodyLen);
                    if (alreadyHave > 0)
                        Buffer.BlockCopy(headerBuf, bodyStart, body, 0, alreadyHave);
                    // 再从 stream 补足剩余
                    if (alreadyHave < bodyLen)
                        await ReadExactAsync(stream, body, alreadyHave, bodyLen - alreadyHave, ct);
                }

                ClientWebSocket ws;
                try { ws = await OpenTunnelAsync(ct); }
                catch (Exception ex) { Log($"隧道连接失败 [{host}:{port}]: {ex.Message}"); client.Close(); return; }

                var fwdSb = new StringBuilder();
                fwdSb.Append($"{method} {u.PathAndQuery} HTTP/1.1\r\n");
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) break;
                    if (line.StartsWith("proxy-connection:", StringComparison.OrdinalIgnoreCase)) continue;
                    fwdSb.Append(line + "\r\n");
                }
                fwdSb.Append("\r\n");

                var vlessHdr = BuildVlessHeader(_cfg.Uuid, host, port);
                var rawReq   = Encoding.UTF8.GetBytes(fwdSb.ToString());
                var pkt      = ConcatBytes(vlessHdr, rawReq, body);
                await ws.SendAsync(pkt, WebSocketMessageType.Binary, true, ct);
                await RelayHttpResponseAsync(stream, ws, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"HTTP 错误: {ex.Message}"); }
        finally { client.Close(); }
    }

    // ── Early data ────────────────────────────────────────────────────────────

    private static async Task CollectEarlyDataAsync(
        NetworkStream stream,
        List<ArraySegment<byte>> pending,
        int millisecondsTimeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(millisecondsTimeout);
        var buf = new byte[65536];
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, timeoutCts.Token);
                if (n <= 0) break;
                var chunk = new byte[n];
                Buffer.BlockCopy(buf, 0, chunk, 0, n);
                pending.Add(new ArraySegment<byte>(chunk));
            }
        }
        catch (OperationCanceledException) { /* 超时正常退出 */ }
        catch { /* 连接异常，忽略，让后续流程处理 */ }
    }

    // ── 双向中继 ──────────────────────────────────────────────────────────────

    private static async Task RelayAsync(NetworkStream stream, ClientWebSocket ws, CancellationToken outerCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var respBuf     = Array.Empty<byte>();
        var respSkipped = false;
        var respHdrSize = -1;

        var wsToTcp = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try { result = await ws.ReceiveAsync(buf, cts.Token); }
                    catch { break; }
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    int count = result.Count;

                    if (respSkipped)
                    {
                        await stream.WriteAsync(buf.AsMemory(0, count), cts.Token);
                        continue;
                    }

                    respBuf = Append(respBuf, buf, count);
                    if (respBuf.Length < 2) continue;
                    if (respHdrSize == -1) respHdrSize = 2 + respBuf[1];
                    if (respBuf.Length < respHdrSize) continue;

                    respSkipped = true;
                    int payloadLen = respBuf.Length - respHdrSize;
                    if (payloadLen > 0)
                        await stream.WriteAsync(respBuf.AsMemory(respHdrSize, payloadLen), cts.Token);
                    respBuf = Array.Empty<byte>();
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { cts.Cancel(); }
        });

        var tcpToWs = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int n;
                    try { n = await stream.ReadAsync(buf, cts.Token); }
                    catch { break; }
                    if (n == 0) break;
                    await ws.SendAsync(new ArraySegment<byte>(buf, 0, n),
                        WebSocketMessageType.Binary, true, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { cts.Cancel(); }
        });

        await Task.WhenAny(wsToTcp, tcpToWs);
        try { ws.Abort(); } catch { }
        try { stream.Close(); } catch { }
        await Task.WhenAll(wsToTcp, tcpToWs);
    }

    private static async Task RelayHttpResponseAsync(NetworkStream stream, ClientWebSocket ws, CancellationToken ct)
    {
        var recvBuf      = new byte[65536];
        var vlessBuf     = Array.Empty<byte>();
        var vlessSkipped = false;
        var vlessHdrSize = -1;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(recvBuf, ct); }
                catch { break; }
                if (result.MessageType == WebSocketMessageType.Close) break;

                var chunk = recvBuf[..result.Count];
                if (!vlessSkipped)
                {
                    vlessBuf = Append(vlessBuf, chunk);
                    if (vlessBuf.Length < 2) continue;
                    if (vlessHdrSize == -1) vlessHdrSize = 2 + vlessBuf[1];
                    if (vlessBuf.Length < vlessHdrSize) continue;
                    vlessSkipped = true;
                    chunk = vlessBuf[vlessHdrSize..];
                    vlessBuf = Array.Empty<byte>();
                    if (chunk.Length == 0) continue;
                }
                await stream.WriteAsync(chunk.AsMemory(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { try { ws.Abort(); } catch { } }
    }

    // ── WebSocket 隧道 ────────────────────────────────────────────────────────

    private async Task<ClientWebSocket> OpenTunnelAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Host",          _cfg.WsHost);
        ws.Options.SetRequestHeader("User-Agent",    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Pragma",        "no-cache");

        if (!_cfg.RejectUnauthorized)
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(new Uri(BuildWsUrl()), timeout.Token);
        return ws;
    }

    // ── VLESS 请求头构建 ──────────────────────────────────────────────────────

    private static byte[] BuildVlessHeader(string uuid, string host, int port)
    {
        var uid = Convert.FromHexString(uuid.Replace("-", ""));
        byte atype; byte[] abuf;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            { atype = 0x01; abuf = ip.GetAddressBytes(); }
            else
            { atype = 0x04; abuf = ip.GetAddressBytes(); }
        }
        else
        {
            atype = 0x02;
            var db = Encoding.UTF8.GetBytes(host);
            abuf = new byte[1 + db.Length];
            abuf[0] = (byte)db.Length;
            db.CopyTo(abuf, 1);
        }

        var hdr = new byte[22];
        int o = 0;
        hdr[o++] = 0x00;
        uid.CopyTo(hdr, o); o += 16;
        hdr[o++] = 0x00;
        hdr[o++] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(o, 2), (ushort)port); o += 2;
        hdr[o] = atype;

        var result = new byte[hdr.Length + abuf.Length];
        hdr.CopyTo(result, 0);
        abuf.CopyTo(result, hdr.Length);
        return result;
    }

    private string BuildWsUrl()
    {
        string scheme = (_cfg.Security == "tls" || _cfg.Port == 443) ? "wss" : "ws";
        int qIdx = _cfg.Path.IndexOf('?');
        if (qIdx >= 0)
        {
            string p = _cfg.Path[..qIdx];
            string q = _cfg.Path[(qIdx + 1)..];
            return $"{scheme}://{_cfg.Server}:{_cfg.Port}{p}?{q}";
        }
        return $"{scheme}://{_cfg.Server}:{_cfg.Port}{_cfg.Path}";
    }

    // ── 工具方法 ──────────────────────────────────────────────────────────────

    private static async Task ReadExactAsync(NetworkStream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + read, count - read), ct);
            if (n == 0) throw new Exception("连接已关闭");
            read += n;
        }
    }

    private static async Task<int> ReadUInt16BEAsync(NetworkStream s, CancellationToken ct)
    {
        var b = new byte[2];
        await ReadExactAsync(s, b, 0, 2, ct);
        return (b[0] << 8) | b[1];
    }

    private static byte[] Append(byte[] existing, byte[] src, int count)
    {
        var r = new byte[existing.Length + count];
        existing.CopyTo(r, 0);
        Buffer.BlockCopy(src, 0, r, existing.Length, count);
        return r;
    }

    private static byte[] Append(byte[] existing, byte[] src) => Append(existing, src, src.Length);

    private static byte[] ConcatArraySegments(byte[] header, List<ArraySegment<byte>> segments)
    {
        int total = header.Length + segments.Sum(s => s.Count);
        var result = new byte[total];
        header.CopyTo(result, 0);
        int pos = header.Length;
        foreach (var seg in segments)
        {
            Buffer.BlockCopy(seg.Array!, seg.Offset, result, pos, seg.Count);
            pos += seg.Count;
        }
        return result;
    }

    private static byte[] ConcatBytes(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        int pos = 0;
        foreach (var a in arrays) { a.CopyTo(result, pos); pos += a.Length; }
        return result;
    }

    private static string GetHeader(string[] lines, string name)
    {
        string prefix = name.ToLowerInvariant() + ":";
        foreach (var l in lines.Skip(1))
        {
            if (l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return l[(prefix.Length)..].Trim();
        }
        return string.Empty;
    }

    /// <summary>在字节数组中查找 \r\n\r\n，返回第一个 \r 的位置，未找到返回 -1</summary>
    private static int FindEndOfHeader(byte[] buf, int from, int count)
    {
        for (int i = from; i <= count - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private void Log(string msg) => LogMessage?.Invoke(msg);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _listener?.Stop();
    }
}
