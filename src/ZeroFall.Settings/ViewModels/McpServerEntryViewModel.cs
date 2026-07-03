using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Models;

namespace ZeroFall.Settings.ViewModels;

public partial class McpServerEntryViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _id = "default";

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private bool _useHttp;

    [ObservableProperty]
    private string _stdioCommand = string.Empty;

    [ObservableProperty]
    private string _stdioArgs = string.Empty;

    [ObservableProperty]
    private string _httpEndpoint = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private string _stdioEnv = string.Empty;

    public string ListTitle
    {
        get
        {
            var transport = UseHttp ? "HTTP" : "stdio";
            var state = Enabled ? string.Empty : " · 已禁用";
            return $"{Id} ({transport}){state}";
        }
    }

    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(ListTitle));
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(ListTitle));
    partial void OnUseHttpChanged(bool value) => OnPropertyChanged(nameof(ListTitle));

    public static McpServerEntryViewModel FromConfig(AiMcpServerConfig cfg)
    {
        var vm = new McpServerEntryViewModel
        {
            Id = string.IsNullOrWhiteSpace(cfg.Id) ? "default" : cfg.Id,
            Enabled = cfg.Enabled,
            UseHttp = string.Equals(cfg.Transport, "http", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(cfg.Transport, "sse", StringComparison.OrdinalIgnoreCase),
            StdioCommand = cfg.Command ?? string.Empty,
            StdioArgs = cfg.Arguments.Count > 0 ? string.Join(" ", cfg.Arguments) : string.Empty,
            HttpEndpoint = cfg.HttpEndpoint ?? string.Empty,
            WorkingDirectory = cfg.WorkingDirectory ?? string.Empty,
            StdioEnv = FormatEnvironmentVariables(cfg.EnvironmentVariables)
        };
        return vm;
    }

    public AiMcpServerConfig ToConfig()
    {
        var id = string.IsNullOrWhiteSpace(Id) ? "default" : Id.Trim();
        if (UseHttp)
        {
            return new AiMcpServerConfig
            {
                Id = id,
                Enabled = Enabled,
                Transport = "http",
                Command = string.Empty,
                Arguments = new(),
                HttpEndpoint = HttpEndpoint.Trim(),
                WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
                EnvironmentVariables = ParseEnvironmentVariables(StdioEnv)
            };
        }

        return new AiMcpServerConfig
        {
            Id = id,
            Enabled = Enabled,
            Transport = "stdio",
            Command = StdioCommand.Trim(),
            Arguments = McpSettingsViewModel.SplitArgLine(StdioArgs),
            HttpEndpoint = null,
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
            EnvironmentVariables = ParseEnvironmentVariables(StdioEnv)
        };
    }

    internal static string FormatEnvironmentVariables(Dictionary<string, string>? env)
    {
        if (env is not { Count: > 0 })
            return string.Empty;

        return string.Join(Environment.NewLine, env.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    internal static Dictionary<string, string>? ParseEnvironmentVariables(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
                continue;

            dict[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }

        return dict.Count == 0 ? null : dict;
    }
}
