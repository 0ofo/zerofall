using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ZeroFall.Browser.ViewModels;

/// <summary>网站树展示布局（左侧 UI 与 browser_website_tree JSON 共用，见 doc/website-tree.md）。</summary>
internal static class WebsiteTreeSiteLayout
{
    private static readonly HashSet<string> EmptyExpanded = new(StringComparer.Ordinal);

    public static string GetRequestKey(WebsiteTreeNodeViewModel request) =>
        string.IsNullOrEmpty(request.RequestPath) ? request.Title : request.RequestPath;

    public static string GetExportLeaf(WebsiteTreeNodeViewModel request)
    {
        var (_, leaf) = ParseRequestPathParts(GetRequestKey(request), request.Title);
        return leaf;
    }

    /// <summary>克隆本站 Path/Request 子树并应用展示规则（单链合并 + 同 path 分组）。</summary>
    public static WebsiteTreeNodeViewModel BuildSiteDisplayRoot(
        WebsiteTreeNodeViewModel siteNode,
        IReadOnlySet<string>? expandedIds = null)
    {
        expandedIds ??= EmptyExpanded;
        var clone = new WebsiteTreeNodeViewModel
        {
            Id = siteNode.Id,
            Title = siteNode.Host,
            Host = siteNode.Host,
            NodeType = WebsiteTreeNodeType.Site
        };

        foreach (var child in siteNode.Children)
        {
            if (child.NodeType is WebsiteTreeNodeType.Path or WebsiteTreeNodeType.Request)
                clone.Children.Add(ClonePathOrRequestSubtree(child, expandedIds));
        }

        CollapseDisplayPathChains(clone);
        GroupDuplicatePathRequests(clone);
        SortDisplayChildren(clone);

        foreach (var child in siteNode.Children)
        {
            if (child.NodeType == WebsiteTreeNodeType.Site)
                clone.Children.Add(BuildSiteDisplayRoot(child, expandedIds));
        }

        return clone;
    }

    public static void WriteSiteTreeJson(Utf8JsonWriter writer, WebsiteTreeNodeViewModel siteNode)
    {
        writer.WriteStartObject();
        writer.WriteString("site", siteNode.Host);
        writer.WritePropertyName("child");
        writer.WriteStartArray();
        var display = BuildSiteDisplayRoot(siteNode);
        foreach (var child in display.Children)
            WriteDisplayExportChild(writer, child);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static bool IsDuplicateRequestGroup(WebsiteTreeNodeViewModel node) =>
        node.NodeType == WebsiteTreeNodeType.Path
        && node.Id.StartsWith("grp:", StringComparison.Ordinal);

    public static void CollectLocalRequests(WebsiteTreeNodeViewModel node, List<WebsiteTreeNodeViewModel> result)
    {
        foreach (var child in node.Children)
        {
            switch (child.NodeType)
            {
                case WebsiteTreeNodeType.Request:
                    result.Add(child);
                    break;
                case WebsiteTreeNodeType.Path:
                    CollectLocalRequests(child, result);
                    break;
            }
        }
    }

    /// <summary>
    /// VSCode 紧凑目录：Path 下若仅有唯一子 Path，则将子路径并入当前节点（自下而上）。
    /// 例：img → flexible → logo → pc → logo.png 显示为 img/flexible/logo/pc → logo.png。
    /// </summary>
    public static void CollapseDisplayPathChains(WebsiteTreeNodeViewModel node)
    {
        foreach (var child in node.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Path).ToList())
            CollapseDisplayPathChains(child);

        if (node.NodeType != WebsiteTreeNodeType.Path)
            return;

        while (node.Children.Count == 1 && node.Children[0].NodeType == WebsiteTreeNodeType.Path)
        {
            var child = node.Children[0];
            node.Title = string.IsNullOrEmpty(node.Title) ? child.Title : $"{node.Title}/{child.Title}";
            node.IsExpanded |= child.IsExpanded;

            node.Children.Clear();
            foreach (var grandchild in child.Children)
                node.Children.Add(grandchild);
        }
    }

    /// <summary>同目录下相同 METHOD+path 的多次请求合并为一个分组文件夹（UI 可展开；JSON 仅输出叶子名）。</summary>
    public static void GroupDuplicatePathRequests(WebsiteTreeNodeViewModel node)
    {
        foreach (var child in node.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Path).ToList())
            GroupDuplicatePathRequests(child);

        var requests = node.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Request).ToList();
        if (requests.Count < 2)
            return;

        var byKey = new Dictionary<string, List<WebsiteTreeNodeViewModel>>(StringComparer.Ordinal);
        foreach (var req in requests)
        {
            var key = GetRequestKey(req);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey[key] = list;
            }

            list.Add(req);
        }

        if (byKey.Values.All(v => v.Count < 2))
            return;

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var rebuilt = new List<WebsiteTreeNodeViewModel>();
        foreach (var child in node.Children)
        {
            if (child.NodeType != WebsiteTreeNodeType.Request)
            {
                rebuilt.Add(child);
                continue;
            }

            var key = GetRequestKey(child);
            if (byKey[key].Count < 2)
            {
                rebuilt.Add(child);
                continue;
            }

            if (!emitted.Add(key))
                continue;

            rebuilt.Add(CreateDuplicateRequestGroup(node.Id, byKey[key]));
        }

        node.Children.Clear();
        foreach (var child in rebuilt)
            node.Children.Add(child);
    }

    /// <summary>
    /// 同级排序：请求与合并分组（grp）在前，纯 Path 文件夹在后；组内按 URL 路径长度升序（<c>/</c> 最短）。
    /// </summary>
    public static void SortDisplayChildren(WebsiteTreeNodeViewModel node)
    {
        foreach (var child in node.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Path).ToList())
            SortDisplayChildren(child);

        if (node.Children.Count < 2)
            return;

        var ordered = node.Children
            .OrderBy(GetDisplayKindRank)
            .ThenBy(GetDisplaySortPathLength)
            .ThenBy(GetDisplaySortLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in ordered)
            node.Children.Add(child);
    }

    private static int GetDisplayKindRank(WebsiteTreeNodeViewModel node) =>
        node.NodeType switch
        {
            WebsiteTreeNodeType.Request => 0,
            WebsiteTreeNodeType.Path when IsDuplicateRequestGroup(node) => 0,
            WebsiteTreeNodeType.Path => 1,
            _ => 2
        };

    private static int GetDisplaySortPathLength(WebsiteTreeNodeViewModel node) =>
        node.NodeType switch
        {
            WebsiteTreeNodeType.Request => GetUrlPathLength(GetRequestKey(node)),
            WebsiteTreeNodeType.Path when IsDuplicateRequestGroup(node) =>
                node.Children.Count > 0
                    ? GetUrlPathLength(GetRequestKey(node.Children[0]))
                    : node.Title.Length,
            WebsiteTreeNodeType.Path => GetPathFolderSortLength(node.Title),
            _ => int.MaxValue
        };

    private static string GetDisplaySortLabel(WebsiteTreeNodeViewModel node) =>
        node.NodeType switch
        {
            WebsiteTreeNodeType.Request => GetExportLeaf(node),
            WebsiteTreeNodeType.Path => node.Title,
            WebsiteTreeNodeType.Site => node.Host,
            _ => node.Title
        };

    /// <summary>请求 URL path 长度；根路径 <c>/</c> 为 0。</summary>
    private static int GetUrlPathLength(string requestKey)
    {
        if (string.IsNullOrEmpty(requestKey))
            return 0;

        var space = requestKey.IndexOf(' ');
        var path = space >= 0 ? requestKey[(space + 1)..] : requestKey;
        if (string.IsNullOrEmpty(path) || path == "/")
            return 0;

        return path.Length;
    }

    private static int GetPathFolderSortLength(string title)
    {
        if (string.IsNullOrEmpty(title))
            return 0;

        return title.Length;
    }

    /// <summary>从 RequestPath（"METHOD /path"）拆出目录与叶子；根路径 "/" 的叶子为 "/"。</summary>
    public static (string Directory, string Leaf) ParseRequestPathParts(string requestPath, string fallbackTitle)
    {
        if (string.IsNullOrEmpty(requestPath))
            return (string.Empty, fallbackTitle);

        var space = requestPath.IndexOf(' ');
        var path = space >= 0 ? requestPath[(space + 1)..] : requestPath;
        if (string.IsNullOrEmpty(path) || path == "/")
            return (string.Empty, "/");

        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash <= 0)
            return (string.Empty, path.TrimStart('/'));

        var directory = path[1..lastSlash];
        var leaf = path[(lastSlash + 1)..];
        return string.IsNullOrEmpty(leaf) ? (directory, "/") : (directory, leaf);
    }

    private static void WriteDisplayExportChild(Utf8JsonWriter writer, WebsiteTreeNodeViewModel node)
    {
        switch (node.NodeType)
        {
            case WebsiteTreeNodeType.Site:
                WriteSiteTreeJson(writer, node);
                break;
            case WebsiteTreeNodeType.Request:
                writer.WriteStringValue(GetExportLeaf(node));
                break;
            case WebsiteTreeNodeType.Path when IsDuplicateRequestGroup(node):
                writer.WriteStringValue(node.Title);
                break;
            case WebsiteTreeNodeType.Path:
                writer.WriteStartObject();
                writer.WriteString("path", node.Title);
                writer.WritePropertyName("child");
                writer.WriteStartArray();
                foreach (var child in node.Children)
                    WriteDisplayExportChild(writer, child);
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
        }
    }

    private static WebsiteTreeNodeViewModel ClonePathOrRequestSubtree(
        WebsiteTreeNodeViewModel source,
        IReadOnlySet<string> expandedIds)
    {
        if (source.NodeType == WebsiteTreeNodeType.Request)
            return CloneRequestNode(source, expandedIds);

        var clone = new WebsiteTreeNodeViewModel
        {
            Id = source.Id,
            Title = source.Title,
            NodeType = source.NodeType,
            ItemIcon = source.ItemIcon,
            IsExpanded = source.IsExpanded || expandedIds.Contains(source.Id)
        };

        foreach (var child in source.Children)
        {
            if (child.NodeType is WebsiteTreeNodeType.Path or WebsiteTreeNodeType.Request)
                clone.Children.Add(ClonePathOrRequestSubtree(child, expandedIds));
        }

        return clone;
    }

    private static WebsiteTreeNodeViewModel CloneRequestNode(
        WebsiteTreeNodeViewModel source,
        IReadOnlySet<string> expandedIds) =>
        new()
        {
            Id = source.Id,
            Title = source.Title,
            NodeType = source.NodeType,
            EntryId = source.EntryId,
            Host = source.Host,
            RequestPath = source.RequestPath,
            ItemIcon = source.ItemIcon,
            IsExpanded = source.IsExpanded || expandedIds.Contains(source.Id)
        };

    private static WebsiteTreeNodeViewModel CreateDuplicateRequestGroup(
        string parentId,
        IReadOnlyList<WebsiteTreeNodeViewModel> requests)
    {
        var leaf = GetExportLeaf(requests[0]);
        var folder = new WebsiteTreeNodeViewModel
        {
            Id = $"grp:{parentId}|{GetRequestKey(requests[0])}",
            Title = leaf,
            NodeType = WebsiteTreeNodeType.Path,
            ItemIcon = WebsiteTreeIcons.Folder
        };

        foreach (var req in requests)
            folder.Children.Add(req);

        return folder;
    }
}
