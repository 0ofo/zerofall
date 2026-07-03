using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.ViewModels;

public partial class HttpReplayTabViewModel : BrowserTabViewModelBase, IDisposable
{
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly IDisposable _replayRequestedSub;

    public ObservableCollection<HttpReplayItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private HttpReplayItemViewModel? _selectedItem;

    public HttpReplayTabViewModel(IEventBus eventBus, IOutboundHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _replayRequestedSub = eventBus.SubscribeDisposable<HttpReplayRequestedEvent>(OnReplayRequested);
    }

    private void OnReplayRequested(HttpReplayRequestedEvent e)
    {
        var item = BuildDraft(e);
        Items.Insert(0, item);
        SelectedItem = item;
    }

    [RelayCommand]
    private async Task ReplaySelectedAsync()
    {
        if (SelectedItem is null)
            return;

        await ExecuteReplayAsync(SelectedItem);
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        SelectedItem = null;
    }

    private static HttpReplayItemViewModel BuildDraft(HttpReplayRequestedEvent e)
    {
        var draft = HttpRequestComposer.BuildDraft(
            e.SourceEntryId,
            e.Method,
            e.Url,
            e.RequestHeaders,
            e.RequestBody);

        return new HttpReplayItemViewModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = DateTime.Now.ToString("HH:mm:ss"),
            SourceEntryId = draft.SourceEntryId,
            Method = draft.Method,
            OriginalUrl = draft.OriginalUrl,
            RequestText = draft.RequestText,
            RealHost = draft.RealHost,
            IsHttps = draft.IsHttps
        };
    }

    private async Task ExecuteReplayAsync(HttpReplayItemViewModel item)
    {
        item.Status = "发送中";
        item.ResultSummary = "正在发送...";
        item.ResponseLatencyText = "…";

        var sendResult = await HttpRequestComposer.SendAsync(
            _httpClientFactory,
            "http-replay",
            TimeSpan.FromSeconds(30),
            item.RequestText,
            item.Method,
            item.RealHost,
            item.IsHttps);

        if (sendResult.Success)
        {
            item.Status = sendResult.StatusCode.ToString();
            item.ResultSummary = $"耗时 {sendResult.LatencyMs} ms";
            item.ResponseLatencyText = $"{sendResult.LatencyMs} ms";
            item.ResponseText = sendResult.ResponseText;
        }
        else
        {
            item.Status = "失败";
            item.ResultSummary = sendResult.LatencyMs > 0
                ? $"耗时 {sendResult.LatencyMs} ms"
                : sendResult.Error;
            item.ResponseLatencyText = sendResult.LatencyMs > 0 ? $"{sendResult.LatencyMs} ms" : "—";
            item.ResponseText = sendResult.Error;
        }
    }

    public void Dispose()
    {
        _replayRequestedSub.Dispose();
    }
}
