using TrafficMimeCore = ZeroFall.Traffic.Metadata.TrafficMimeClassifier;

namespace ZeroFall.Browser.ViewModels;

/// <summary>筛选 UI 与 SQL：分类逻辑委托 <see cref="ZeroFall.Traffic.Metadata.TrafficMimeClassifier"/>。</summary>
public static class TrafficMimeClassifier
{
    public static TrafficMimeCategory Classify(string responseContentType, string url) =>
        TrafficMimeCore.Classify(responseContentType, url);

    public static bool IsCategoryEnabled(TrafficFilterSpec spec, TrafficMimeCategory category) =>
        category switch
        {
            TrafficMimeCategory.Html => spec.MimeHtml,
            TrafficMimeCategory.OtherText => spec.MimeOtherText,
            TrafficMimeCategory.Script => spec.MimeScript,
            TrafficMimeCategory.Images => spec.MimeImages,
            TrafficMimeCategory.Xml => spec.MimeXml,
            TrafficMimeCategory.Flash => spec.MimeFlash,
            TrafficMimeCategory.Css => spec.MimeCss,
            TrafficMimeCategory.OtherBinary => spec.MimeOtherBinary,
            _ => true
        };

    public static bool MatchesMimeFilter(TrafficFilterSpec spec, string responseContentType, string url)
    {
        var category = Classify(responseContentType, url);
        return IsCategoryEnabled(spec, category);
    }

    public static bool MatchesMimeFilter(
        TrafficFilterSpec spec,
        TrafficMimeCategory category,
        string responseContentType,
        string url)
    {
        if (category != TrafficMimeCategory.OtherBinary || !string.IsNullOrEmpty(responseContentType))
            return IsCategoryEnabled(spec, category);

        return MatchesMimeFilter(spec, responseContentType, url);
    }

    public static string GetCategorySqlCondition(TrafficMimeCategory category) =>
        TrafficMimeCore.GetCategorySqlCondition(category);
}
