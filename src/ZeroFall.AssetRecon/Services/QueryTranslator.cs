using System;
using System.Collections.Generic;
using System.Net;

namespace ZeroFall.AssetRecon.Services;

public enum QueryFieldType
{
    Text,
    Domain,
    Ip,
    Port,
    Title,
    Service,
    Org,
    Country,
    City,
    Region,
    Os,
    Server,
    Icp,
    Cert,
    Body,
    Asn
}

public class QueryField
{
    public QueryFieldType Type { get; init; }
    public string Value { get; init; } = string.Empty;
    public bool IsNegated { get; init; }
}

/// <summary>智慧搜索：支持 <c>field=value</c>、<c>field="..."</c>，以及 <c>&&</c>、<c>||</c> 与括号组合。</summary>
public abstract class QueryExpr;

public sealed class QueryAtomExpr : QueryExpr
{
    public required QueryField Field { get; init; }
}

public sealed class QueryBinaryExpr : QueryExpr
{
    public bool IsAnd { get; init; }
    public required QueryExpr Left { get; init; }
    public required QueryExpr Right { get; init; }
}

public class UnifiedQuery
{
    /// <summary>布尔表达式（智慧语法）；非空时优先于 <see cref="Fields"/>。</summary>
    public QueryExpr? Expression { get; init; }

    /// <summary>旧版空格分隔 <c>key:value</c> 列表；无表达式时使用。</summary>
    public List<QueryField> Fields { get; init; } = new();

    public string RawText { get; init; } = string.Empty;
}

public static class QueryParser
{
    private static readonly Dictionary<string, QueryFieldType> FieldMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["domain"] = QueryFieldType.Domain,
        ["host"] = QueryFieldType.Domain,
        ["ip"] = QueryFieldType.Ip,
        ["port"] = QueryFieldType.Port,
        ["title"] = QueryFieldType.Title,
        ["service"] = QueryFieldType.Service,
        ["protocol"] = QueryFieldType.Service,
        ["org"] = QueryFieldType.Org,
        ["country"] = QueryFieldType.Country,
        ["city"] = QueryFieldType.City,
        ["region"] = QueryFieldType.Region,
        ["province"] = QueryFieldType.Region,
        ["os"] = QueryFieldType.Os,
        ["server"] = QueryFieldType.Server,
        ["icp"] = QueryFieldType.Icp,
        ["cert"] = QueryFieldType.Cert,
        ["ssl"] = QueryFieldType.Cert,
        ["body"] = QueryFieldType.Body,
        ["banner"] = QueryFieldType.Body,
        ["asn"] = QueryFieldType.Asn
    };

    public static UnifiedQuery Parse(string input)
    {
        var t = input.Trim();
        if (string.IsNullOrEmpty(t))
            return new UnifiedQuery { RawText = input };

        if (TryNormalizeSingleIpOrDomainToken(t) is { } rewritten)
            t = rewritten;

        if (ExpressionQueryParser.LooksLikeExpression(t))
        {
            try
            {
                var expr = ExpressionQueryParser.ParseExpression(t);
                return new UnifiedQuery { Expression = expr, RawText = input };
            }
            catch (FormatException)
            {
                // 回退到旧解析，避免误触表达式模式时完全不可用
            }
        }

        return ParseLegacy(t, input);
    }

    private static UnifiedQuery ParseLegacy(string text, string rawText)
    {
        var fields = new List<QueryField>();
        var textParts = new List<string>();
        var i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            var isNegated = false;
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] != ' ')
            {
                isNegated = true;
                i++;
            }

            var keyStart = i;
            while (i < text.Length && text[i] != ':' && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            var key = text[keyStart..i];

            if (i < text.Length && (text[i] == ':' || text[i] == '=') && FieldMap.TryGetValue(key, out var fieldType))
            {
                i++;
                var value = ReadValue(text, ref i);
                fields.Add(new QueryField { Type = fieldType, Value = value, IsNegated = isNegated });
            }
            else
            {
                if (isNegated) i--;
                var word = ReadWord(text, ref i);
                if (!string.IsNullOrEmpty(word))
                    textParts.Add(word);
            }
        }

        if (textParts.Count > 0)
        {
            fields.Insert(0, new QueryField
            {
                Type = QueryFieldType.Text,
                Value = string.Join(" ", textParts)
            });
        }

        return new UnifiedQuery { Fields = fields, RawText = rawText };
    }

    /// <summary>
    /// 聚合搜索：整段仅为单个 IPv4/IPv6 时等价 <c>ip="…"</c>；仅为可解析的单域名主机名时等价 <c>domain="…"</c>。
    /// 已含运算符、括号、字段赋值或引号时不改写。
    /// </summary>
    private static string? TryNormalizeSingleIpOrDomainToken(string t)
    {
        if (t.Contains('(') || t.Contains(')') || t.Contains("&&", StringComparison.Ordinal) || t.Contains("||", StringComparison.Ordinal))
            return null;
        if (t.Contains('=') || t.Contains('"'))
            return null;
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c))
                return null;
        }

        if (IPAddress.TryParse(t, out _))
            return $"ip=\"{QueryTranslator.EscapeAtomQuoted(t)}\"";

        if (IsLikelyDnsHostname(t))
            return $"domain=\"{QueryTranslator.EscapeAtomQuoted(t)}\"";

        return null;
    }

    private static bool IsLikelyDnsHostname(string t)
    {
        if (t.Length is < 1 or > 253) return false;
        if (string.Equals(t, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!t.Contains('.')) return false;

        foreach (var label in t.Split('.'))
        {
            if (label.Length is < 1 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return false;
            }
        }

        return true;
    }

    private static string ReadValue(string input, ref int i)
    {
        while (i < input.Length && char.IsWhiteSpace(input[i])) i++;

        if (i < input.Length && input[i] == '"')
        {
            i++;
            var start = i;
            while (i < input.Length && input[i] != '"') i++;
            var value = input[start..i];
            if (i < input.Length) i++;
            return value;
        }

        return ReadWord(input, ref i);
    }

    private static string ReadWord(string input, ref int i)
    {
        var start = i;
        while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != ':') i++;

        if (i > start)
            return input[start..i];

        while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
        return i > start ? input[start..i] : string.Empty;
    }

    internal static bool TryResolveFieldType(string key, out QueryFieldType fieldType) =>
        FieldMap.TryGetValue(key, out fieldType);
}

/// <summary>解析 <c>(a=b || c=d) && (e=f)</c> 形式；<c>=</c> 右侧可为引号字符串或无引号（无空格）。</summary>
internal sealed class ExpressionQueryParser
{
    internal static bool LooksLikeExpression(string t) =>
        t.Contains('(') || t.Contains("&&", StringComparison.Ordinal) || t.Contains("||", StringComparison.Ordinal);

    private readonly string _s;
    private int _i;

    private ExpressionQueryParser(string s) => _s = s;

    internal static QueryExpr ParseExpression(string input)
    {
        var p = new ExpressionQueryParser(input);
        var e = p.ParseOr();
        p.SkipWs();
        if (p._i < p._s.Length)
            throw new FormatException($"智慧搜索语法：位置 {p._i} 处存在未解析字符。");
        return e;
    }

    private QueryExpr ParseOr()
    {
        var left = ParseAnd();
        while (true)
        {
            SkipWs();
            if (!Match("||")) break;
            var right = ParseAnd();
            left = new QueryBinaryExpr { IsAnd = false, Left = left, Right = right };
        }

        return left;
    }

    private QueryExpr ParseAnd()
    {
        var left = ParsePrimary();
        while (true)
        {
            SkipWs();
            if (!Match("&&")) break;
            var right = ParsePrimary();
            left = new QueryBinaryExpr { IsAnd = true, Left = left, Right = right };
        }

        return left;
    }

    private QueryExpr ParsePrimary()
    {
        SkipWs();
        if (Match("("))
        {
            var inner = ParseOr();
            SkipWs();
            if (!Match(")"))
                throw new FormatException("智慧搜索语法：缺少右括号 ')'。");
            return inner;
        }

        return ParseAtom();
    }

    private QueryExpr ParseAtom()
    {
        var key = ReadIdent();
        if (string.IsNullOrEmpty(key))
            throw new FormatException("智慧搜索语法：需要字段名。");

        SkipWs();
        if (!Match("="))
            throw new FormatException($"智慧搜索语法：字段 '{key}' 后应为 '='。");

        SkipWs();
        var value = ReadAtomValue();
        if (!QueryParser.TryResolveFieldType(key, out var fieldType))
            throw new FormatException($"智慧搜索语法：未知字段 '{key}'。");

        return new QueryAtomExpr
        {
            Field = new QueryField { Type = fieldType, Value = value, IsNegated = false }
        };
    }

    private string ReadIdent()
    {
        SkipWs();
        var start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
            _i++;
        return start < _i ? _s[start.._i] : string.Empty;
    }

    private string ReadAtomValue()
    {
        if (_i >= _s.Length)
            return string.Empty;

        if (_s[_i] == '"')
        {
            _i++;
            var start = _i;
            while (_i < _s.Length && _s[_i] != '"') _i++;
            var v = _s[start.._i];
            if (_i < _s.Length && _s[_i] == '"') _i++;
            return v;
        }

        var uStart = _i;
        while (_i < _s.Length)
        {
            if (MatchAhead("&&") || MatchAhead("||")) break;
            if (_s[_i] == ')') break;
            if (char.IsWhiteSpace(_s[_i])) break;
            _i++;
        }

        return _s[uStart.._i];
    }

    private bool MatchAhead(string two)
    {
        return _i + 1 < _s.Length && _s[_i] == two[0] && _s[_i + 1] == two[1];
    }

    private void SkipWs()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
    }

    private bool Match(string lit)
    {
        SkipWs();
        if (_i + lit.Length > _s.Length) return false;
        for (var k = 0; k < lit.Length; k++)
        {
            if (_s[_i + k] != lit[k]) return false;
        }

        _i += lit.Length;
        return true;
    }
}

public static class QueryTranslator
{
    private static string Q(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>供聚合语法预重写（<c>ip="…"</c>）时与 <see cref="MapFieldToFofa"/> 等路径一致地转义。</summary>
    internal static string EscapeAtomQuoted(string v) => Q(v);

    public static string ToFofa(this UnifiedQuery query)
    {
        if (query.Expression != null)
            return MapExprToFofa(query.Expression);

        var parts = new List<string>();
        foreach (var f in query.Fields)
        {
            var part = MapFieldToFofa(f);
            if (part != null)
                parts.Add(f.IsNegated ? $"!({part})" : part);
        }

        return string.Join(" && ", parts);
    }

    public static string ToHunter(this UnifiedQuery query)
    {
        if (query.Expression != null)
            return MapExprToHunter(query.Expression);

        var parts = new List<string>();
        foreach (var f in query.Fields)
        {
            var part = MapFieldToHunter(f);
            if (part != null)
                parts.Add(f.IsNegated ? $"!({part})" : part);
        }

        return string.Join(" && ", parts);
    }

    public static string ToQuake(this UnifiedQuery query)
    {
        if (query.Expression != null)
            return MapExprToQuake(query.Expression);

        var parts = new List<string>();
        foreach (var f in query.Fields)
        {
            var part = MapFieldToQuake(f);
            if (part != null)
                parts.Add(f.IsNegated ? $"NOT ({part})" : part);
        }

        return string.Join(" AND ", parts);
    }

    public static string ToShodan(this UnifiedQuery query)
    {
        if (query.Expression != null)
            return MapExprToShodan(query.Expression);

        var parts = new List<string>();
        foreach (var f in query.Fields)
        {
            var v = f.Value;
            var part = f.Type switch
            {
                QueryFieldType.Text => v,
                QueryFieldType.Domain => $"hostname:{v}",
                QueryFieldType.Ip => $"net:{v}",
                QueryFieldType.Port => $"port:{v}",
                QueryFieldType.Title => $"http.title:\"{Q(v)}\"",
                QueryFieldType.Service => $"product:{v}",
                QueryFieldType.Org => $"org:\"{Q(v)}\"",
                QueryFieldType.Country => $"country_code:{v}",
                QueryFieldType.City => $"city:\"{Q(v)}\"",
                QueryFieldType.Region => $"region:\"{Q(v)}\"",
                QueryFieldType.Os => $"os:\"{Q(v)}\"",
                QueryFieldType.Server => $"http.server:\"{Q(v)}\"",
                QueryFieldType.Icp => null,
                QueryFieldType.Cert => $"ssl.cert.subject.cn:\"{Q(v)}\"",
                QueryFieldType.Body => $"http.html:\"{Q(v)}\"",
                QueryFieldType.Asn => $"asn:{v}",
                _ => null
            };
            if (part != null)
                parts.Add(f.IsNegated ? $"NOT {part}" : part);
        }

        return string.Join(" ", parts);
    }

    private static string MapExprToFofa(QueryExpr e) => e switch
    {
        QueryAtomExpr a => MapFieldToFofa(a.Field) ?? string.Empty,
        QueryBinaryExpr b => b.IsAnd
            ? $"({MapExprToFofa(b.Left)} && {MapExprToFofa(b.Right)})"
            : $"({MapExprToFofa(b.Left)} || {MapExprToFofa(b.Right)})",
        _ => string.Empty
    };

    private static string MapExprToHunter(QueryExpr e) => e switch
    {
        QueryAtomExpr a => MapFieldToHunter(a.Field) ?? string.Empty,
        QueryBinaryExpr b => b.IsAnd
            ? $"({MapExprToHunter(b.Left)} && {MapExprToHunter(b.Right)})"
            : $"({MapExprToHunter(b.Left)} || {MapExprToHunter(b.Right)})",
        _ => string.Empty
    };

    private static string MapExprToQuake(QueryExpr e) => e switch
    {
        QueryAtomExpr a => MapFieldToQuake(a.Field) ?? string.Empty,
        QueryBinaryExpr b => b.IsAnd
            ? $"({MapExprToQuake(b.Left)} AND {MapExprToQuake(b.Right)})"
            : $"({MapExprToQuake(b.Left)} OR {MapExprToQuake(b.Right)})",
        _ => string.Empty
    };

    private static string MapExprToShodan(QueryExpr e) => e switch
    {
        QueryAtomExpr a => MapAtomToShodan(a.Field),
        QueryBinaryExpr b => b.IsAnd
            ? $"({MapExprToShodan(b.Left)} {MapExprToShodan(b.Right)})"
            : $"({MapExprToShodan(b.Left)} OR {MapExprToShodan(b.Right)})",
        _ => string.Empty
    };

    private static string MapAtomToShodan(QueryField f)
    {
        var v = f.Value;
        var part = f.Type switch
        {
            QueryFieldType.Text => v,
            QueryFieldType.Domain => $"hostname:{v}",
            QueryFieldType.Ip => $"net:{v}",
            QueryFieldType.Port => $"port:{v}",
            QueryFieldType.Title => $"http.title:\"{Q(v)}\"",
            QueryFieldType.Service => $"product:{v}",
            QueryFieldType.Org => $"org:\"{Q(v)}\"",
            QueryFieldType.Country => $"country_code:{v}",
            QueryFieldType.City => $"city:\"{Q(v)}\"",
            QueryFieldType.Region => $"region:\"{Q(v)}\"",
            QueryFieldType.Os => $"os:\"{Q(v)}\"",
            QueryFieldType.Server => $"http.server:\"{Q(v)}\"",
            QueryFieldType.Icp => null,
            QueryFieldType.Cert => $"ssl.cert.subject.cn:\"{Q(v)}\"",
            QueryFieldType.Body => $"http.html:\"{Q(v)}\"",
            QueryFieldType.Asn => $"asn:{v}",
            _ => null
        };
        if (part == null) return string.Empty;
        return f.IsNegated ? $"NOT {part}" : part;
    }

    private static string? MapFieldToFofa(QueryField f)
    {
        var v = f.Value;
        return f.Type switch
        {
            QueryFieldType.Text => v,
            QueryFieldType.Domain => $"domain=\"{Q(v)}\"",
            QueryFieldType.Ip => $"ip=\"{Q(v)}\"",
            QueryFieldType.Port => $"port=\"{Q(v)}\"",
            QueryFieldType.Title => $"title=\"{Q(v)}\"",
            QueryFieldType.Service => $"protocol=\"{Q(v)}\"",
            QueryFieldType.Org => $"org=\"{Q(v)}\"",
            QueryFieldType.Country => $"country=\"{Q(v)}\"",
            QueryFieldType.City => $"city=\"{Q(v)}\"",
            QueryFieldType.Region => $"region=\"{Q(v)}\"",
            QueryFieldType.Os => $"os=\"{Q(v)}\"",
            QueryFieldType.Server => $"server=\"{Q(v)}\"",
            QueryFieldType.Icp => $"icp=\"{Q(v)}\"",
            QueryFieldType.Cert => $"cert=\"{Q(v)}\"",
            QueryFieldType.Body => $"body=\"{Q(v)}\"",
            QueryFieldType.Asn => $"asn=\"{Q(v)}\"",
            _ => null
        };
    }

    private static string? MapFieldToHunter(QueryField f)
    {
        var v = f.Value;
        return f.Type switch
        {
            QueryFieldType.Text => v,
            QueryFieldType.Domain => $"domain.suffix=\"{Q(v)}\"",
            QueryFieldType.Ip => $"ip=\"{Q(v)}\"",
            QueryFieldType.Port => $"port=\"{Q(v)}\"",
            QueryFieldType.Title => $"web.title=\"{Q(v)}\"",
            QueryFieldType.Service => $"protocol=\"{Q(v)}\"",
            QueryFieldType.Org => $"org=\"{Q(v)}\"",
            QueryFieldType.Country => $"country=\"{Q(v)}\"",
            QueryFieldType.City => $"city=\"{Q(v)}\"",
            QueryFieldType.Region => $"province=\"{Q(v)}\"",
            QueryFieldType.Os => $"os=\"{Q(v)}\"",
            QueryFieldType.Server => $"header.server=\"{Q(v)}\"",
            QueryFieldType.Icp => $"icp=\"{Q(v)}\"",
            QueryFieldType.Cert => $"cert.subject=\"{Q(v)}\"",
            QueryFieldType.Body => $"banner=\"{Q(v)}\"",
            QueryFieldType.Asn => $"as_number=\"{Q(v)}\"",
            _ => null
        };
    }

    private static string? MapFieldToQuake(QueryField f)
    {
        var v = f.Value;
        return f.Type switch
        {
            QueryFieldType.Text => v,
            QueryFieldType.Domain => $"domain:\"{Q(v)}\"",
            QueryFieldType.Ip => $"ip:\"{Q(v)}\"",
            QueryFieldType.Port => $"port:{v}",
            QueryFieldType.Title => $"title:\"{Q(v)}\"",
            QueryFieldType.Service => $"service:\"{Q(v)}\"",
            QueryFieldType.Org => $"org:\"{Q(v)}\"",
            QueryFieldType.Country => $"country_code:\"{Q(v)}\"",
            QueryFieldType.City => $"city:\"{Q(v)}\"",
            QueryFieldType.Region => $"province:\"{Q(v)}\"",
            QueryFieldType.Os => $"os:\"{Q(v)}\"",
            QueryFieldType.Server => $"server:\"{Q(v)}\"",
            QueryFieldType.Icp => null,
            QueryFieldType.Cert => $"cert:\"{Q(v)}\"",
            QueryFieldType.Body => $"body:\"{Q(v)}\"",
            QueryFieldType.Asn => $"asn:{v}",
            _ => null
        };
    }
}
