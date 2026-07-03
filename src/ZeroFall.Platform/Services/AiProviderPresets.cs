using System;
using System.Collections.Generic;

namespace ZeroFall.Platform.Services;

public sealed record AiProviderPreset(
    string Id,
    string DisplayName,
    string BaseUrl,
    string ApiKeyPlaceholder,
    string? EndpointHint = null);

/// <summary>内置 AI 运营商预设（OpenAI 兼容端点）。</summary>
public static class AiProviderPresets
{
    public const string CustomId = "custom";

    public static readonly AiProviderPreset DeepSeek = new(
        "deepseek",
        "DeepSeek",
        "https://api.deepseek.com/v1",
        "sk-...",
        "官方 OpenAI 兼容接口");

    public static readonly AiProviderPreset Aliyun = new(
        "aliyun",
        "阿里云百炼",
        "https://dashscope.aliyuncs.com/compatible-mode/v1",
        "sk-...",
        "DashScope 兼容模式，模型名如 qwen-plus");

    public static readonly AiProviderPreset Volcengine = new(
        "volcengine",
        "火山方舟",
        "https://ark.cn-beijing.volces.com/api/v3",
        "API Key...",
        "需在控制台创建推理接入点，模型名为接入点 ID");

    public static readonly AiProviderPreset OpenAi = new(
        "openai",
        "OpenAI",
        "https://api.openai.com/v1",
        "sk-...");

    public static readonly AiProviderPreset Custom = new(
        CustomId,
        "自定义",
        string.Empty,
        "sk-...",
        "填写任意 OpenAI 兼容 API 地址");

    public static IReadOnlyList<AiProviderPreset> BuiltIn { get; } =
    [
        DeepSeek,
        Aliyun,
        Volcengine,
        OpenAi,
        Custom
    ];

    public static AiProviderPreset? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        foreach (var preset in BuiltIn)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return null;
    }

    public static AiProviderPreset MatchByUrl(string? url)
    {
        var normalized = AiEndpointCatalog.NormalizeUrl(url);
        if (normalized.Length == 0)
            return Custom;

        foreach (var preset in BuiltIn)
        {
            if (string.Equals(preset.Id, CustomId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(AiEndpointCatalog.NormalizeUrl(preset.BaseUrl), normalized, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return Custom;
    }
}
