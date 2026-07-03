using System;
using System.Collections.Generic;

namespace ZeroFall.Browser.ViewModels;

public sealed record TrafficFilterSpec
{
    public bool ShowOnlyInScope { get; init; }
    public bool HideWithoutResponse { get; init; }
    public bool ShowOnlyParameterized { get; init; }

    public bool MimeHtml { get; init; } = true;
    public bool MimeOtherText { get; init; } = true;
    public bool MimeScript { get; init; } = true;
    public bool MimeImages { get; init; }
    public bool MimeXml { get; init; } = true;
    public bool MimeFlash { get; init; } = true;
    public bool MimeCss { get; init; }
    public bool MimeOtherBinary { get; init; } = true;

    public bool Status2xx { get; init; } = true;
    public bool Status3xx { get; init; } = true;
    public bool Status4xx { get; init; } = true;
    public bool Status5xx { get; init; } = true;

    public string SearchTerm { get; init; } = string.Empty;
    public bool SearchRegex { get; init; }
    public bool SearchCaseSensitive { get; init; }
    public bool SearchNegative { get; init; }

    public bool ExtensionShowOnlyEnabled { get; init; }
    public string ExtensionShowOnly { get; init; } = "asp,aspx,jsp,php";
    public bool ExtensionHideEnabled { get; init; } = true;
    public string ExtensionHide { get; init; } = "png,ico,css,woff,woff2,ttf,svg";

    public bool ShowOnlyWithNotes { get; init; }
    public bool ShowOnlyHighlighted { get; init; }

    public string ListenerPort { get; init; } = string.Empty;

    /// <summary>由 <see cref="Services.ITargetScopeService"/> 注入；非空时「仅范围内」按主机匹配。</summary>
    public IReadOnlyList<string>? ScopeHosts { get; init; }

    public static TrafficFilterSpec Default { get; } = new();

    /// <summary>网站树从归档恢复时使用，不做 MIME/扩展名等默认过滤。</summary>
    public static TrafficFilterSpec SiteMapRestore { get; } = new()
    {
        MimeHtml = true,
        MimeOtherText = true,
        MimeScript = true,
        MimeImages = true,
        MimeXml = true,
        MimeFlash = true,
        MimeCss = true,
        MimeOtherBinary = true,
        Status2xx = true,
        Status3xx = true,
        Status4xx = true,
        Status5xx = true,
        ExtensionHideEnabled = false
    };

    public bool IsEquivalentToDefault()
    {
        var d = Default;
        return ShowOnlyInScope == d.ShowOnlyInScope
            && HideWithoutResponse == d.HideWithoutResponse
            && ShowOnlyParameterized == d.ShowOnlyParameterized
            && MimeHtml == d.MimeHtml
            && MimeOtherText == d.MimeOtherText
            && MimeScript == d.MimeScript
            && MimeImages == d.MimeImages
            && MimeXml == d.MimeXml
            && MimeFlash == d.MimeFlash
            && MimeCss == d.MimeCss
            && MimeOtherBinary == d.MimeOtherBinary
            && Status2xx == d.Status2xx
            && Status3xx == d.Status3xx
            && Status4xx == d.Status4xx
            && Status5xx == d.Status5xx
            && string.IsNullOrWhiteSpace(SearchTerm)
            && !SearchRegex
            && !SearchCaseSensitive
            && !SearchNegative
            && ExtensionShowOnlyEnabled == d.ExtensionShowOnlyEnabled
            && string.Equals(ExtensionShowOnly, d.ExtensionShowOnly, StringComparison.Ordinal)
            && ExtensionHideEnabled == d.ExtensionHideEnabled
            && string.Equals(ExtensionHide, d.ExtensionHide, StringComparison.Ordinal)
            && !ShowOnlyWithNotes
            && !ShowOnlyHighlighted
            && string.IsNullOrWhiteSpace(ListenerPort);
    }
}
