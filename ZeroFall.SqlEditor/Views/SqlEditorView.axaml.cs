using System;
using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.Platform.Registries;
using ZeroFall.SqlEditor.ViewModels;

namespace ZeroFall.SqlEditor.Views;

public partial class SqlEditorView : UserControl, IDockTabToolPanelProvider
{
    private SqlEditorViewModel? _currentViewModel;
    private StackPanel? _dockToolPanel;

    public SqlEditorView()
    {
        InitializeComponent();

        var editor = this.FindControl<TextEditor>("Editor");
        if (editor != null)
        {
            editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");
            editor.TextArea.Options.IndentationSize = 2;
        }
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
        if (_currentViewModel != null)
        {
            _currentViewModel = null;
        }
    }
}
