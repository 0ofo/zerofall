using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ZeroFall.Browser.Services;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.Browser.ViewModels;

public sealed class TrafficFilterPipeline : IFilterPipeline<TrafficLogEntryViewModel>
{
    private readonly IRowFilter<TrafficLogEntryViewModel> _persistedFilter;
    private readonly IRowFilter<TrafficLogEntryViewModel> _liveFilter;

    public TrafficFilterPipeline(TrafficFilterSpec persistedSpec, TrafficFilterSpec liveSpec)
    {
        _persistedFilter = new TrafficRowFilter(persistedSpec);
        _liveFilter = new TrafficRowFilter(liveSpec);
    }

    public IEnumerable<TrafficLogEntryViewModel> Apply(IEnumerable<TrafficLogEntryViewModel> source)
    {
        return source.Where(x => _persistedFilter.Match(x) && _liveFilter.Match(x));
    }

    public bool MatchLiveGate(TrafficLogEntryViewModel row) => _liveFilter.Match(row);

    private sealed class TrafficRowFilter : IRowFilter<TrafficLogEntryViewModel>
    {
        private readonly TrafficFilterSpec _spec;
        private Regex? _searchRegex;

        public TrafficRowFilter(TrafficFilterSpec spec)
        {
            _spec = spec;
            if (_spec.SearchRegex && !string.IsNullOrWhiteSpace(_spec.SearchTerm))
            {
                var options = _spec.SearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                _searchRegex = new Regex(_spec.SearchTerm, options | RegexOptions.Compiled);
            }
        }

        public bool Match(TrafficLogEntryViewModel row)
        {
            if (_spec.ShowOnlyInScope && !IsInScope(row, _spec))
                return false;

            if (_spec.HideWithoutResponse && !HasResponse(row))
                return false;

            if (_spec.ShowOnlyParameterized && !IsParameterized(row.Url))
                return false;

        if (row.HasDerivedMetadata)
            return TrafficMimeClassifier.IsCategoryEnabled(_spec, row.Mime.FilterCategory);

        if (!TrafficMimeClassifier.MatchesMimeFilter(_spec, row.ResponseContentType, row.Url))
            return false;

            if (!MatchesStatusClass(row))
                return false;

            if (!MatchesSearch(row))
                return false;

            if (!MatchesExtensionFilter(row))
                return false;

            if (_spec.ShowOnlyWithNotes && !row.HasRemark)
                return false;

            if (_spec.ShowOnlyHighlighted && !row.HasHighlight)
                return false;

            if (!MatchesListenerPort(row))
                return false;

            return true;
        }

        private bool MatchesStatusClass(TrafficLogEntryViewModel row)
        {
            var code = ParseStatusCode(row.Status);
            if (code is null)
                return !_spec.HideWithoutResponse;

            var statusClass = code.Value / 100;
            return statusClass switch
            {
                2 => _spec.Status2xx,
                3 => _spec.Status3xx,
                4 => _spec.Status4xx,
                5 => _spec.Status5xx,
                _ => true
            };
        }

        private bool MatchesSearch(TrafficLogEntryViewModel row)
        {
            if (string.IsNullOrWhiteSpace(_spec.SearchTerm))
                return true;

            var matched = _searchRegex is not null
                ? MatchesRegex(row)
                : ContainsPlainSearch(row, _spec.SearchTerm);

            return _spec.SearchNegative ? !matched : matched;
        }

        private bool MatchesRegex(TrafficLogEntryViewModel row)
        {
            if (_searchRegex is null)
                return false;

            return _searchRegex.IsMatch(row.Url)
                || _searchRegex.IsMatch(row.RequestHeaders)
                || _searchRegex.IsMatch(row.RequestBody)
                || _searchRegex.IsMatch(row.ResponseHeaders)
                || _searchRegex.IsMatch(row.ResponseBody)
                || _searchRegex.IsMatch(row.Remark);
        }

        private bool ContainsPlainSearch(TrafficLogEntryViewModel row, string keyword)
        {
            var comparison = _spec.SearchCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            return row.Url.Contains(keyword, comparison)
                || row.RequestHeaders.Contains(keyword, comparison)
                || row.RequestBody.Contains(keyword, comparison)
                || row.ResponseHeaders.Contains(keyword, comparison)
                || row.ResponseBody.Contains(keyword, comparison)
                || row.Remark.Contains(keyword, comparison);
        }

        private bool MatchesExtensionFilter(TrafficLogEntryViewModel row)
        {
            if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var uri))
                return !_spec.ExtensionShowOnlyEnabled;

            var ext = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.');
            if (string.IsNullOrEmpty(ext))
            {
                if (_spec.ExtensionShowOnlyEnabled)
                    return false;
            }
            else
            {
                if (_spec.ExtensionShowOnlyEnabled
                    && !TokenListContains(_spec.ExtensionShowOnly, ext))
                    return false;

                if (_spec.ExtensionHideEnabled
                    && TokenListContains(_spec.ExtensionHide, ext))
                    return false;
            }

            return true;
        }

        private bool MatchesListenerPort(TrafficLogEntryViewModel row)
        {
            if (string.IsNullOrWhiteSpace(_spec.ListenerPort))
                return true;

            if (!int.TryParse(_spec.ListenerPort.Trim(), out var expectedPort))
                return true;

            if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var uri))
                return false;

            var actualPort = uri.IsDefaultPort
                ? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
                : uri.Port;
            return actualPort == expectedPort;
        }

        private static bool HasResponse(TrafficLogEntryViewModel row)
        {
            if (ParseStatusCode(row.Status) is not null)
                return true;

            return !string.IsNullOrWhiteSpace(row.ResponseHeaders)
                || !string.IsNullOrWhiteSpace(row.ResponseBody);
        }

        private static bool IsParameterized(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url.Contains('?', StringComparison.Ordinal);

            return uri.Query.Length > 1;
        }

        private static bool TokenListContains(string list, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return SplitTokens(list).Any(token =>
                string.Equals(token.TrimStart('.'), value, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> SplitTokens(string value)
        {
            return value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static bool IsInScope(TrafficLogEntryViewModel row, TrafficFilterSpec spec)
        {
            if (spec.ScopeHosts is { Count: > 0 })
            {
                if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var uri))
                    return false;

                return MatchesScopeHost(uri.Host, spec.ScopeHosts);
            }

            if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var httpUri))
                return false;
            return httpUri.Scheme is "http" or "https";
        }

        private static bool MatchesScopeHost(string host, IReadOnlyList<string> scopeHosts)
        {
            var normalized = TargetScopeService.NormalizeHost(host);
            if (string.IsNullOrEmpty(normalized))
                return false;

            foreach (var scoped in scopeHosts)
            {
                if (TargetScopeService.HostMatchesScope(normalized, scoped))
                    return true;
            }

            return false;
        }

        private static int? ParseStatusCode(string statusText)
        {
            if (string.IsNullOrWhiteSpace(statusText))
                return null;
            var first = statusText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return int.TryParse(first, out var code) ? code : null;
        }
    }
}
