using System;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroFall.Browser.Serialization;
using ZeroFall.HtmlToMarkdown;

namespace ZeroFall.Browser.Services;

/// <summary>从浏览器标签 CDP DOM 提取 raw HTML 或 Markdown（与 <c>page_content</c> 相同逻辑）。</summary>
internal static class CdpPageContentExtractor
{
    public static async Task<string> ExtractAsync(
        Func<string, string, Task<string>> callCdpAsync,
        bool isMd,
        bool withImages)
    {
        _ = await callCdpAsync("DOM.enable", "{}").ConfigureAwait(false);
        if (!isMd)
            return await GetRawHtmlAsync(callCdpAsync).ConfigureAwait(false);

        return await GetMarkdownFromDomTreeAsync(callCdpAsync, withImages).ConfigureAwait(false);
    }

    private static async Task<string> GetRawHtmlAsync(Func<string, string, Task<string>> callCdpAsync)
    {
        var documentJson = await callCdpAsync(
            "DOM.getDocument",
            BrowserJson.Serialize(new DomGetDocumentParams { Depth = 0, Pierce = false })).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(documentJson);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return $"CDP 错误: {err.GetString()}";
            if (!doc.RootElement.TryGetProperty("root", out var root)
                || !root.TryGetProperty("nodeId", out var nodeIdEl))
                return $"DOM.getDocument 返回异常: {documentJson}";

            var outerHtml = await callCdpAsync(
                "DOM.getOuterHTML",
                BrowserJson.Serialize(new DomGetOuterHtmlParams { NodeId = nodeIdEl.GetInt32() })).ConfigureAwait(false);

            using var outer = JsonDocument.Parse(outerHtml);
            if (outer.RootElement.TryGetProperty("error", out var oerr))
                return $"CDP 错误: {oerr.GetString()}";
            if (!outer.RootElement.TryGetProperty("outerHTML", out var htmlEl))
                return outerHtml;

            var html = htmlEl.GetString() ?? string.Empty;
            return html;
        }
        catch (Exception ex)
        {
            return $"解析失败: {ex.Message}";
        }
    }

    private static async Task<string> GetMarkdownFromDomTreeAsync(
        Func<string, string, Task<string>> callCdpAsync,
        bool withImages)
    {
        var fullDocJson = await callCdpAsync(
            "DOM.getDocument",
            BrowserJson.Serialize(new DomGetDocumentParams { Depth = -1, Pierce = true })).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(fullDocJson);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return $"CDP 错误: {err.GetString()}";
            if (!doc.RootElement.TryGetProperty("root", out var root))
                return $"DOM.getDocument 返回异常: {fullDocJson}";

            var options = new HtmlToMarkdownOptions
            {
                MaxOutputCharacters = 0,
                SkipImages = !withImages
            };
            return new DomToMarkdownConverter(options).Convert(root);
        }
        catch (Exception ex)
        {
            return $"解析 DOM.getDocument 失败: {ex.Message}";
        }
    }
}
