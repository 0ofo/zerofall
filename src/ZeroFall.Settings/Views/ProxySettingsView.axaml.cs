using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings.Views.Settings;

public partial class ProxySettingsView : UserControl
{
    public ProxySettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ProxySettingsViewModel vm)
            vm.SaveProxyCertificateFileAsync = SaveProxyCertificateFileAsync;
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ProxySettingsViewModel vm)
            vm.SaveProxyCertificateFileAsync = null;
    }

    private async Task<bool> SaveProxyCertificateFileAsync(string suggestedFileName, byte[] pemBytes)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return false;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Fluxzy 根证书",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "pem",
            FileTypeChoices =
            [
                new FilePickerFileType("PEM 证书") { Patterns = ["*.pem", "*.crt"] }
            ]
        });

        if (file is null)
            return false;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(pemBytes);
        return true;
    }
}
