using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;

namespace ZeroFall.AssetRecon.Services;

/// <summary>
/// UDP 全端口探测：按端口使用 <see cref="UdpServiceProbes"/> 中的多组探测包，命中回包即记录 Banner 与规则名（指纹占位）。
/// </summary>
public static class UdpPortScanner
{
    public static async Task ScanAsync(
        IPAddress ip,
        int portStart,
        int portEnd,
        int maxConcurrency,
        int recvWaitMs,
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
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, TcpPortScanner.MaxDegreeOfParallelismUpperBound),
                CancellationToken = cancellationToken
            },
            async (port, ct) =>
            {
                try
                {
                    var row = await ProbeUdpAsync(ip, port, recvWaitMs, bannerMaxBytes, ct).ConfigureAwait(false);
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

    private static bool IsReplyFromTarget(IPEndPoint remote, IPAddress target, int expectedPort)
    {
        if (remote.Port != expectedPort)
            return false;
        if (remote.Address.Equals(target))
            return true;
        try
        {
            if (remote.Address.AddressFamily == AddressFamily.InterNetworkV6
                && remote.Address.IsIPv4MappedToIPv6
                && target.AddressFamily == AddressFamily.InterNetwork)
                return remote.Address.MapToIPv4().Equals(target);
            if (target.AddressFamily == AddressFamily.InterNetworkV6
                && target.IsIPv4MappedToIPv6
                && remote.Address.AddressFamily == AddressFamily.InterNetwork)
                return remote.Address.Equals(target.MapToIPv4());
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static async Task<PortScanRow?> ProbeUdpAsync(
        IPAddress ip,
        int port,
        int recvWaitMs,
        int bannerMaxBytes,
        CancellationToken cancellationToken)
    {
        var probes = UdpServiceProbes.GetProbeSequence(port);
        var slotMs = Math.Max(80, recvWaitMs / Math.Max(1, probes.Length));
        var swTotal = Stopwatch.StartNew();

        for (var i = 0; i < probes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = probes[i];
            UdpClient? udp = null;
            try
            {
                udp = new UdpClient(0, ip.AddressFamily);
                var remote = new IPEndPoint(ip, port);
                await udp.SendAsync(probe.Payload, remote, cancellationToken).ConfigureAwait(false);

                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                recvCts.CancelAfter(Math.Clamp(slotMs, 50, 60000));

                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(recvCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                if (!IsReplyFromTarget(result.RemoteEndPoint, ip, port))
                    continue;

                var elapsedMs = (int)Math.Min(int.MaxValue, swTotal.ElapsedMilliseconds);
                var take = Math.Min(result.Buffer.Length, Math.Clamp(bannerMaxBytes, 64, 16384));
                var banner = take > 0 ? TcpPortScanner.SanitizeBanner(result.Buffer.AsSpan(0, take)) : string.Empty;
                return new PortScanRow
                {
                    Port = port,
                    ConnectMs = elapsedMs,
                    Banner = banner,
                    Protocol = "UDP",
                    Fingerprint = probe.RuleName
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单探测失败则尝试下一条
            }
            finally
            {
                try
                {
                    udp?.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        return null;
    }
}
