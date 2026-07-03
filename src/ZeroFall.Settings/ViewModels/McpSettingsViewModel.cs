using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class McpSettingsViewModel : ViewModelBase, ISettingsSaveable
{
    private readonly ISettingsService _settingsService;
    private readonly IMcpServerProbe _mcpServerProbe;

    public string? LastSaveError => HasError ? ErrorMessage : null;

    [ObservableProperty]
    private bool _mcpEnabled;

    [ObservableProperty]
    private McpServerEntryViewModel? _selectedServer;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    public ObservableCollection<McpServerEntryViewModel> Servers { get; } = [];

    public McpSettingsViewModel(ISettingsService settingsService, IMcpServerProbe mcpServerProbe)
    {
        _settingsService = settingsService;
        _mcpServerProbe = mcpServerProbe;
        LoadConfig();
    }

    internal static List<string> SplitArgLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new List<string>();
        return Regex.Split(line.Trim(), @"\s+").Where(s => s.Length > 0).ToList();
    }

    private void LoadConfig()
    {
        var config = _settingsService.Load().Ai;
        McpEnabled = config.McpEnabled;
        Servers.Clear();
        foreach (var srv in config.McpServers)
            Servers.Add(McpServerEntryViewModel.FromConfig(srv));

        SelectedServer = Servers.FirstOrDefault();
        HasError = false;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void AddOpenWebSearchServer()
    {
        var existing = Servers.FirstOrDefault(s =>
            string.Equals(s.Id, OpenWebSearchMcpPreset.ServerId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedServer = existing;
            StatusMessage = "已存在 open-websearch 服务器配置。";
            return;
        }

        var entry = McpServerEntryViewModel.FromConfig(OpenWebSearchMcpPreset.CreateHttpLocal());
        Servers.Add(entry);
        SelectedServer = entry;
        StatusMessage = "已添加 open-websearch 预设，保存后生效。";
    }

    [RelayCommand]
    private void AddServer()
    {
        var index = Servers.Count + 1;
        var id = $"server-{index}";
        while (Servers.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            id = $"server-{index}";
        }

        var entry = new McpServerEntryViewModel { Id = id, Enabled = true };
        Servers.Add(entry);
        SelectedServer = entry;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void RemoveSelectedServer()
    {
        if (SelectedServer == null)
            return;

        var idx = Servers.IndexOf(SelectedServer);
        Servers.Remove(SelectedServer);
        if (Servers.Count == 0)
            SelectedServer = null;
        else
            SelectedServer = Servers[Math.Min(idx, Servers.Count - 1)];

        StatusMessage = string.Empty;
    }

    public bool TrySave() => SaveCore();

    [RelayCommand]
    private void Save() => SaveCore();

    private bool SaveCore()
    {
        if (McpEnabled && Servers.Count == 0)
        {
            ErrorMessage = "已启用 MCP 时请至少添加一个服务器，或关闭「启用 MCP」。";
            HasError = true;
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var srv in Servers)
        {
            var id = string.IsNullOrWhiteSpace(srv.Id) ? "default" : srv.Id.Trim();
            if (!ids.Add(id))
            {
                ErrorMessage = $"MCP 服务器 Id 重复：{id}";
                HasError = true;
                return false;
            }

            if (!srv.Enabled)
                continue;

            if (srv.UseHttp)
            {
                if (string.IsNullOrWhiteSpace(srv.HttpEndpoint)
                    || !Uri.TryCreate(srv.HttpEndpoint.Trim(), UriKind.Absolute, out _))
                {
                    ErrorMessage = $"「{id}」：HTTP 模式需要有效的绝对 URL 端点";
                    HasError = true;
                    return false;
                }
            }
            else if (string.IsNullOrWhiteSpace(srv.StdioCommand))
            {
                ErrorMessage = $"「{id}」：stdio 模式需要填写启动命令";
                HasError = true;
                return false;
            }
        }

        try
        {
            var settings = _settingsService.Load();
            settings.Ai.McpEnabled = McpEnabled;
            settings.Ai.McpServers = Servers.Select(s => s.ToConfig()).ToList();

            if (!_settingsService.Save(settings))
            {
                ErrorMessage = _settingsService.LastError ?? "保存失败";
                HasError = true;
                return false;
            }

            HasError = false;
            ErrorMessage = string.Empty;
            StatusMessage = "保存成功，AI 面板将自动重连 MCP。";
            LoadConfig();
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存失败: {ex.Message}";
            HasError = true;
            return false;
        }
    }

    [RelayCommand]
    private async Task TestSelectedServerAsync()
    {
        if (SelectedServer == null)
        {
            ErrorMessage = "请先选择一个 MCP 服务器";
            HasError = true;
            return;
        }

        HasError = false;
        ErrorMessage = string.Empty;
        IsTesting = true;
        StatusMessage = "正在连接…";

        try
        {
            var cfg = SelectedServer.ToConfig();
            if (SelectedServer.UseHttp)
            {
                if (string.IsNullOrWhiteSpace(cfg.HttpEndpoint)
                    || !Uri.TryCreate(cfg.HttpEndpoint.Trim(), UriKind.Absolute, out _))
                {
                    StatusMessage = "HTTP 模式需要有效的绝对 URL 端点（如 http://localhost:3000/mcp）";
                    return;
                }
            }
            else if (string.IsNullOrWhiteSpace(cfg.Command))
            {
                StatusMessage = "stdio 模式需要填写启动命令";
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await _mcpServerProbe.ProbeAsync(cfg, cts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = result.Message);
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = "连接超时（90s）。stdio 首次 npx 较慢；HTTP 模式请先运行 npx open-websearch serve。");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"测试失败: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsTesting = false);
        }
    }
}
