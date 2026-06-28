using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Datafinder.Base.Events;
using Datafinder.Platform.Events;
using Datafinder.Platform.Models;
using Datafinder.Platform.Serialization;

namespace Datafinder.Platform.Services;

public sealed class SettingsServiceImpl : ISettingsService
{
    private readonly IEventBus _eventBus;

    private string? _activeDir;
    private AppSettings? _cached;
    private string? _lastError;

    public SettingsServiceImpl(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public string? LastError => _lastError;

    /// <summary>当前生效的配置目录（保存成功后才有值）。</summary>
    public string? SettingsDirectory => _activeDir;

    public AppSettings Load()
    {
        if (_cached != null)
            return _cached;

        _lastError = null;

        foreach (var dir in GetCandidateDirectories())
        {
            var path = Path.Combine(dir, "settings.json");
            if (!File.Exists(path))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                _cached = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                NormalizeAndMigrate(_cached);
                ActivateDirectory(dir);
                return _cached;
            }
            catch (Exception ex)
            {
                _lastError = $"加载配置失败 ({FormatPath(path)}): {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Load failed: {ex}");
            }
        }

        foreach (var dir in GetCandidateDirectories())
        {
            var backup = Path.Combine(dir, "settings.json.bak");
            if (!File.Exists(backup))
                continue;
            try
            {
                var backupJson = File.ReadAllText(backup);
                _cached = JsonSerializer.Deserialize(backupJson, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                NormalizeAndMigrate(_cached);
                ActivateDirectory(dir);
                return _cached;
            }
            catch
            {
            }
        }

        _cached = new AppSettings();
        NormalizeAndMigrate(_cached);
        return _cached;
    }

    public bool Save(AppSettings settings)
    {
        _lastError = null;
        NormalizeAndMigrate(settings);

        var failures = new List<string>();
        foreach (var dir in GetCandidateDirectories())
        {
            try
            {
                if (SaveToDirectory(dir, settings))
                {
                    ActivateDirectory(dir);
                    _cached = settings;
                    _eventBus.Publish(new AppSettingsSavedEvent());
                    return true;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                failures.Add($"{FormatPath(dir)}: 没有写入权限 ({ex.Message})");
            }
            catch (IOException ex)
            {
                failures.Add($"{FormatPath(dir)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"{FormatPath(dir)}: {ex.Message}");
            }
        }

        _lastError = failures.Count > 0
            ? $"保存配置失败，已尝试：{string.Join("；", failures)}"
            : "保存配置失败: 无法确定可写的配置目录";
        System.Diagnostics.Debug.WriteLine($"[SettingsService] Save failed: {_lastError}");
        return false;
    }

    public void InvalidateCache()
    {
        _cached = null;
        _activeDir = null;
    }

    private bool SaveToDirectory(string directory, AppSettings settings)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);

        var settingsPath = Path.Combine(directory, "settings.json");
        var backupPath = Path.Combine(directory, "settings.json.bak");

        VerifyWritable(directory);

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

        if (File.Exists(settingsPath))
        {
            try
            {
                File.Copy(settingsPath, backupPath, overwrite: true);
            }
            catch
            {
            }
        }

        var tmpPath = settingsPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, settingsPath, overwrite: true);
        return true;
    }

    private static void VerifyWritable(string directory)
    {
        var probe = Path.Combine(directory, $".df_write_{Guid.NewGuid():N}");
        File.WriteAllText(probe, "1");
        File.Delete(probe);
    }

    private void ActivateDirectory(string directory)
    {
        _activeDir = Path.GetFullPath(directory);
    }

    /// <summary>优先 LocalAppData（权限问题较少），其次 Roaming，最后 Temp。</summary>
    private static IEnumerable<string> GetCandidateDirectories()
    {
        static string? CombineSafe(Environment.SpecialFolder folder, string name)
        {
            var root = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, name);
        }

        var list = new[]
        {
            CombineSafe(Environment.SpecialFolder.LocalApplicationData, "Datafinder"),
            CombineSafe(Environment.SpecialFolder.ApplicationData, "Datafinder"),
            Path.Combine(Path.GetTempPath(), "Datafinder")
        };

        return list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    /// <summary>错误信息中用正斜杠，避免 \\a 等被 UI/日志当成转义显示成 Usersla。</summary>
    private static string FormatPath(string path) => path.Replace('\\', '/');

    private static void NormalizeAndMigrate(AppSettings settings)
    {
        settings.Proxy ??= new ProxySettings();
        settings.Proxy.BypassHosts ??= [];
        settings.Proxy.ReplaceRules ??= [];

        if (string.IsNullOrWhiteSpace(settings.Proxy.Mode))
            settings.Proxy.Mode = ProxyModes.Direct;

        settings.Proxy.GatewayHost = string.IsNullOrWhiteSpace(settings.Proxy.GatewayHost)
            ? "127.0.0.1"
            : settings.Proxy.GatewayHost.Trim();

        if (settings.Proxy.GatewayPort <= 0 || settings.Proxy.GatewayPort > 65535)
            settings.Proxy.GatewayPort = 18080;

        if (settings.Proxy.Mode != ProxyModes.Direct
            && settings.Proxy.Mode != ProxyModes.System
            && settings.Proxy.Mode != ProxyModes.Fixed
            && settings.Proxy.Mode != ProxyModes.FluxzyGateway)
        {
            settings.Proxy.Mode = ProxyModes.Direct;
        }
    }
}
