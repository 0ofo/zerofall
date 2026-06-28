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

    public static void EnsureMigrated(AiSettings settings)
    {
        if (settings.ModelCatalogs.Count > 0 || settings.KnownModels.Count == 0)
            return;

        var key = NormalizeUrl(settings.ApiBaseUrl);
        if (key.Length == 0)
            return;

        settings.ModelCatalogs[key] = settings.KnownModels
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new AiModelEntry
            {
                Id = id,
                ContextTokens = null
            })
            .ToList();
    }

    public static IReadOnlyList<AiModelEntry> GetCatalog(AiSettings settings, string? apiBaseUrl)
    {
        EnsureMigrated(settings);
        var key = NormalizeUrl(apiBaseUrl);
        if (key.Length > 0 && settings.ModelCatalogs.TryGetValue(key, out var list) && list.Count > 0)
            return list;

        return settings.KnownModels
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new AiModelEntry
            {
                Id = id,
                ContextTokens = null
            })
            .ToList();
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
