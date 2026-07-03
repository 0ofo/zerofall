using System;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

/// <summary>按 Base URL 读写各运营商 API Key。</summary>
public static class AiProviderKeyStore
{
    public static string GetKey(AiSettings settings, string? apiBaseUrl)
    {
        var normalized = AiEndpointCatalog.NormalizeUrl(apiBaseUrl);
        if (normalized.Length > 0
         && settings.ProviderApiKeys.TryGetValue(normalized, out var stored)
         && !string.IsNullOrWhiteSpace(stored))
            return stored;

        if (string.Equals(AiEndpointCatalog.NormalizeUrl(settings.ApiBaseUrl), normalized, StringComparison.OrdinalIgnoreCase))
            return settings.ApiKey;

        return string.Empty;
    }

    public static void SetKey(AiSettings settings, string? apiBaseUrl, string apiKey)
    {
        var normalized = AiEndpointCatalog.NormalizeUrl(apiBaseUrl);
        if (normalized.Length == 0)
            return;

        settings.ProviderApiKeys[normalized] = apiKey;

        if (string.Equals(AiEndpointCatalog.NormalizeUrl(settings.ApiBaseUrl), normalized, StringComparison.OrdinalIgnoreCase))
            settings.ApiKey = apiKey;
    }

    public static void MigrateLegacyKey(AiSettings settings)
    {
        if (settings.ProviderApiKeys.Count > 0 || string.IsNullOrWhiteSpace(settings.ApiKey))
            return;

        var normalized = AiEndpointCatalog.NormalizeUrl(settings.ApiBaseUrl);
        if (normalized.Length == 0)
            return;

        settings.ProviderApiKeys[normalized] = settings.ApiKey;
    }
}
