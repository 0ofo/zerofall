using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaEdit;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.SqlEditor.ViewModels;

namespace ZeroFall.SqlEditor.Views;

public partial class SqlEditorView : UserControl, IDockTabToolPanelProvider, ITabContentReleasable
{
    private SqlEditorViewModel? _currentViewModel;
    private StackPanel? _dockToolPanel;
    private bool _resourcesReleased;

    public SqlEditorView()
    {
        InitializeComponent();

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeChanged;

        ApplyEditorHighlighting();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        CodeSyntaxHighlighting.InvalidateCache();
        ApplyEditorHighlighting();
    }

    private void ApplyEditorHighlighting()
    {
        var editor = this.FindControl<TextEditor>("Editor");
        if (editor == null)
            return;

        editor.SyntaxHighlighting = CodeSyntaxHighlighting.GetDefinition("SQL");
        editor.TextArea.Options.IndentationSize = 2;
        AvaloniaEditEditorHelper.ApplyTheme(editor);
    }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private static StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "执行",
            nameof(SqlEditorViewModel.ExecuteCommand));
        return panel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        UnsubscribeViewModel();

        if (DataContext is SqlEditorViewModel viewModel)
        {
            _currentViewModel = viewModel;

            var editor = this.FindControl<TextEditor>("Editor");
            if (editor != null)
            {
                editor.Text = viewModel.Sql;
                editor.TextChanged += (_, _) =>
                {
                    viewModel.Sql = editor.Text;
                };
            }
        }
    }

    private void UnsubscribeViewModel()
    {
        _currentViewModel = null;
    }

    public void ReleaseTabResources()
    {
        if (_resourcesReleased)
            return;

        _resourcesReleased = true;

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnThemeChanged;

        var editor = this.FindControl<TextEditor>("Editor");
        if (editor != null)
            editor.Text = string.Empty;

        if (DataContext is IDisposable disposable)
            disposable.Dispose();

        DataContext = null;
        _dockToolPanel = null;
        UnsubscribeViewModel();
    }
}
