using System;
using System.Threading.Tasks;
using Datafinder.Platform.Models;
using Datafinder.Platform.Services;

namespace Datafinder.Platform.AppServices;

public sealed class DatafinderApiService : IDatafinderApiService
{
    private readonly ISettingsService _settingsService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IProjectService _projectService;
    private readonly IProxyGatewayService _proxyGatewayService;
    private readonly ProxyRuntimeCoordinator _proxyCoordinator;

    public DatafinderApiService(
        ISettingsService settingsService,
        IWorkspaceService workspaceService,
        IProjectService projectService,
        IProxyGatewayService proxyGatewayService,
        ProxyRuntimeCoordinator proxyCoordinator)
    {
        _settingsService = settingsService;
        _workspaceService = workspaceService;
        _projectService = projectService;
        _proxyGatewayService = proxyGatewayService;
        _proxyCoordinator = proxyCoordinator;
    }

    public HealthSnapshot GetHealth()
    {
        return new HealthSnapshot("ok", DateTimeOffset.UtcNow, _workspaceService.HasWorkspace);
    }

    public AppSettings GetSettings() => _settingsService.Load();

    public ApiActionResult SaveSettings(AppSettings settings)
    {
        var ok = _settingsService.Save(settings);
        return ok
            ? ApiActionResult.Ok("保存成功")
            : ApiActionResult.Fail(_settingsService.LastError ?? "保存失败");
    }

    public Workspace? GetWorkspace() => _workspaceService.CurrentWorkspace;

    public async Task<ApiActionResult<Workspace>> OpenWorkspaceAsync(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ApiActionResult<Workspace>.Fail("DirectoryPath 不能为空");
        }

        await _workspaceService.OpenWorkspaceAsync(directoryPath);
        return ApiActionResult<Workspace>.Ok(_workspaceService.CurrentWorkspace, "工作区已打开");
    }

    public async Task<ApiActionResult> CloseWorkspaceAsync()
    {
        await _workspaceService.CloseWorkspaceAsync();
        return ApiActionResult.Ok("工作区已关闭");
    }

    public async Task<ApiActionResult> CreateFolderAsync(string parentDirectory, string folderName)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(folderName))
        {
            return ApiActionResult.Fail("ParentDirectory 和 FolderName 均不能为空");
        }

        var success = await _projectService.CreateFolderAsync(parentDirectory, folderName);
        return success
            ? ApiActionResult.Ok("文件夹创建成功")
            : ApiActionResult.Fail("文件夹创建失败");
    }

    public async Task<ApiActionResult> MoveEntryAsync(string sourcePath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return ApiActionResult.Fail("SourcePath 和 TargetDirectory 均不能为空");
        }

        var success = await _projectService.MoveEntryAsync(sourcePath, targetDirectory);
        return success
            ? ApiActionResult.Ok("移动成功")
            : ApiActionResult.Fail("移动失败");
    }

    public async Task<ApiActionResult> RenameEntryAsync(string sourcePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(newName))
        {
            return ApiActionResult.Fail("SourcePath 和 NewName 均不能为空");
        }

        var success = await _projectService.RenameEntryAsync(sourcePath, newName);
        return success
            ? ApiActionResult.Ok("重命名成功")
            : ApiActionResult.Fail("重命名失败");
    }

    public async Task<ApiActionResult> DeleteEntryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ApiActionResult.Fail("Path 不能为空");
        }

        var success = await _projectService.DeleteEntryAsync(path);
        return success
            ? ApiActionResult.Ok("删除成功")
            : ApiActionResult.Fail("删除失败");
    }

    public ProxyRuntimeState GetProxyState() => _proxyGatewayService.CurrentState;

    public async Task<ApiActionResult<ProxyRuntimeState>> SwitchProxyAsync(string mode, string? upstreamProxyUrl = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return ApiActionResult<ProxyRuntimeState>.Fail("mode 不能为空");

        var settings = _settingsService.Load();
        settings.Proxy.Mode = mode.Trim();
        settings.Proxy.UpstreamProxyUrl = upstreamProxyUrl?.Trim() ?? string.Empty;
        if (!_settingsService.Save(settings))
            return ApiActionResult<ProxyRuntimeState>.Fail(_settingsService.LastError ?? "保存代理配置失败");

        var state = await _proxyCoordinator.ApplyAsync(settings.Proxy);
        return ApiActionResult<ProxyRuntimeState>.Ok(state, "代理模式已切换");
    }

    public async Task<ApiActionResult<ProxyConnectivityResult>> TestProxyConnectivityAsync()
    {
        var result = await _proxyGatewayService.TestConnectivityAsync();
        return result.Success
            ? ApiActionResult<ProxyConnectivityResult>.Ok(result, result.Message)
            : ApiActionResult<ProxyConnectivityResult>.Fail(result.Message, result.Message);
    }
}
