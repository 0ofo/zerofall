namespace ZeroFall.Browser;

/// <summary>浏览器流量经本地 Fluxzy 代理入库，不使用 CDP 抓包。</summary>
public static class OpenSourceBrowserPolicy
{
    public static bool ProxyOnlyTrafficCapture => true;
}
