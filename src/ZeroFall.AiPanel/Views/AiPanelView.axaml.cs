using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ursa.Controls;
using ZeroFall.AiPanel.Tools.Builtin;
using ZeroFall.AiPanel.Services;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.Dock.Services;
using ZeroFall.Platform.Registries;

namespace ZeroFall.AiPanel.Views;

public partial class AiPanelView : UserControl, IDockTabToolPanelProvider, System.IDisposable
{
    private AiPanelViewModel? _subscribedVm;
    private StackPanel? _dockToolPanel;
    private bool _disposed;

    public AiPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ChatWebView.StatusChanged += OnChatWebViewStatusChanged;
        InputTextBox.AddHandler(
            InputElement.KeyDownEvent,
            OnInputKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        ApplySendButtonIcons();
    }

    public void AttachWebViewWhenReady() => ChatWebView.AttachWebViewWhenReady();

    public void ActivateToolDialogHandler()
    {
        if (_subscribedVm is not null)
            _subscribedVm.AskToolService.AskDialogHandler = ShowAskDialogAsync;
    }

    private void OnChatWebViewStatusChanged(object? sender, AiChatWebViewStatusEventArgs e)
    {
        ChatStatusText.Text = e.Message;
        ChatStatusText.IsVisible = !e.IsReady;
    }

    private void ApplySendButtonIcons()
    {
        SendIcon.Data = DockTabToolPanelHelper.ResolveIcon("SemiIconSend");
        StopIcon.Data = DockTabToolPanelHelper.ResolveIcon("SemiIconStop");
    }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private static StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconPlus",
            nameof(AiPanelViewModel.NewConversationCommand),
            tooltip: "新建对话");
        return panel;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        var send = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                   || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!send)
            return;

        if (DataContext is not AiPanelViewModel vm)
            return;

        // 发送中 CanSendOrStop 为 true 时会走 CancelSending；Ctrl/Shift+Enter 仅用于发起发送，停止请点按钮。
        if (vm.IsSending)
            return;

        if (vm.SendOrStopCommand.CanExecute(null))
            vm.SendOrStopCommand.Execute(null);

        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedVm is not null
            && _subscribedVm.AskToolService.AskDialogHandler == ShowAskDialogAsync)
        {
            _subscribedVm.AskToolService.AskDialogHandler = null;
        }

        _subscribedVm = DataContext as AiPanelViewModel;
        ActivateToolDialogHandler();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DataContextChanged -= OnDataContextChanged;
        ChatWebView.StatusChanged -= OnChatWebViewStatusChanged;
        if (_subscribedVm is not null
            && _subscribedVm.AskToolService.AskDialogHandler == ShowAskDialogAsync)
        {
            _subscribedVm.AskToolService.AskDialogHandler = null;
        }

        _subscribedVm = null;
        ChatWebView.ReleaseResources();
        DataContext = null;
        _dockToolPanel = null;
    }

    private async Task<AskResult> ShowAskDialogAsync(AskDialogViewModel vm)
    {
        var owner = AppDialogService.ResolveOwner(this);
        if (owner is null)
            return new AskResult([], string.Empty, true);

        var result = await Dialog.ShowStandardAsync<AskDialogView, AskDialogViewModel>(
            vm,
            owner,
            new DialogOptions
            {
                Title = "AI 询问",
                Button = DialogButton.None,
                CanResize = false,
                StartupLocation = WindowStartupLocation.CenterOwner,
            });

        return vm.ToResult(result);
    }
}
