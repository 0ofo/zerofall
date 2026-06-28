using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Datafinder.Platform.Models;

public partial class Workspace : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private DateTime _openedAt = DateTime.UtcNow;

    public string DatabasePath => string.IsNullOrEmpty(DirectoryPath)
        ? string.Empty
        : Path.Combine(DirectoryPath, ".datafinder.db");

    public static Workspace FromDirectory(string directoryPath)
    {
        return new Workspace
        {
            Name = Path.GetFileName(directoryPath) ?? "Untitled",
            DirectoryPath = directoryPath
        };
    }
}
