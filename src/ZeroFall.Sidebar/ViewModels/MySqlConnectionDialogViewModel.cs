using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Platform.Models;

namespace ZeroFall.Sidebar.ViewModels;

public partial class MySqlConnectionDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "本地 MySQL";

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private string _port = "3306";

    [ObservableProperty]
    private string _database = string.Empty;

    [ObservableProperty]
    private string _user = "root";

    [ObservableProperty]
    private string _password = string.Empty;

    public MySqlConnectionConfig ToConfig()
    {
        if (!int.TryParse(Port?.Trim(), out var port) || port <= 0)
            port = 3306;

        return new MySqlConnectionConfig
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "MySQL" : Name.Trim(),
            Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim(),
            Port = port,
            Database = Database?.Trim() ?? string.Empty,
            User = string.IsNullOrWhiteSpace(User) ? "root" : User.Trim(),
            Password = Password ?? string.Empty
        };
    }
}
