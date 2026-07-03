using System;
using Avalonia.Threading;
using ZeroFall.Base.Events;
using ZeroFall.DataTable.Services;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;

namespace ZeroFall.AssetRecon.Services;

/// <summary>资产侦察结果的 Dock 展示协调器，避免 App 层承载模块业务。</summary>
public sealed class AssetReconResultDockPresenter : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly Action<AssetReconResultEvent> _resultHandler;

    public AssetReconResultDockPresenter(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _resultHandler = OnAssetReconResult;
        _eventBus.Subscribe(_resultHandler);
    }

    private void OnAssetReconResult(AssetReconResultEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var tabId = $"asset-recon:{e.SourceName}:{e.Query}:{Guid.NewGuid():N}";
            var dtvm = DataTableBuilder.BuildFromAssetRecon(e.SourceName, e.Query, e.Rows, e.TotalCount);
            var newTab = new DockTabItemViewModel
            {
                Id = tabId,
                Title = dtvm.Title,
                Icon = IconHelper.GetIcon("SemiIconGridView"),
                Content = new DataTableView { DataContext = dtvm },
                IsClosable = true
            };

            _eventBus.Publish(new AddDockTabEvent(DockPosition.Bottom, newTab));
            _eventBus.Publish(new StatusMessageEvent($"{e.SourceName}结果: {e.Rows.Count} 条"));
        });
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_resultHandler);
    }
}
