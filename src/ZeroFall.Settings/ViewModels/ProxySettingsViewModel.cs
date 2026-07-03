using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class ProxySettingsViewModel : ViewModelBase, ISettingsSaveable
{
    private readonly ISettingsService _settingsService;
    private readonly IEventBus _eventBus;
    private readonly IProxyGatewayService _proxyGatewayService;

    private string? _lastSaveError;

    public string? LastSaveError => _lastSaveError;

    public ObservableCollection<string> ListenAddressOptions { get; } = new();

    public ObservableCollection<ProxyReplaceRuleViewModel> ReplaceRules { get; } = [];

    [ObservableProperty]
    private ProxyReplaceRuleViewModel? _selectedReplaceRule;

    [ObservableProperty]
    private string _upstreamProxyUrl = string.Empty;

    [ObservableProperty]
    private int _proxyModeIndex;

    [ObservableProperty]
    private string _proxyGatewayHost = "127.0.0.1";

    [ObservableProperty]
    private int _proxyGatewayPort = 18080;

    [ObservableProperty]
    private bool _httpsInterceptionEnabled;

    [ObservableProperty]
    private bool _listenerEnabled;

    [ObservableProperty]
    private string _proxyRuntimeMessage = string.Empty;

    /// <summary>由 View 注入：导出 CA 证书到用户选择的路径。</summary>
    public Func<string, byte[], Task<bool>>? SaveProxyCertificateFileAsync { get; set; }

    public ProxySettingsViewModel(
        ISettingsService settingsService,
        IEventBus eventBus,
        IProxyGatewayService proxyGatewayService)
    {
        _settingsService = settingsService;
        _eventBus = eventBus;
        _proxyGatewayService = proxyGatewayService;
        ReloadListenAddressOptions();
        LoadConfig();
        RefreshProxyRuntimeMessage();
        SubscribeEvent(eventBus, (ProxyRuntimeStateChangedEvent _) => RefreshProxyRuntimeMessage());
    }

    private void ReloadListenAddressOptions()
    {
        ListenAddressOptions.Clear();
        foreach (var item in ProxyListenAddressOptions.Build())
            ListenAddressOptions.Add(item);
    }

    private void LoadConfig()
    {
        var settings = _settingsService.Load();
        UpstreamProxyUrl = settings.Proxy.UpstreamProxyUrl ?? string.Empty;
        ProxyGatewayHost = settings.Proxy.GatewayHost;
        ProxyGatewayPort = settings.Proxy.GatewayPort;
        HttpsInterceptionEnabled = settings.Proxy.HttpsInterceptionEnabled;
        ListenerEnabled = settings.Proxy.ListenerEnabled;
        ProxyModeIndex = settings.Proxy.Mode switch
        {
            ProxyModes.System => 1,
            ProxyModes.Fixed => 2,
            ProxyModes.FluxzyGateway => 3,
            _ => 0
        };

        if (!ListenAddressOptions.Contains(ProxyGatewayHost))
            ListenAddressOptions.Add(ProxyGatewayHost);

        ReplaceRules.Clear();
        foreach (var rule in settings.Proxy.ReplaceRules)
            ReplaceRules.Add(ProxyReplaceRuleViewModel.FromConfig(rule));
        SelectedReplaceRule = ReplaceRules.FirstOrDefault();
    }

    private void RefreshProxyRuntimeMessage()
    {
        var state = _proxyGatewayService.CurrentState;
        ProxyRuntimeMessage = state.Message ?? string.Empty;
    }

    public bool TrySave() => SaveCore();

    [RelayCommand]
    private void Save() => SaveCore();

    [RelayCommand]
    private void AddReplaceRule()
    {
        var rule = new ProxyReplaceRuleViewModel { Enabled = true };
        ReplaceRules.Add(rule);
        SelectedReplaceRule = rule;
    }

    [RelayCommand]
    private void RemoveSelectedReplaceRule()
    {
        if (SelectedReplaceRule is null)
            return;

        var idx = ReplaceRules.IndexOf(SelectedReplaceRule);
        ReplaceRules.Remove(SelectedReplaceRule);
        SelectedReplaceRule = ReplaceRules.Count == 0
            ? null
            : ReplaceRules[Math.Min(idx, ReplaceRules.Count - 1)];
    }

    [RelayCommand]
    private async Task ExportProxyCertificateAsync()
    {
        if (SaveProxyCertificateFileAsync is null)
            return;

        var pem = _proxyGatewayService.ExportRootCertificatePem();
        if (pem is null || pem.Length == 0)
        {
            ProxyRuntimeMessage = "导出失败：Fluxzy 根证书不可用。";
            return;
        }

        var saved = await SaveProxyCertificateFileAsync("fluxzy-ca.pem", pem);
        ProxyRuntimeMessage = saved ? "根证书已导出。" : "已取消导出。";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SaveProxyCertificateFileAsync = null;

        base.Dispose(disposing);
    }

    private bool SaveCore()
    {
        _lastSaveError = null;
        try
        {
            var settings = _settingsService.Load();
            settings.Proxy.Mode = ProxyModeIndex switch
            {
                1 => ProxyModes.System,
                2 => ProxyModes.Fixed,
                3 => ProxyModes.FluxzyGateway,
                _ => ProxyModes.Direct
            };
            settings.Proxy.UpstreamProxyUrl = UpstreamProxyUrl.Trim();
            settings.Proxy.GatewayHost = string.IsNullOrWhiteSpace(ProxyGatewayHost) ? "127.0.0.1" : ProxyGatewayHost.Trim();
            settings.Proxy.GatewayPort = ProxyGatewayPort <= 0 ? 18080 : ProxyGatewayPort;
            settings.Proxy.HttpsInterceptionEnabled = HttpsInterceptionEnabled;
            settings.Proxy.ListenerEnabled = ListenerEnabled;
            settings.Proxy.ReplaceRules = ReplaceRules.Select(static x => x.ToConfig()).ToList();
            if (_settingsService.Save(settings))
            {
                _eventBus.Publish(new ProxySettingsChangedEvent(settings.Proxy));
                RefreshProxyRuntimeMessage();
                return true;
            }

            _lastSaveError = _settingsService.LastError ?? "代理设置保存失败";
            System.Diagnostics.Debug.WriteLine($"[ProxySettings] Save failed: {_lastSaveError}");
            return false;
        }
        catch (Exception ex)
        {
            _lastSaveError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[ProxySettings] Save failed: {ex}");
            return false;
        }
    }
}
