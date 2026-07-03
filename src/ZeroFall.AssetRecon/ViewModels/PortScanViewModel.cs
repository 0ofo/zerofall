using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.AssetRecon.Models;
using ZeroFall.AssetRecon.Services;
using ZeroFall.Base.Mvvm;

namespace ZeroFall.AssetRecon.ViewModels;

public partial class PortScanViewModel : ViewModelBase
{
    private CancellationTokenSource? _scanCts;
    private readonly ConcurrentQueue<PortScanRow> _pendingPortRows = new();
    private int _drainRowsScheduled;

    [ObservableProperty]
    private string _targetHost = "127.0.0.1";

    [ObservableProperty]
    private int _portStart = 1;

    [ObservableProperty]
    private int _portEnd = 65535;

    [ObservableProperty]
    private int _maxConcurrency = 400;

    [ObservableProperty]
    private int _connectTimeoutMs = 1200;

    [ObservableProperty]
    private int _bannerReadMs = 800;

    [ObservableProperty]
    private int _bannerMaxBytes = 2048;

    [ObservableProperty]
    private bool _enableUdp = true;

    [ObservableProperty]
    private int _udpRecvWaitMs = 800;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText =
        "就绪。TCP 全端口连接 + Banner；UDP 按端口多探测（UdpServiceProbes），可扩展规则名与载荷逼近 Goby。并发 65536 慎用。";

    [ObservableProperty]
    private string _progressText = string.Empty;

    public ObservableCollection<PortScanRow> Results { get; } = new();

    private bool CanStartScan() => !IsScanning;

    private bool CanStopScan() => IsScanning;

    private void EnqueueOpenPortRow(PortScanRow row)
    {
        _pendingPortRows.Enqueue(row);
        ScheduleDrainPendingRows();
    }

    /// <summary>
    /// 将「开放端口」入队后合并为单次 UI 投递，避免每端口一次 Post 把队列撑满导致停止后长时间假死。
    /// </summary>
    private void ScheduleDrainPendingRows()
    {
        if (Interlocked.CompareExchange(ref _drainRowsScheduled, 1, 0) != 0)
            return;
        Dispatcher.UIThread.Post(DrainPendingRowsPosted, DispatcherPriority.Background);
    }

    private void DrainPendingRowsPosted()
    {
        try
        {
            while (_pendingPortRows.TryDequeue(out var row))
                Results.Add(row);
        }
        finally
        {
            Interlocked.Exchange(ref _drainRowsScheduled, 0);
            if (!_pendingPortRows.IsEmpty)
                ScheduleDrainPendingRows();
        }
    }

    private void DrainAllPendingRowsToResults()
    {
        while (_pendingPortRows.TryDequeue(out var row))
            Results.Add(row);
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        var host = TargetHost.Trim();
        if (string.IsNullOrEmpty(host))
        {
            StatusText = "请填写目标主机或 IP";
            return;
        }

        if (PortStart < 1 || PortEnd > 65535 || PortStart > PortEnd)
        {
            StatusText = "端口范围无效（1–65535，且起始不大于结束）";
            return;
        }

        var conc = Math.Clamp(MaxConcurrency, 1, TcpPortScanner.MaxDegreeOfParallelismUpperBound);
        MaxConcurrency = conc;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        Results.Clear();
        ProgressText = string.Empty;
        StatusText = "正在解析目标…";

        string? terminalStatus = null;
        var scanFinishedNormally = false;

        try
        {
            var ip = await TcpPortScanner.ResolveTargetAsync(host, _scanCts.Token).ConfigureAwait(false);
            if (ip is null)
            {
                terminalStatus = "无法解析主机名";
                return;
            }

            StatusText = $"TCP 扫描 {ip}（{PortStart}-{PortEnd}），并发 {conc}…";

            var tcpProgress = new Progress<(int Scanned, int Total, int OpenCount)>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                    ProgressText = $"[TCP] 已扫描 {p.Scanned}/{p.Total}，开放 {p.OpenCount}");
            });

            await TcpPortScanner.ScanAsync(
                host,
                ip,
                PortStart,
                PortEnd,
                conc,
                Math.Clamp(ConnectTimeoutMs, 100, 60000),
                Math.Clamp(BannerReadMs, 50, 30000),
                Math.Clamp(BannerMaxBytes, 64, 16384),
                tcpProgress,
                EnqueueOpenPortRow,
                _scanCts.Token).ConfigureAwait(false);

            if (EnableUdp)
            {
                StatusText = $"UDP 扫描 {ip}（{PortStart}-{PortEnd}），并发 {conc}…";
                var udpWait = Math.Clamp(UdpRecvWaitMs, 50, 60000);
                var udpProgress = new Progress<(int Scanned, int Total, int OpenCount)>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                        ProgressText = $"[UDP] 已扫描 {p.Scanned}/{p.Total}，有响应 {p.OpenCount}");
                });

                await UdpPortScanner.ScanAsync(
                    ip,
                    PortStart,
                    PortEnd,
                    conc,
                    udpWait,
                    Math.Clamp(BannerMaxBytes, 64, 16384),
                    udpProgress,
                    EnqueueOpenPortRow,
                    _scanCts.Token).ConfigureAwait(false);
            }

            scanFinishedNormally = true;
        }
        catch (OperationCanceledException)
        {
            terminalStatus = "扫描已停止";
        }
        catch (Exception ex)
        {
            terminalStatus = $"扫描异常: {ex.Message}";
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DrainAllPendingRowsToResults();
                SortResultsByPort();
                if (terminalStatus is not null)
                    StatusText = terminalStatus;
                else if (scanFinishedNormally)
                {
                    var tcpOpen = Results.Count(r => r.Protocol == "TCP");
                    var udpHits = Results.Count(r => r.Protocol == "UDP");
                    StatusText = EnableUdp
                        ? $"完成。TCP 开放 {tcpOpen}，UDP 有响应 {udpHits}，表格共 {Results.Count} 条。"
                        : $"完成。TCP 开放 {tcpOpen}。";
                }

                IsScanning = false;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopScan))]
    private void StopScan()
    {
        _scanCts?.Cancel();
        Dispatcher.UIThread.Post(() => ProgressText = "正在停止…", DispatcherPriority.Normal);
    }

    [RelayCommand]
    private void ClearResults()
    {
        if (IsScanning)
            return;
        Results.Clear();
        ProgressText = string.Empty;
        StatusText = "已清空结果";
    }

    private void SortResultsByPort()
    {
        var sorted = Results
            .OrderBy(r => r.Port)
            .ThenBy(r => r.Protocol, StringComparer.Ordinal)
            .ToList();
        Results.Clear();
        foreach (var r in sorted)
            Results.Add(r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = null;
        }

        base.Dispose(disposing);
    }
}
