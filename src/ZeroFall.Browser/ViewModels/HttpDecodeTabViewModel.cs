using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.ViewModels;

public partial class HttpDecodeTabViewModel : BrowserTabViewModelBase, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IDisposable _decodeRequestedSub;

    [ObservableProperty]
    private string _sourceLabel = "手动输入";

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _statusText = "从流量表右键「发送到 Decoder」，或粘贴文本后选择解码方式。";

    public HttpDecodeTabViewModel(IEventBus eventBus)
    {
        _eventBus = eventBus;
        Title = "Decoder";
        _decodeRequestedSub = eventBus.SubscribeDisposable<HttpDecodeRequestedEvent>(OnDecodeRequested);
    }

    private void OnDecodeRequested(HttpDecodeRequestedEvent e)
    {
        SourceLabel = e.Label;
        InputText = e.InputText;
        OutputText = string.Empty;
        StatusText = "已导入流量数据，请选择解码方式。";
        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Content, "http-decode"));
    }

    [RelayCommand]
    private void RunDecode(string? operationName)
    {
        if (!Enum.TryParse<HttpDecodeOperation>(operationName, ignoreCase: true, out var operation))
            return;

        OutputText = HttpDecoder.Transform(InputText, operation);
        StatusText = operation switch
        {
            HttpDecodeOperation.Smart => "已执行智能解码",
            _ => $"已执行 {operationName}"
        };
    }

    [RelayCommand]
    private void CopyOutputToInput()
    {
        if (string.IsNullOrEmpty(OutputText))
            return;

        InputText = OutputText;
        StatusText = "已将输出复制到输入区，可继续链式解码。";
    }

    public void Dispose() => _decodeRequestedSub.Dispose();
}
