using Avalonia.Controls;
using ZeroFall.AiPanel.Services;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Views;

/// <summary>AI 会话 Tab 的不可重载壳；每个会话持有自己的 AI 面板。</summary>
public sealed class AiSessionTabShell : Panel, INonReloadableTabShell, IDockTabToolPanelProvider
{
    private readonly AiSharedPanelHost _sharedHost;

    public AiSessionTabShell(AiSharedPanelHost sharedHost, string sessionId)
    {
        _sharedHost = sharedHost;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public Control? PersistedContent => _sharedHost.GetOrCreateView(SessionId);

    public void OnTabBecameVisible()
    {
        _ = _sharedHost.ActivateSessionAsync(SessionId);
    }

    public void OnTabBecameHidden()
    {
    }

    public Control? GetDockTabToolPanel() => _sharedHost.GetDockTabToolPanel(SessionId);
}
