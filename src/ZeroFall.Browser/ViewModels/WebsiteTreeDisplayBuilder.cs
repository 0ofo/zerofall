using System;
using System.Collections.Generic;
using ZeroFall.Fingerprint.Core;

namespace ZeroFall.Browser.ViewModels;

/// <summary>网站树展示克隆（可在后台线程运行；输入为内存树引用 + 技术栈快照）。</summary>
internal static class WebsiteTreeDisplayBuilder
{
    internal sealed record TechnologySnapshot(
        string? CmsSummary,
        IReadOnlyList<Framework> Frameworks);

    internal sealed record SiteBuildInput(
        WebsiteTreeNodeViewModel MemorySite,
        IReadOnlySet<string> ExpandedIds,
        TechnologySnapshot Technology);

    public static WebsiteTreeNodeViewModel BuildSiteDisplayClone(
        SiteBuildInput input,
        Func<WebsiteTreeNodeViewModel, TechnologySnapshot> technologyForSite)
    {
        var technology = technologyForSite(input.MemorySite);
        var siteExpanded = input.ExpandedIds.Contains(input.MemorySite.Id);
        var clone = new WebsiteTreeNodeViewModel
        {
            Id = input.MemorySite.Id,
            Title = input.MemorySite.Title,
            NodeType = input.MemorySite.NodeType,
            Host = input.MemorySite.Host,
            ItemIcon = input.MemorySite.ItemIcon,
            IsExpanded = siteExpanded
        };

        if (!string.IsNullOrWhiteSpace(technology.CmsSummary))
            clone.Title = $"{input.MemorySite.Title} — {technology.CmsSummary}";

        // 始终构建子树以便 TreeView 显示展开箭头；折叠态由 IsExpanded 控制，不默认全部展开。
        AppendTechnologyFolder(clone, input.ExpandedIds, technology);

        foreach (var child in WebsiteTreeSiteLayout.BuildSiteDisplayRoot(input.MemorySite, input.ExpandedIds).Children)
        {
            if (child.NodeType != WebsiteTreeNodeType.Site)
                clone.Children.Add(child);
        }

        foreach (var child in input.MemorySite.Children)
        {
            if (child.NodeType == WebsiteTreeNodeType.Site)
            {
                clone.Children.Add(BuildSiteDisplayClone(
                    new SiteBuildInput(child, input.ExpandedIds, technology),
                    technologyForSite));
            }
        }

        return clone;
    }

    private static void AppendTechnologyFolder(
        WebsiteTreeNodeViewModel clone,
        IReadOnlySet<string> expandedIds,
        TechnologySnapshot technology)
    {
        if (technology.Frameworks.Count == 0)
            return;

        var folderId = $"tech:{clone.Id}";
        var folder = new WebsiteTreeNodeViewModel
        {
            Id = folderId,
            Title = "技术栈",
            NodeType = WebsiteTreeNodeType.Path,
            ItemIcon = WebsiteTreeIcons.TechnologyFolder,
            IsExpanded = expandedIds.Contains(folderId)
        };

        foreach (var fw in technology.Frameworks)
        {
            folder.Children.Add(new WebsiteTreeNodeViewModel
            {
                Id = $"{folderId}:{fw.Name}:{fw.Version}",
                Title = fw.DisplayText,
                NodeType = WebsiteTreeNodeType.Technology,
                Host = clone.Host,
                ItemIcon = WebsiteTreeIcons.Technology
            });
        }

        clone.Children.Insert(0, folder);
    }
}
