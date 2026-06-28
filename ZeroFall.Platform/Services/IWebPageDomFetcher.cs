using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

/// <summary>通过 WebView2 CDP 获取页面完整 DOM（outerHTML）。</summary>
public interface IWebPageDomFetcher
{
    Task<WebPageDomFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>并发抓取多个 URL（各自使用临时浏览器标签，完成后关闭）。</summary>
    Task<IReadOnlyList<WebPageDomFetchResult>> FetchManyAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default);
}
