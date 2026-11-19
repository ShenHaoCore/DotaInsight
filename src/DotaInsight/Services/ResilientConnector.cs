using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;

namespace DotaInsight.Services;

/// <summary>
/// 韧性连接器：解决「单个 Cloudflare 边缘 IP 被丢包」的问题。
/// 实测该场景下 SYN 发出后无应答（netstat 呈 SYN_SENT，~20s 才超时），
/// 而同一域名的另一个解析 IP 是通的——逐个串行等 TCP 超时太慢，
/// 这里并行竞速：所有 IP 同时发起连接（单个限时 3s），谁先通用谁。
/// 也负责经代理的 CONNECT 隧道（供探测器与主通道复用）。
/// </summary>
public static class ResilientConnector
{
    private static readonly TimeSpan PerIpTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 建立到目标端点的 TCP 连接：代理非空走 CONNECT 隧道，否则多 IP 竞速直连。
    /// </summary>
    public static async ValueTask<Stream> ConnectStreamAsync(
        DnsEndPoint endpoint,
        IWebProxy? proxy,
        CancellationToken cancellationToken)
    {
        if (proxy is not null)
        {
            return await ConnectViaProxyAsync(endpoint, proxy, cancellationToken).ConfigureAwait(false);
        }

        return await ConnectRacingAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>并行竞速：所有解析出的 IP 同时连，单个 3s 限时，第一个「成功」的胜出。
    /// 注意被丢包的 IP 会在 3s 超时后以失败退出——不能因它取消还在成功的其他连接，
    /// 所以等「成功」或「全部失败」为止。</summary>
    private static async Task<Stream> ConnectRacingAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var pending = new List<Task<Stream>>(addresses.Length);
        foreach (var ip in addresses)
        {
            pending.Add(ConnectOneAsync(ip, endpoint.Port, cancellationToken));
        }

        Task<Stream> lastFailure;
        do
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                return done.Result;
            }

            lastFailure = done;
        }
        while (pending.Count > 0);

        // 全部失败：重抛最后一个真实异常（保持原始类型与栈）
        await lastFailure.ConfigureAwait(false);
        throw new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Stream> ConnectOneAsync(IPAddress ip, int port, CancellationToken cancellationToken)
    {
        try
        {
            var tcp = new TcpClient(ip.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PerIpTimeout);
            await tcp.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return new NetworkStream(tcp.Client, ownsSocket: true);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// CONNECT 隧道：TcpClient 连代理 → 发 CONNECT → 等待 200 → 返回隧道流。
    /// </summary>
    private static async Task<Stream> ConnectViaProxyAsync(
        DnsEndPoint endpoint,
        IWebProxy proxy,
        CancellationToken cancellationToken)
    {
        var proxyUri = proxy.GetProxy(new Uri($"https://{endpoint.Host}:{endpoint.Port}"))
            ?? throw new InvalidOperationException("代理未提供地址");

        var host = proxyUri.Host;
        var port = proxyUri.Port > 0 ? proxyUri.Port : 80;

        var tcp = new TcpClient();
        Stream? stream = null;
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(tcp.Client, ownsSocket: true);

            var request =
                $"CONNECT {endpoint.Host}:{endpoint.Port} HTTP/1.1\r\n" +
                $"Host: {endpoint.Host}:{endpoint.Port}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);

            var response = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !response.Contains(" 200", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"代理隧道建立失败：{response.Split('\r')[0]}");
            }

            return stream;
        }
        catch
        {
            stream?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>读 HTTP 头（到空行为止）。</summary>
    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.Length > 8192)
            {
                break;
            }
        }

        return sb.ToString();
    }
}
