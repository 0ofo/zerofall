using System;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;

namespace ZeroFall.Dock.ViewModels;

public partial class StatusBarViewModel : ViewModelBase
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DispatcherTimer _metricsTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleUtc;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private string _resourceUsageText = string.Empty;

    public StatusBarViewModel(IEventBus eventBus)
    {
        SubscribeEvent(eventBus, (StatusMessageEvent e) => StatusMessage = e.Message);

        _metricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _metricsTimer.Tick += OnMetricsTick;
        _metricsTimer.Start();
        RefreshResourceUsage();
    }

    private void OnMetricsTick(object? sender, EventArgs e) => RefreshResourceUsage();

    private void RefreshResourceUsage()
    {
        var (cpu, memory) = AppProcessMetricsSampler.Sample(_process, ref _lastCpuTime, ref _lastSampleUtc);
        ResourceUsageText = AppProcessMetricsSampler.Format(cpu, memory);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _metricsTimer.Tick -= OnMetricsTick;
            _metricsTimer.Stop();
        }

        base.Dispose(disposing);
    }
}
