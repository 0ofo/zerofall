namespace ZeroFall.AssetRecon.Views;

/// <summary>侦察条目详情 Flyout，与 App.axaml 中 FlyoutPresenter 选择器一致。</summary>
internal static class AssetReconDetailFlyoutChrome
{
    internal const string PresenterClass = "AssetReconDetailFlyoutPresenter";

    /// <summary>Host 向内收缩，消解 Flyout 外壳的假滚动。</summary>
    internal const double HostLayoutInset = 6;

    internal const double MinWidth = 200;
    internal const double MinHeight = 48;
    internal const double MaxWidth = 400;
    internal const double MaxHeight = 480;
}
