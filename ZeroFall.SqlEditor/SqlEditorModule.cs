using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.Base;
using ZeroFall.Base.Events;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.SqlEditor.ViewModels;
using ZeroFall.SqlEditor.Views;

namespace ZeroFall.SqlEditor;

public class SqlEditorModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<SqlEditorViewModel>();
        services.AddTransient<FilePreviewViewModel>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var contentFactoryRegistry = sp.GetRequiredService<IContentFactoryRegistry>();
        var menuRegistry = sp.GetRequiredService<IMenuRegistry>();
        var eventBus = sp.GetRequiredService<IEventBus>();

        contentFactoryRegistry.Register(new SqlEditorContentFactory(sp));
        contentFactoryRegistry.Register(new DataTableContentFactory());
        contentFactoryRegistry.Register(new CsvDataContentFactory(sp));
        contentFactoryRegistry.Register(new TextFileContentFactory(sp));
        contentFactoryRegistry.Register(new BinaryFileContentFactory());

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "新建查询",
            MenuPath = "文件",
            MenuGroupOrder = 0,
            Order = 1,
            CommandId = UiMenuCommandIds.NewQuery,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                eventBus.Publish(new NewQueryRequestedEvent(string.Empty, string.Empty)))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "",
            IsSeparator = true,
            MenuPath = "文件",
            MenuGroupOrder = 0,
            Order = 2
        });
    }
}

internal class SqlEditorContentFactory : IContentFactory
{
    private readonly IServiceProvider _sp;
    public SqlEditorContentFactory(IServiceProvider sp) => _sp = sp;

    public string ContentType => "sql-editor";

    public object? CreateContent(ContentFactoryContext context)
    {
        var vm = _sp.GetRequiredService<SqlEditorViewModel>();
        vm.FilePath = context.FilePath ?? string.Empty;
        return new SqlEditorView { DataContext = vm };
    }
}

internal class DataTableContentFactory : IContentFactory
{
    public string ContentType => "data-table";

    public object? CreateContent(ContentFactoryContext context)
    {
        if (context.Extra.TryGetValue("DataTableViewModel", out var dtvmObj) && dtvmObj is DataTableViewModel dtvm)
            return new SqliteDataTableDiagnosticView { DataContext = dtvm };
        return null;
    }
}

internal class CsvDataContentFactory : IContentFactory
{
    private readonly IServiceProvider _sp;
    public CsvDataContentFactory(IServiceProvider sp) => _sp = sp;

    public string ContentType => "csv-data";

    public object? CreateContent(ContentFactoryContext context)
    {
        if (context.FilePath == null) return null;
        try
        {
            var dataTable = DataTableViewModel.FromCsv(context.FilePath);
            var text = FilePreviewViewModel.ReadAllTextShared(context.FilePath);
            return new FilePreviewView(context.FilePath, text, dataTable);
        }
        catch
        {
            return null;
        }
    }
}

internal class TextFileContentFactory : IContentFactory
{
    private readonly IServiceProvider _sp;
    public TextFileContentFactory(IServiceProvider sp) => _sp = sp;

    public string ContentType => "text-file";

    public object? CreateContent(ContentFactoryContext context)
    {
        if (context.FilePath == null || !File.Exists(context.FilePath))
            return null;

        try
        {
            var text = FilePreviewViewModel.ReadAllTextShared(context.FilePath);
            return new FilePreviewView(context.FilePath, text);
        }
        catch
        {
            return null;
        }
    }
}

internal class BinaryFileContentFactory : IContentFactory
{
    public string ContentType => "binary-file";

    public object? CreateContent(ContentFactoryContext context)
    {
        if (context.FilePath == null || !File.Exists(context.FilePath))
            return null;

        return new BinaryFilePreviewView(context.FilePath);
    }
}
