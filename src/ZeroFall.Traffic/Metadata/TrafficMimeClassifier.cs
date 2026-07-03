using System;
using System.IO;

namespace ZeroFall.Traffic.Metadata;

public static class TrafficMimeClassifier
{
    public static TrafficMimeCategory Classify(string responseContentType, string url, string? extension = null)
    {
        var contentType = (responseContentType ?? string.Empty).Trim().ToLowerInvariant();
        var ext = extension ?? TryGetExtension(url);

        if (contentType.StartsWith("text/html", StringComparison.Ordinal)
            || ext is "html" or "htm")
            return TrafficMimeCategory.Html;

        if (contentType.Contains("javascript", StringComparison.Ordinal)
            || contentType.Contains("ecmascript", StringComparison.Ordinal)
            || ext is "js" or "mjs" or "cjs")
            return TrafficMimeCategory.Script;

        if (contentType.Contains("xml", StringComparison.Ordinal)
            || ext is "xml" or "svg" or "rss" or "atom")
            return TrafficMimeCategory.Xml;

        if (contentType.StartsWith("text/css", StringComparison.Ordinal)
            || ext == "css")
            return TrafficMimeCategory.Css;

        if (contentType.StartsWith("image/", StringComparison.Ordinal)
            || ext is "png" or "jpg" or "jpeg" or "gif" or "webp" or "ico" or "bmp" or "tif" or "tiff")
            return TrafficMimeCategory.Images;

        if (contentType.Contains("flash", StringComparison.Ordinal)
            || ext is "swf" or "flv")
            return TrafficMimeCategory.Flash;

        if (contentType.StartsWith("text/", StringComparison.Ordinal))
            return TrafficMimeCategory.OtherText;

        return TrafficMimeCategory.OtherBinary;
    }

    public static string GetCategorySqlCondition(TrafficMimeCategory category)
    {
        var id = (int)category;
        var legacy = GetLegacyCategorySqlCondition(category);
        return $"(mime_category = {id} OR ((mime_category IS NULL OR mime_category < 0) AND ({legacy})))";
    }

    private static string GetLegacyCategorySqlCondition(TrafficMimeCategory category) =>
        category switch
        {
            TrafficMimeCategory.Html =>
                "(LOWER(response_content_type) LIKE 'text/html%' OR extension IN ('html','htm'))",
            TrafficMimeCategory.Script =>
                "(LOWER(response_content_type) LIKE '%javascript%' OR LOWER(response_content_type) LIKE '%ecmascript%' OR extension IN ('js','mjs','cjs'))",
            TrafficMimeCategory.Xml =>
                "(LOWER(response_content_type) LIKE '%xml%' OR extension IN ('xml','svg','rss','atom'))",
            TrafficMimeCategory.Css =>
                "(LOWER(response_content_type) LIKE '%css%' OR extension = 'css')",
            TrafficMimeCategory.Images =>
                "(LOWER(response_content_type) LIKE 'image/%' OR extension IN ('png','jpg','jpeg','gif','webp','ico','bmp','tif','tiff'))",
            TrafficMimeCategory.Flash =>
                "(LOWER(response_content_type) LIKE '%flash%' OR extension IN ('swf','flv'))",
            TrafficMimeCategory.OtherText =>
                """
                (
                    LOWER(response_content_type) LIKE 'text/%'
                    AND LOWER(response_content_type) NOT LIKE 'text/html%'
                    AND LOWER(response_content_type) NOT LIKE '%css%'
                    AND LOWER(response_content_type) NOT LIKE '%javascript%'
                    AND LOWER(response_content_type) NOT LIKE '%ecmascript%'
                    AND LOWER(response_content_type) NOT LIKE '%xml%'
                    AND extension NOT IN ('html','htm','css','js','mjs','cjs','xml','svg')
                )
                """,
            TrafficMimeCategory.OtherBinary =>
                """
                NOT (
                    LOWER(response_content_type) LIKE 'text/html%'
                    OR LOWER(response_content_type) LIKE '%javascript%'
                    OR LOWER(response_content_type) LIKE '%ecmascript%'
                    OR LOWER(response_content_type) LIKE '%xml%'
                    OR LOWER(response_content_type) LIKE '%css%'
                    OR LOWER(response_content_type) LIKE 'text/%'
                    OR LOWER(response_content_type) LIKE 'image/%'
                    OR LOWER(response_content_type) LIKE '%flash%'
                    OR extension IN ('html','htm','js','mjs','cjs','xml','svg','css','png','jpg','jpeg','gif','webp','ico','bmp','swf','flv')
                )
                """,
            _ => "1=1"
        };

    private static string TryGetExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        return Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
    }
}
