using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Data;
using ZeroFall.Base.Mvvm;
using ZeroFall.DataTable.Services;
using ZeroFall.Platform.Providers;

namespace ZeroFall.DataTable.ViewModels;

public partial class DataTableViewModel : ViewModelBase
{
    private IDataProvider? _provider;
    private Action? _detachProviderTotalHandler;
    private readonly List<DataRowViewModel> _liveSourceRows = new();
    private int _maxLiveEntries = 1000;

    /// <summary>由 <see cref="Views.DataTableView"/> 在可视树挂载时注入，用于选择保存位置并打开写入流。</summary>
    public Func<Task<Stream?>>? OpenCsvSaveStreamAsync { get; set; }

    /// <summary>若设置，点击 URL 列时调用此委托而非系统默认浏览器（例如内嵌打开浏览器标签）。</summary>
    public Action<string>? OpenUrlInApp { get; set; }

    /// <summary>0 导出当前页（或当前内存行），1 导出全部页（遍历 <see cref="IDataProvider"/>）。</summary>
    [ObservableProperty]
    private int _exportScopeIndex;

    /// <summary>是否可对数据源分页拉取「全部页」导出。</summary>
    public bool SupportsAllPageExport => _provider != null;

    partial void OnExportScopeIndexChanged(int value) => ExportCsvCommand.NotifyCanExecuteChanged();

    public DataTableViewModel()
    {
        Rows.CollectionChanged += OnRowsOrColumnsCollectionChanged;
        Columns.CollectionChanged += OnRowsOrColumnsCollectionChanged;
    }

    private void OnRowsOrColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ExportCsvCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SupportsAllPageExport));
    }

    /// <summary>有本地库 <see cref="IDataProvider"/> 时可刷新条数与当前页（非实时流）。</summary>
    public bool CanRefreshLocalData =>
        _provider != null
        && DisplayMode != DataTableDisplayMode.LiveCollection
        && !string.IsNullOrEmpty(_provider.DatabasePath);

    public void RefreshExportCommands()
    {
        ExportCsvCommand.NotifyCanExecuteChanged();
        RefreshLocalDataCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SupportsAllPageExport));
        OnPropertyChanged(nameof(CanRefreshLocalData));
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private DataTableDisplayMode _displayMode = DataTableDisplayMode.VirtualScroll;

    [ObservableProperty]
    private ObservableCollection<DataColumnViewModel> _columns = new();

    [ObservableProperty]
    private ObservableCollection<DataRowViewModel> _rows = new();

    [ObservableProperty]
    private DataRowViewModel? _selectedRow;

    [ObservableProperty]
    private bool _disableUrlColumns;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canEdit;

    [ObservableProperty]
    private bool _canWriteBack;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _sourceType = string.Empty;

    [ObservableProperty]
    private string _sourceFilePath = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _tableName = string.Empty;

    [ObservableProperty]
    private int _rowCount;

    [ObservableProperty]
    private int _columnCount;

    [ObservableProperty]
    private long _totalRows;

    /// <summary>嵌入宿主（如资产测绘）时可关闭顶部标题区。</summary>
    [ObservableProperty]
    private bool _showHeaderPanel;

    /// <summary>是否在首列显示行号（实时流如流量可关闭）。</summary>
    [ObservableProperty]
    private bool _showLineNumberColumn = true;

    partial void OnTotalRowsChanged(long value)
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        RefreshLocalDataCommand.NotifyCanExecuteChanged();
    }

    partial void OnDisplayModeChanged(DataTableDisplayMode value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    #region VirtualScroll Mode

    [ObservableProperty]
    private int _loadedRowCount;

    [ObservableProperty]
    private int _pageSize = 200;

    [ObservableProperty]
    private int _preloadThreshold = 50;

    private bool _isLoadingMore;
    private bool _hasMoreData = true;

    #endregion

    #region UserPaged Mode

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _userPageSize = 200;

    public int TotalPages => UserPageSize > 0 ? (int)Math.Ceiling((double)TotalRows / UserPageSize) : 1;

    public bool HasPreviousPage => CurrentPage > 1;

    public bool HasNextPage => CurrentPage < TotalPages;

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    partial void OnUserPageSizeChanged(int value)
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    #endregion

    public async Task InitializeVirtualScrollAsync(IDataProvider provider)
    {
        _detachProviderTotalHandler?.Invoke();
        _detachProviderTotalHandler = null;
        _provider?.Dispose();
        _provider = provider;
        Title = provider.Title;
        CanEdit = provider.CanEdit;
        CanWriteBack = provider.CanWriteBack;
        IsDirty = provider.IsDirty;
        DisplayMode = DataTableDisplayMode.VirtualScroll;

        AttachProviderTotalNotifications(provider);

        var totalCount = await provider.GetTotalCountAsync();
        TotalRows = totalCount;

        BuildColumns(await ResolveProviderColumnsAsync(provider));

        _hasMoreData = true;
        _isLoadingMore = false;
        LoadedRowCount = 0;
        Rows.Clear();

        await LoadMoreInternalAsync();
        RefreshExportCommands();
    }

    public async Task InitializePagedAsync(IDataProvider provider)
    {
        _detachProviderTotalHandler?.Invoke();
        _detachProviderTotalHandler = null;
        _provider?.Dispose();
        _provider = provider;
        Title = provider.Title;
        CanEdit = provider.CanEdit;
        CanWriteBack = provider.CanWriteBack;
        IsDirty = provider.IsDirty;
        DisplayMode = DataTableDisplayMode.UserPaged;

        AttachProviderTotalNotifications(provider);

        var totalCount = await provider.GetTotalCountAsync();
        TotalRows = totalCount;

        BuildColumns(await ResolveProviderColumnsAsync(provider));

        CurrentPage = 1;
        await LoadUserPageAsync();
        RefreshExportCommands();
    }

    /// <summary>
    /// 初始化实时列表模式（无 <see cref="IDataProvider"/>）。
    /// </summary>
    public void InitializeLive(IReadOnlyList<string> columnHeaders, int maxEntries,
        bool showHeaderPanel = false, bool showLineNumberColumn = false)
    {
        _provider?.Dispose();
        _provider = null;
        _detachProviderTotalHandler?.Invoke();
        _detachProviderTotalHandler = null;
        DisplayMode = DataTableDisplayMode.LiveCollection;
        Title = string.Empty;
        Subtitle = string.Empty;
        CanEdit = false;
        CanWriteBack = false;
        IsDirty = false;
        ShowHeaderPanel = showHeaderPanel;
        ShowLineNumberColumn = showLineNumberColumn;
        _maxLiveEntries = maxEntries;
        _liveSourceRows.Clear();
        Rows.Clear();
        TotalRows = 0;
        BuildColumns(columnHeaders);
        ExportScopeIndex = 0;
        RefreshExportCommands();
    }

    private void AttachProviderTotalNotifications(IDataProvider provider)
    {
        _detachProviderTotalHandler?.Invoke();
        _detachProviderTotalHandler = null;

        if (provider is not INotifyTotalCountEstimateChanged n)
            return;

        void Handler(object? _, TotalCountEstimateChangedEventArgs e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_provider, provider))
                    return;
                TotalRows = e.NewTotal;
            });
        }

        n.TotalCountEstimateChanged += Handler;
        _detachProviderTotalHandler = () => n.TotalCountEstimateChanged -= Handler;
    }

    public void PrependLiveRow(DataRowViewModel row)
    {
        if (DisplayMode != DataTableDisplayMode.LiveCollection) return;
        _liveSourceRows.Insert(0, row);
        Rows.Insert(0, row);
        while (_liveSourceRows.Count > _maxLiveEntries)
        {
            _liveSourceRows.RemoveAt(_liveSourceRows.Count - 1);
            Rows.RemoveAt(Rows.Count - 1);
        }
        TotalRows = _liveSourceRows.Count;
    }

    public void ClearLiveRows()
    {
        _liveSourceRows.Clear();
        Rows.Clear();
        TotalRows = 0;
        SelectedRow = null;
    }

    private void RefreshLiveRows()
    {
        if (DisplayMode != DataTableDisplayMode.LiveCollection) return;
        var savedSelection = SelectedRow;
        Rows.Clear();
        foreach (var r in _liveSourceRows)
            Rows.Add(r);
        TotalRows = _liveSourceRows.Count;
        if (savedSelection != null && _liveSourceRows.Contains(savedSelection))
            SelectedRow = savedSelection;
    }

    public void CheckPreload(int visibleStartIndex, int visibleEndIndex)
    {
        if (DisplayMode != DataTableDisplayMode.VirtualScroll) return;
        if (!_hasMoreData || _isLoadingMore) return;

        if (visibleEndIndex >= LoadedRowCount - PreloadThreshold)
        {
            _ = LoadMoreAsync();
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        await LoadMoreInternalAsync();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteNextPage))]
    public async Task NextPageAsync()
    {
        if (!HasNextPage || _provider == null) return;
        CurrentPage++;
        await LoadUserPageAsync();
    }

    private bool CanExecuteNextPage() =>
        DisplayMode == DataTableDisplayMode.UserPaged && HasNextPage && _provider != null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanExecutePreviousPage))]
    public async Task PreviousPageAsync()
    {
        if (!HasPreviousPage || _provider == null) return;
        CurrentPage--;
        await LoadUserPageAsync();
    }

    private bool CanExecutePreviousPage() =>
        DisplayMode == DataTableDisplayMode.UserPaged && HasPreviousPage && _provider != null && !IsLoading;

    [RelayCommand]
    private async Task SaveRowAsync(DataRowViewModel row)
    {
        if (_provider?.CanEdit != true) return;
        var values = row.Values.Select(v => (object?)v).ToList();
        await _provider.UpdateRowAsync(row.LineNumber, values);
        IsDirty = _provider.IsDirty;
    }

    [RelayCommand]
    private async Task WriteBackAsync()
    {
        if (_provider?.CanWriteBack != true) return;
        IsLoading = true;
        try
        {
            await _provider.WriteBackAsync();
            IsDirty = _provider.IsDirty;
            Subtitle = "已保存到原始文件";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshFromSourceAsync()
    {
        if (_provider == null) return;
        IsLoading = true;
        try
        {
            await _provider.RefreshFromSourceAsync();
            IsDirty = _provider.IsDirty;
            await ReloadFromProviderAsync(resetToFirstPage: true);
            Subtitle = "已从原始文件重新索引";
        }
        finally
        {
            IsLoading = false;
            RefreshExportCommands();
        }
    }

    private bool CanExecuteRefreshLocalData() => CanRefreshLocalData;

    [RelayCommand(CanExecute = nameof(CanExecuteRefreshLocalData))]
    private async Task RefreshLocalDataAsync()
    {
        if (_provider == null || !CanRefreshLocalData) return;
        IsLoading = true;
        try
        {
            await _provider.RefreshFromSourceAsync();
            IsDirty = _provider.IsDirty;
            await ReloadFromProviderAsync(resetToFirstPage: false);
        }
        finally
        {
            IsLoading = false;
            RefreshExportCommands();
        }
    }

    private async Task ReloadFromProviderAsync(bool resetToFirstPage)
    {
        if (_provider == null) return;

        TotalRows = await _provider.GetTotalCountAsync();
        BuildColumns(await ResolveProviderColumnsAsync(_provider));

        if (DisplayMode == DataTableDisplayMode.VirtualScroll)
        {
            Rows.Clear();
            LoadedRowCount = 0;
            _hasMoreData = true;
            await LoadMoreInternalAsync();
            return;
        }

        if (resetToFirstPage)
            CurrentPage = 1;
        else
        {
            var totalPages = TotalPages;
            if (totalPages < 1) totalPages = 1;
            if (CurrentPage > totalPages)
                CurrentPage = totalPages;
        }

        await LoadUserPageAsync();
    }

    private async Task LoadMoreInternalAsync()
    {
        if (_provider == null || _isLoadingMore || !_hasMoreData) return;
        _isLoadingMore = true;
        IsLoading = true;

        try
        {
            var result = await _provider.GetPageAsync(LoadedRowCount, PageSize);
            var addedCount = 0;

            foreach (var row in result.Rows)
            {
                var rowVm = new DataRowViewModel { LineNumber = LoadedRowCount + addedCount + 1 };
                for (var i = 0; i < Columns.Count; i++)
                {
                    rowVm.Values.Add(i < row.Count ? row[i]?.ToString() ?? string.Empty : string.Empty);
                }
                Rows.Add(rowVm);
                addedCount++;
            }

            LoadedRowCount += addedCount;

            if (addedCount < PageSize)
                _hasMoreData = false;
        }
        finally
        {
            _isLoadingMore = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var u = url.Trim();
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "http://" + u;
        if (OpenUrlInApp is { } handler)
        {
            try { handler(u); }
            catch { }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private bool CanExportCsv() =>
        OpenCsvSaveStreamAsync != null
        && Columns.Count > 0
        && Rows.Count > 0
        && !IsLoading
        && (ExportScopeIndex != 1 || _provider != null);

    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private async Task ExportCsvAsync()
    {
        if (OpenCsvSaveStreamAsync == null) return;
        // 保存对话框必须在 UI 线程；ConfigureAwait(true) 避免回到线程池后触碰 Avalonia 绑定集合
        var stream = await OpenCsvSaveStreamAsync.Invoke().ConfigureAwait(true);
        if (stream == null) return;

        IsLoading = true;
        try
        {
            if (ExportScopeIndex == 0)
                await WriteCsvCurrentRowsAsync(stream).ConfigureAwait(true);
            else
                await WriteCsvAllProviderPagesAsync(stream).ConfigureAwait(true);
            Subtitle = ExportScopeIndex == 0 ? "已导出当前页 CSV" : "已导出全部页 CSV";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task WriteCsvCurrentRowsAsync(Stream stream)
    {
        var headerLine = BuildHeaderCsvLine();
        var rowLines = Rows.Select(r => BuildRowCsvLine(r)).ToList();

        await Task.Run(() =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                bufferSize: 65536, leaveOpen: false);
            writer.WriteLine(headerLine);
            foreach (var line in rowLines)
                writer.WriteLine(line);
        }).ConfigureAwait(true);
    }

    private async Task WriteCsvAllProviderPagesAsync(Stream stream)
    {
        if (_provider == null) return;

        var columnCount = Columns.Count;
        var headerLine = string.Join(",", Columns.Select(c => EscapeCsvField(c.Header)));
        var pageSize = DisplayMode == DataTableDisplayMode.UserPaged ? UserPageSize : PageSize;
        var effectivePageSize = pageSize > 0 ? pageSize : 200;
        var provider = _provider;

        await Task.Run(async () =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                bufferSize: 65536, leaveOpen: false);
            await writer.WriteLineAsync(headerLine).ConfigureAwait(false);

            var total = await provider.GetTotalCountAsync().ConfigureAwait(false);
            var offset = 0;
            while (offset < total)
            {
                var page = await provider.GetPageAsync(offset, effectivePageSize).ConfigureAwait(false);
                foreach (var row in page.Rows)
                    await writer.WriteLineAsync(FormatDataRowForCsv(row, columnCount)).ConfigureAwait(false);
                if (page.Count == 0) break;
                offset += page.Count;
            }
        }).ConfigureAwait(true);
    }

    private string BuildHeaderCsvLine() =>
        string.Join(",", Columns.Select(c => EscapeCsvField(c.Header)));

    private string BuildRowCsvLine(DataRowViewModel row) => FormatDataRowForCsv(row.Values, Columns.Count);

    private static string FormatDataRowForCsv(IReadOnlyList<string> row, int columnCount)
    {
        var parts = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var s = i < row.Count ? row[i] ?? string.Empty : string.Empty;
            parts[i] = EscapeCsvField(s);
        }

        return string.Join(",", parts);
    }

    private static string FormatDataRowForCsv(IReadOnlyList<object?> row, int columnCount)
    {
        var parts = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var s = i < row.Count ? row[i]?.ToString() ?? string.Empty : string.Empty;
            parts[i] = EscapeCsvField(s);
        }

        return string.Join(",", parts);
    }

    private static string EscapeCsvField(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.IndexOfAny([',', '"', '\n', '\r']) < 0) return s;
        return $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private async Task LoadUserPageAsync()
    {
        if (_provider == null) return;
        IsLoading = true;

        try
        {
            var offset = (CurrentPage - 1) * UserPageSize;
            var result = await _provider.GetPageAsync(offset, UserPageSize);

            if (_provider.SupportsTotalCount)
            {
                TotalRows = await _provider.GetTotalCountAsync();
                var totalPages = UserPageSize > 0 ? (int)Math.Ceiling((double)TotalRows / UserPageSize) : 1;
                var safeTotalPages = totalPages < 1 ? 1 : totalPages;
                if (CurrentPage > safeTotalPages)
                {
                    CurrentPage = safeTotalPages;
                    offset = (CurrentPage - 1) * UserPageSize;
                    result = await _provider.GetPageAsync(offset, UserPageSize);
                }
            }

            Rows.Clear();
            var lineIndex = offset + 1;
            foreach (var row in result.Rows)
            {
                var rowVm = new DataRowViewModel { LineNumber = lineIndex++ };
                for (var i = 0; i < Columns.Count; i++)
                {
                    rowVm.Values.Add(i < row.Count ? row[i]?.ToString() ?? string.Empty : string.Empty);
                }
                Rows.Add(rowVm);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveProviderColumnsAsync(IDataProvider provider)
    {
        if (provider is RelationalDbDataProvider relational)
            return await relational.GetColumnsAsync();

        return await Task.Run(() => provider.Columns);
    }

    private void BuildColumns(IReadOnlyList<string> columnNames)
    {
        Columns.Clear();
        for (var i = 0; i < columnNames.Count; i++)
        {
            Columns.Add(new DataColumnViewModel { Header = columnNames[i], Index = i });
        }

        OnPropertyChanged(nameof(Columns));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Rows.CollectionChanged -= OnRowsOrColumnsCollectionChanged;
            Columns.CollectionChanged -= OnRowsOrColumnsCollectionChanged;
            OpenCsvSaveStreamAsync = null;
            OpenUrlInApp = null;
            _detachProviderTotalHandler?.Invoke();
            _detachProviderTotalHandler = null;
            _provider?.Dispose();
            _provider = null;
            _liveSourceRows.Clear();
            Rows.Clear();
        }
        base.Dispose(disposing);
    }

    public static DataTableViewModel FromCsv(string filePath)
    {
        var result = CsvParser.Parse(filePath);
        var vm = new DataTableViewModel
        {
            SourceFilePath = filePath,
            SourceType = "csv",
            ShowHeaderPanel = false
        };

        for (var i = 0; i < result.Headers.Count; i++)
        {
            vm.Columns.Add(new DataColumnViewModel
            {
                Header = result.Headers[i],
                Index = i
            });
        }

        var lineIndex = 1;
        foreach (var row in result.Rows)
        {
            var rowVm = new DataRowViewModel { LineNumber = lineIndex++ };
            for (var i = 0; i < result.Headers.Count; i++)
            {
                var value = i < row.Length ? row[i] : string.Empty;
                rowVm.Values.Add(value);
            }
            vm.Rows.Add(rowVm);
        }

        vm.TotalRows = result.RowCount;
        vm.RefreshExportCommands();
        return vm;
    }
}

public partial class DataColumnViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private int _index;
}

public partial class DataRowViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _lineNumber;

    [ObservableProperty]
    private ObservableCollection<string> _values = new();

    /// <summary>宿主自定义数据（如流量详情的原始条目）。</summary>
    public object? Tag { get; set; }
}
