using System.Threading.Tasks;
using Datafinder.Platform.Models;
using Datafinder.Platform.Services;

namespace Datafinder.Platform.AppServices;

public interface IDatafinderApiService
{
    HealthSnapshot GetHealth();
    AppSettings GetSettings();
    ApiActionResult SaveSettings(AppSettings settings);

    Workspace? GetWorkspace();
    Task<ApiActionResult<Workspace>> OpenWorkspaceAsync(string directoryPath);
    Task<ApiActionResult> CloseWorkspaceAsync();

    Task<ApiActionResult> CreateFolderAsync(string parentDirectory, string folderName);
    Task<ApiActionResult> MoveEntryAsync(string sourcePath, string targetDirectory);
    Task<ApiActionResult> RenameEntryAsync(string sourcePath, string newName);
    Task<ApiActionResult> DeleteEntryAsync(string path);

    ProxyRuntimeState GetProxyState();
    Task<ApiActionResult<ProxyRuntimeState>> SwitchProxyAsync(string mode, string? upstreamProxyUrl = null);
    Task<ApiActionResult<ProxyConnectivityResult>> TestProxyConnectivityAsync();
}
