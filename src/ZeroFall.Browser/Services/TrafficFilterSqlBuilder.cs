using System;
using System.Collections.Generic;
using System.Linq;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Traffic.Capture;

namespace ZeroFall.Browser.Services;

public sealed record TrafficSqlParameter(string Name, object Value);

public sealed record TrafficSqlQuery(string Sql, IReadOnlyList<TrafficSqlParameter> Parameters);

public static class TrafficFilterSqlBuilder
{
    public static TrafficSqlQuery Build(
        TrafficFilterSpec spec,
        string lastBrowserTabId,
        bool onlyLastBrowserTab,
        int limit,
        TrafficArchiveProjection projection = TrafficArchiveProjection.Full)
    {
        var clauses = new List<string>();
        var parameters = new List<TrafficSqlParameter>();
        var index = 0;

        if (spec.ShowOnlyInScope)
        {
            if (spec.ScopeHosts is { Count: > 0 })
                AddScopeHostClause(spec.ScopeHosts, clauses, parameters, ref index);
            else
                clauses.Add("(url LIKE 'http://%' OR url LIKE 'https://%')");
        }

        if (spec.HideWithoutResponse)
            clauses.Add("(status_code IS NOT NULL AND status_code > 0)");

        if (spec.ShowOnlyParameterized)
            clauses.Add("(has_query = 1 OR INSTR(url, '?') > 0)");

        AddMimeFilter(spec, clauses);
        AddStatusFilter(spec, clauses);

        if (!string.IsNullOrWhiteSpace(spec.SearchTerm) && !spec.SearchRegex)
            AddKeywordClause(spec.SearchTerm, spec.SearchNegative, spec.SearchCaseSensitive, clauses, parameters, ref index);

        if (spec.ExtensionShowOnlyEnabled)
            AddInClause("extension", SplitTokens(spec.ExtensionShowOnly).Select(x => x.TrimStart('.').ToLowerInvariant()), clauses, parameters, ref index);

        if (spec.ExtensionHideEnabled)
            AddNotInClause("extension", SplitTokens(spec.ExtensionHide).Select(x => x.TrimStart('.').ToLowerInvariant()), clauses, parameters, ref index);

        if (spec.ShowOnlyWithNotes)
            clauses.Add("TRIM(remark) <> ''");

        if (spec.ShowOnlyHighlighted)
            clauses.Add("TRIM(color) <> ''");

        if (!string.IsNullOrWhiteSpace(spec.ListenerPort)
            && int.TryParse(spec.ListenerPort.Trim(), out var listenerPort))
        {
            clauses.Add("(url LIKE $p" + index + " OR url LIKE $p" + (index + 1) + " OR url LIKE $p" + (index + 2) + ")");
            parameters.Add(new TrafficSqlParameter("$p" + index++, $"%:{listenerPort}/%"));
            parameters.Add(new TrafficSqlParameter("$p" + index++, $"%:{listenerPort}?%"));
            parameters.Add(new TrafficSqlParameter("$p" + index++, $"%:{listenerPort}"));
        }

        if (onlyLastBrowserTab && !string.IsNullOrWhiteSpace(lastBrowserTabId))
        {
            var current = "$p" + index++;
            var proxy = "$p" + index++;
            clauses.Add($"(browser_tab_id = {current} OR browser_tab_id = {proxy})");
            parameters.Add(new TrafficSqlParameter(current, lastBrowserTabId));
            parameters.Add(new TrafficSqlParameter(proxy, ProxyTrafficSource.BrowserTabId));
        }

        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        var boundedLimit = Math.Clamp(limit, 1, 10000);
        var selectList = TrafficArchiveColumnSets.SelectList(projection);
        var sql = $"""
            SELECT {selectList}
            FROM "{TrafficArchiveService.TableName}"
            {where}
            ORDER BY captured_at_utc DESC, rowid DESC
            LIMIT {boundedLimit}
            """;

        return new TrafficSqlQuery(sql, parameters);
    }

    private static void AddMimeFilter(TrafficFilterSpec spec, List<string> clauses)
    {
        var enabled = new List<string>();
        if (spec.MimeHtml) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.Html));
        if (spec.MimeOtherText) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.OtherText));
        if (spec.MimeScript) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.Script));
        if (spec.MimeImages) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.Images));
        if (spec.MimeXml) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.Xml));
        if (spec.MimeFlash) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.Flash));
        if (spec.MimeCss) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.Css));
        if (spec.MimeOtherBinary) enabled.Add(TrafficMimeClassifier.GetCategorySqlCondition(TrafficMimeCategory.OtherBinary));

        if (enabled.Count == 0)
            clauses.Add("1=0");
        else if (enabled.Count < 8)
            clauses.Add("(" + string.Join(" OR ", enabled) + ")");
    }

    private static void AddStatusFilter(TrafficFilterSpec spec, List<string> clauses)
    {
        var parts = new List<string>();
        if (spec.Status2xx) parts.Add("(status_code >= 200 AND status_code < 300)");
        if (spec.Status3xx) parts.Add("(status_code >= 300 AND status_code < 400)");
        if (spec.Status4xx) parts.Add("(status_code >= 400 AND status_code < 500)");
        if (spec.Status5xx) parts.Add("(status_code >= 500 AND status_code < 600)");

        if (parts.Count == 0)
            clauses.Add("(status_code IS NULL OR status_code = 0)");
        else if (parts.Count < 4)
            clauses.Add("((status_code IS NULL OR status_code = 0) OR (" + string.Join(" OR ", parts) + "))");
    }


    private static void AddScopeHostClause(
        IReadOnlyList<string> scopeHosts,
        List<string> clauses,
        List<TrafficSqlParameter> parameters,
        ref int index)
    {
        var parts = new List<string>();
        foreach (var host in scopeHosts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exact = "$p" + index++;
            var sub = "$p" + index++;
            parts.Add($"(host = {exact} OR host LIKE {sub})");
            parameters.Add(new TrafficSqlParameter(exact, host.Trim().ToLowerInvariant()));
            parameters.Add(new TrafficSqlParameter(sub, "%." + host.Trim().ToLowerInvariant()));
        }

        if (parts.Count > 0)
            clauses.Add("(" + string.Join(" OR ", parts) + ")");
    }

    private static void AddInClause(
        string expression,
        IEnumerable<string> values,
        List<string> clauses,
        List<TrafficSqlParameter> parameters,
        ref int index)
    {
        var names = new List<string>();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = "$p" + index++;
            names.Add(name);
            parameters.Add(new TrafficSqlParameter(name, value));
        }

        if (names.Count > 0)
            clauses.Add($"{expression} IN ({string.Join(", ", names)})");
    }

    private static void AddNotInClause(
        string expression,
        IEnumerable<string> values,
        List<string> clauses,
        List<TrafficSqlParameter> parameters,
        ref int index)
    {
        var names = new List<string>();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = "$p" + index++;
            names.Add(name);
            parameters.Add(new TrafficSqlParameter(name, value));
        }

        if (names.Count > 0)
            clauses.Add($"({expression} = '' OR {expression} IS NULL OR {expression} NOT IN ({string.Join(", ", names)}))");
    }

    private static void AddKeywordClause(
        string value,
        bool negate,
        bool caseSensitive,
        List<string> clauses,
        List<TrafficSqlParameter> parameters,
        ref int index)
    {
        var columns = new[] { "url", "request_headers", "request_body", "response_headers", "response_body", "remark" };
        var parts = new List<string>();
        var name = "$p" + index++;
        var pattern = caseSensitive ? value.Trim() : value.Trim().ToLowerInvariant();

        foreach (var column in columns)
        {
            var expr = caseSensitive ? column : $"LOWER({column})";
            parts.Add($"{expr} LIKE {name}");
        }

        var clause = "(" + string.Join(" OR ", parts) + ")";
        clauses.Add(negate ? "NOT " + clause : clause);
        parameters.Add(new TrafficSqlParameter(name, "%" + pattern + "%"));
    }

    private static IEnumerable<string> SplitTokens(string value)
    {
        return value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
