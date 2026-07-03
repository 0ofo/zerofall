using ZeroFall.Base.Mvvm;

namespace ZeroFall.AssetRecon.ViewModels;

/// <summary>左侧侦察面板：查询（<see cref="Recon"/>）+ 历史（<see cref="History"/>）合一。</summary>
public sealed class AssetReconPanelHostViewModel : ViewModelBase
{
    public AssetReconViewModel Recon { get; }
    public AssetReconLeftPanelViewModel History { get; }

    public AssetReconPanelHostViewModel(
        AssetReconViewModel recon,
        AssetReconLeftPanelViewModel history)
    {
        Recon = recon;
        History = history;
        recon.SetLeftPanelViewModel(history);
    }
}
