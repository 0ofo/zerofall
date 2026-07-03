using System;
using System.Collections.Generic;
using System.Linq;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

public static class AiEndpointCatalog
{
    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        return url.Trim().TrimEnd('/');
    }

    public static IReadOnlyList<AiModelEntry> GetCatalog(AiSettings settings, string? apiBaseUrl)
    {
        var key = NormalizeUrl(apiBaseUrl);
        if (key.Length > 0 && settings.ModelCatalogs.TryGetValue(key, out var list))
            return list;

        return Array.Empty<AiModelEntry>();
    }

    public static void SetCatalog(AiSettings settings, string? apiBaseUrl, IReadOnlyList<AiModelEntry> models)
    {
        var key = NormalizeUrl(apiBaseUrl);
        if (key.Length == 0)
            return;

        settings.ModelCatalogs[key] = models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new AiModelEntry
                {
                    Id = first.Id.Trim(),
                    ContextTokens = g.Select(x => x.ContextTokens).FirstOrDefault(x => x is > 0)
                };
            })
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.Equals(NormalizeUrl(settings.ApiBaseUrl), key, StringComparison.OrdinalIgnoreCase))
        {
            settings.KnownModels = settings.ModelCatalogs[key]
                .Select(m => m.Id)
                .ToList();
        }
    }

    public static AiModelEntry? FindModel(AiSettings settings, string? apiBaseUrl, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var id = modelId.Trim();
        foreach (var entry in GetCatalog(settings, apiBaseUrl))
        {
            if (string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return new AiModelEntry
        {
            Id = id,
            ContextTokens = null
        };
    }

    public static int ResolveContextTokens(AiSettings settings, string? apiBaseUrl, string? modelId)
    {
        var entry = FindModel(settings, apiBaseUrl, modelId);
        if (entry?.ContextTokens is int tokens and > 0)
            return tokens;
        return AiModelContextHints.Resolve(modelId);
    }
}
