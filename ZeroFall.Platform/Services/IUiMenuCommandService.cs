namespace ZeroFall.Platform.Services;

public interface IUiMenuCommandService
{
    string Execute(string commandId, UiMenuCommandArgs? args = null);
}
