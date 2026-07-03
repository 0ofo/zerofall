using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Browser.Views;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.ViewModels;

public partial class HttpIntruderTabViewModel : BrowserTabViewModelBase, IDisposable
{
    private const int MaxResults = 5000;
    private readonly IEventBus _eventBus;
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly IDisposable _intruderRequestedSub;
    private CancellationTokenSource? _attackCts;

    public DataTableViewModel ResultsTable { get; } = new();
    public ObservableCollection<HttpIntruderResultViewModel> Results { get; } = new();

    [ObservableProperty]
    private string _requestText = string.Empty;

    [ObservableProperty]
    private string _realHost = string.Empty;

    [ObservableProperty]
    private bool _isHttps;

    [ObservableProperty]
    private string _fallbackMethod = "GET";

    [ObservableProperty]
    private string _payloadListText = string.Empty;

    [ObservableProperty]
    private HttpIntruderAttackType _attackType = HttpIntruderAttackType.Sniper;

    public IReadOnlyList<HttpIntruderAttackType> AttackTypes { get; } =
        Enum.GetValues<HttpIntruderAttackType>();

    [ObservableProperty]
    private int _maxConcurrency = 10;

    [ObservableProperty]
    private int _requestTimeoutSeconds = 30;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "在请求中用 § 标记 payload 位置，例如 GET /?id=§1§ HTTP/1.1";

    [ObservableProperty]
    private HttpIntruderResultViewModel? _selectedResult;

    [ObservableProperty]
    private string _selectedResponseText = string.Empty;

    public HttpIntruderTabViewModel(IEventBus eventBus, IOutboundHttpClientFactory httpClientFactory)
    {
        _eventBus = eventBus;
        _httpClientFactory = httpClientFactory;
        _intruderRequestedSub = eventBus.SubscribeDisposable<HttpIntruderRequestedEvent>(OnIntruderRequested);

        ResultsTable.InitializeLive(
            new[] { "#", "Payload", "状态", "长度", "耗时" },
            MaxResults,
            showHeaderPanel: false,
            showLineNumberColumn: false);
        ResultsTable.DisableUrlColumns = true;
        ResultsTable.PropertyChanged += OnResultsTablePropertyChanged;
    }

    private void OnIntruderRequested(HttpIntruderRequestedEvent e)
    {
        var draft = HttpRequestComposer.BuildDraft(
            e.SourceEntryId,
            e.Method,
            e.Url,
            e.RequestHeaders,
            e.RequestBody);

        RequestText = draft.RequestText;
        RealHost = draft.RealHost;
        IsHttps = draft.IsHttps;
        FallbackMethod = draft.Method;
        StatusText = "已导入请求模板，请添加 §payload§ 标记后开始攻击";
        _eventBus.Publish(new SwitchDockTabRequestedEvent(Platform.Registries.DockPosition.Content, "http-intruder"));
    }

    private void OnResultsTablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DataTableViewModel.SelectedRow))
            return;

        SelectedResult = ResultsTable.SelectedRow?.Tag as HttpIntruderResultViewModel;
        SelectedResponseText = SelectedResult?.ResponseText ?? string.Empty;
    }

    partial void OnSelectedResultChanged(HttpIntruderResultViewModel? value)
    {
        SelectedResponseText = value?.ResponseText ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanStartAttack))]
    private async Task StartAttackAsync()
    {
        var payloads = ParsePayloadList(PayloadListText);
        if (payloads.Count == 0)
        {
            StatusText = "请填写至少一行 payload";
            return;
        }

        var markers = HttpIntruderEngine.FindMarkers(RequestText);
        if (markers.Count == 0)
        {
            StatusText = "请求中未找到 § 标记，请用 §value§ 标记要爆破的位置";
            return;
        }

        if (string.IsNullOrWhiteSpace(RealHost))
        {
            StatusText = "请填写 Host";
            return;
        }

        _attackCts = new CancellationTokenSource();
        IsRunning = true;
        StartAttackCommand.NotifyCanExecuteChanged();
        StopAttackCommand.NotifyCanExecuteChanged();

        ClearResultsInternal();
        OpenResultsBottomTab();

        var iterations = HttpIntruderEngine.GenerateIterations(RequestText, payloads, AttackType).ToList();
        StatusText = $"攻击中 0/{iterations.Count}…";

        var concurrency = Math.Clamp(MaxConcurrency, 1, 256);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 300));
        var completed = 0;
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var total = iterations.Count;

        try
        {
            var tasks = iterations.Select(async (iteration, index) =>
            {
                await gate.WaitAsync(_attackCts.Token).ConfigureAwait(false);
                try
                {
                    var resultVm = new HttpIntruderResultViewModel
                    {
                        Index = index + 1,
                        PayloadLabel = iteration.Label,
                        RequestText = iteration.RequestText
                    };

                    var sendResult = await HttpRequestComposer.SendAsync(
                        _httpClientFactory,
                        "http-intruder",
                        timeout,
                        iteration.RequestText,
                        FallbackMethod,
                        RealHost,
                        IsHttps,
                        _attackCts.Token).ConfigureAwait(false);

                    if (sendResult.Success)
                    {
                        resultVm.Status = sendResult.StatusCode.ToString();
                        resultVm.Length = sendResult.ResponseLength.ToString();
                        resultVm.Latency = $"{sendResult.LatencyMs} ms";
                        resultVm.ResponseText = sendResult.ResponseText;
                    }
                    else
                    {
                        resultVm.Status = "失败";
                        resultVm.Length = "—";
                        resultVm.Latency = sendResult.LatencyMs > 0 ? $"{sendResult.LatencyMs} ms" : "—";
                        resultVm.Error = sendResult.Error;
                        resultVm.ResponseText = sendResult.Error;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Results.Add(resultVm);
                        var row = new DataRowViewModel { Tag = resultVm };
                        row.Values.Add(resultVm.Index.ToString());
                        row.Values.Add(resultVm.PayloadLabel);
                        row.Values.Add(resultVm.Status);
                        row.Values.Add(resultVm.Length);
                        row.Values.Add(resultVm.Latency);
                        ResultsTable.PrependLiveRow(row);
                        var done = Interlocked.Increment(ref completed);
                        StatusText = $"攻击中 {done}/{total}…";
                    });
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusText = _attackCts.IsCancellationRequested
                    ? $"已停止，完成 {completed}/{total}"
                    : $"完成 {completed}/{total} 个请求");
        }
        catch (OperationCanceledException)
        {
            StatusText = "攻击已取消";
        }
        finally
        {
            IsRunning = false;
            StartAttackCommand.NotifyCanExecuteChanged();
            StopAttackCommand.NotifyCanExecuteChanged();
            _attackCts?.Dispose();
            _attackCts = null;
        }
    }

    private bool CanStartAttack() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStopAttack))]
    private void StopAttack()
    {
        _attackCts?.Cancel();
        StatusText = "正在停止…";
    }

    private bool CanStopAttack() => IsRunning;

    [RelayCommand]
    private void ClearResults()
    {
        ClearResultsInternal();
        StatusText = "结果已清空";
    }

    [RelayCommand]
    private void WrapSelectionWithMarkers()
    {
        StatusText = "请在请求编辑器中手动将目标文本改为 §payload§ 格式（与 Burp Intruder 相同）";
    }

    private void OpenResultsBottomTab()
    {
        _eventBus.Publish(new AddDockTabEvent(DockPosition.Bottom, new DockTabItemViewModel
        {
            Id = "http-intruder-results",
            Title = IsRunning ? "Intruder 攻击中" : "Intruder 结果",
            Icon = IconHelper.GetIcon("SemiIconPulse"),
            Content = new HttpIntruderResultsView { DataContext = this },
            IsClosable = true
        }));
    }

    private void ClearResultsInternal()
    {
        Results.Clear();
        ResultsTable.ClearLiveRows();
        SelectedResult = null;
        SelectedResponseText = string.Empty;
    }

    private static List<string> ParsePayloadList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();
    }

    public void Dispose()
    {
        ResultsTable.PropertyChanged -= OnResultsTablePropertyChanged;
        _intruderRequestedSub.Dispose();
        _attackCts?.Cancel();
        _attackCts?.Dispose();
    }
}
