using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

public interface IUiMenuCommandService
{
    string Execute(string commandId, UiMenuCommandArgs? args = null);

    Task<string> ExecuteAsync(string commandId, UiMenuCommandArgs? args = null);
}
