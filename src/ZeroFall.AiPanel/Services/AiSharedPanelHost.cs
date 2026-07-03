using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.AiPanel.Views;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>按会话持有独立 AI 面板实例；每个 Tab 懒创建自己的 AiPanelView / AiChatWebView。</summary>
public sealed class AiSharedPanelHost
{
    private readonly IServiceProvider _services;
    private readonly AiChatSessionContext _sessionContext;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public AiSharedPanelHost(IServiceProvider services, AiChatSessionContext sessionContext)
    {
        _services = services;
        _sessionContext = sessionContext;
    }

    public async Task ActivateSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var entry = EnsureEntry(sessionId);
            _sessionContext.SetSessionId(sessionId);
            entry.View.ActivateToolDialogHandler();
            entry.View.AttachWebViewWhenReady();
            entry.ViewModel.RequestChatSurfaceResync();
        });
    }

    public void AttachWebViewWhenReady(string sessionId) =>
        Dispatcher.UIThread.Post(() => EnsureEntry(sessionId).View.AttachWebViewWhenReady());

    public Control? GetDockTabToolPanel(string sessionId) =>
        EnsureEntry(sessionId).View is IDockTabToolPanelProvider provider ? provider.GetDockTabToolPanel() : null;

    public AiPanelView GetOrCreateView(string sessionId) => EnsureEntry(sessionId).View;

    public void ReleaseSession(string sessionId)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        if (!_entries.Remove(sessionId, out var entry))
            return;

        entry.View.Dispose();
        entry.ViewModel.Dispose();
    }

    /// <summary>主窗口关闭前：把所有已打开会话 Tab 的消息落盘。</summary>
    public Task FlushAllSessionsAsync()
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            foreach (var entry in _entries.Values.ToList())
                await entry.ViewModel.FlushPersistAsync().ConfigureAwait(false);
        });
    }

    private Entry EnsureEntry(string sessionId)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_entries.TryGetValue(sessionId, out var entry))
            return entry;

        var viewModel = ActivatorUtilities.CreateInstance<AiPanelViewModel>(
            _services,
            new AiPanelViewModelLifetime(isCoordinatorInstance: false));
        var view = new AiPanelView { DataContext = viewModel };
        entry = new Entry(viewModel, view);
        _entries[sessionId] = entry;
        _ = viewModel.SwitchSessionAsync(sessionId);
        return entry;
    }

    private sealed record Entry(AiPanelViewModel ViewModel, AiPanelView View);
}
