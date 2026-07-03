using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;

namespace ZeroFall.AssetRecon.Services;

/// <summary>
/// TCP 全端口连接扫描 + 被动 Banner 读取；对常见 Web 端口在无首包时发送极简 <c>HEAD / HTTP/1.0</c> 探测。
/// </summary>
public static class TcpPortScanner
{
    /// <summary>TCP/UDP 扫描并发上限（与端口总数同量级，慎用）。</summary>
    public const int MaxDegreeOfParallelismUpperBound = 65536;

    private static readonly HashSet<int> WebProbePorts =
    [
        80, 443, 8080, 8443, 8000, 8888, 8008, 8081, 9000, 9443, 7080, 8880, 3000, 5000, 9090
    ];

    public static async Task<IPAddress?> ResolveTargetAsync(string hostOrIp, CancellationToken cancellationToken)
    {
        hostOrIp = hostOrIp.Trim();
        if (string.IsNullOrEmpty(hostOrIp))
            return null;
        if (IPAddress.TryParse(hostOrIp, out var parsed))
            return parsed;
        var addrs = await Dns.GetHostAddressesAsync(hostOrIp, cancellationToken).ConfigureAwait(false);
        return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
    }

    /// <summary>
    /// 扫描 <paramref name="portStart"/>..<paramref name="portEnd"/>（含端点）。仅对成功建立的端口调用 <paramref name="onOpenPort"/>。
    /// </summary>
    public static async Task ScanAsync(
        string hostLabel,
        IPAddress ip,
        int portStart,
        int portEnd,
        int maxConcurrency,
        int connectTimeoutMs,
        int bannerReadMs,
        int bannerMaxBytes,
        IProgress<(int Scanned, int Total, int OpenCount)>? progress,
        Action<PortScanRow> onOpenPort,
        CancellationToken cancellationToken)
    {
        if (portStart < 1 || portEnd > 65535 || portStart > portEnd)
            throw new ArgumentOutOfRangeException(nameof(portEnd), "端口范围须在 1–65535 且起始不大于结束。");

        var total = portEnd - portStart + 1;
        long scannedL = 0;
        long openL = 0;
        var progressLock = new object();

        void Report()
        {
            var s = (int)Math.Min(int.MaxValue, Interlocked.Read(ref scannedL));
            var o = (int)Math.Min(int.MaxValue, Interlocked.Read(ref openL));
            progress?.Report((s, total, o));
        }

        var range = Enumerable.Range(portStart, total);
        await Parallel.ForEachAsync(
            range,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, MaxDegreeOfParallelismUpperBound),
                CancellationToken = cancellationToken
            },
            async (port, ct) =>
            {
                try
                {
                    var row = await ProbePortAsync(hostLabel, ip, port, connectTimeoutMs, bannerReadMs, bannerMaxBytes, ct)
                        .ConfigureAwait(false);
                    if (row is not null)
                    {
                        Interlocked.Increment(ref openL);
                        onOpenPort(row);
                    }
                }
                finally
                {
                    var v = Interlocked.Increment(ref scannedL);
                    if (v % 256 == 0 || v == total)
                    {
                        lock (progressLock)
                            Report();
                    }
                }
            }).ConfigureAwait(false);

        lock (progressLock)
            Report();
    }

    private static async Task<PortScanRow?> ProbePortAsync(
        string hostLabel,
        IPAddress ip,
        int port,
        int connectTimeoutMs,
        int bannerReadMs,
        int bannerMaxBytes,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var sw = Stopwatch.StartNew();
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(connectTimeoutMs);
                await socket.ConnectAsync(new IPEndPoint(ip, port), connectCts.Token).ConfigureAwait(false);
            }

            sw.Stop();
            var connectMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            var banner = await ReadBannerAsync(socket, hostLabel, ip, port, bannerReadMs, bannerMaxBytes, cancellationToken)
                .ConfigureAwait(false);
            return new PortScanRow
            {
                Port = port,
                ConnectMs = connectMs,
                Banner = banner,
                Protocol = "TCP",
                Fingerprint = string.Empty
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                socket.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<string> ReadBannerAsync(
        Socket socket,
        string hostLabel,
        IPAddress ip,
        int port,
        int bannerReadMs,
        int bannerMaxBytes,
        CancellationToken cancellationToken)
    {
        var buf = new byte[Math.Clamp(bannerMaxBytes, 64, 16384)];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(bannerReadMs);

        try
        {
            var n = await ReceiveWithTimeoutAsync(socket, buf, readCts.Token).ConfigureAwait(false);
            if (n > 0)
                return SanitizeBanner(buf.AsSpan(0, n));

            if (WebProbePorts.Contains(port))
            {
                var hostHeader = IPAddress.TryParse(hostLabel.Trim(), out _) ? ip.ToString() : hostLabel.Trim();
                var probe = Encoding.ASCII.GetBytes(
                    $"HEAD / HTTP/1.0\r\nHost: {hostHeader}\r\nUser-Agent: ZeroFall/1.0\r\nConnection: close\r\n\r\n");
                await socket.SendAsync(probe, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                using var read2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                read2.CancelAfter(bannerReadMs);
                var n2 = await ReceiveWithTimeoutAsync(socket, buf, read2.Token).ConfigureAwait(false);
                if (n2 > 0)
                    return SanitizeBanner(buf.AsSpan(0, n2));
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private static async Task<int> ReceiveWithTimeoutAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var n = await socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), SocketFlags.None, cancellationToken)
            .ConfigureAwait(false);
        if (n <= 0 || n >= buffer.Length)
            return n;
        try
        {
            if (socket.Available > 0)
            {
                var more = await socket
                    .ReceiveAsync(buffer.AsMemory(n, buffer.Length - n), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
                if (more > 0)
                    n += more;
            }
        }
        catch
        {
            // ignore
        }

        return n;
    }

    internal static string SanitizeBanner(ReadOnlySpan<byte> raw)
    {
        const int maxLen = 2048;
        var limit = Math.Min(raw.Length, maxLen);
        Span<char> chars = stackalloc char[limit];
        var j = 0;
        for (var i = 0; i < limit && j < chars.Length; i++)
        {
            var c = (char)raw[i];
            if (c is '\r' or '\n')
                chars[j++] = ' ';
            else if (c < ' ' || c > '~')
                chars[j++] = '.';
            else
                chars[j++] = c;
        }

        var s = new string(chars[..j]).Trim();
        return s.Length <= maxLen ? s : s[..maxLen];
    }
}
