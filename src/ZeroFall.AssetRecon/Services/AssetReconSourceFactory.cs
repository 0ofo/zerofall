using System.Collections.Generic;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Services;

public static class AssetReconSourceFactory
{
    /// <summary>与 <see cref="ViewModels.AssetReconViewModel"/> 情报源 ComboBox 一致：0 聚合，1 FOFA，2 Hunter，3 Quake。</summary>
    public static List<IReconSource> CreateSources(
        AssetReconSettings config,
        IOutboundHttpClientFactory httpClientFactory,
        int sourceModeIndex)
    {
        var sources = new List<IReconSource>();

        void TryAddFofa()
        {
            if (config.FofaEnabled)
                sources.Add(new FofaReconSource(config, httpClientFactory));
        }

        void TryAddHunter()
        {
            if (config.HunterEnabled)
                sources.Add(new HunterReconSource(config, httpClientFactory));
        }

        void TryAddQuake()
        {
            if (config.QuakeEnabled)
                sources.Add(new QuakeReconSource(config, httpClientFactory));
        }

        switch (sourceModeIndex)
        {
            case 0:
                TryAddFofa();
                TryAddHunter();
                TryAddQuake();
                break;
            case 1:
                TryAddFofa();
                break;
            case 2:
                TryAddHunter();
                break;
            case 3:
                TryAddQuake();
                break;
        }

        return sources;
    }

    /// <summary>AI 工具 source 参数 → sourceModeIndex。</summary>
    public static bool TryParseSourceMode(string? source, out int sourceModeIndex, out string? error)
    {
        error = null;
        sourceModeIndex = 0;
        if (string.IsNullOrWhiteSpace(source))
            return true;

        switch (source.Trim().ToLowerInvariant())
        {
            case "aggregate":
            case "auto":
            case "all":
                sourceModeIndex = 0;
                return true;
            case "fofa":
                sourceModeIndex = 1;
                return true;
            case "hunter":
                sourceModeIndex = 2;
                return true;
            case "quake":
                sourceModeIndex = 3;
                return true;
            default:
                error = $"未知 source「{source}」。可用：aggregate、fofa、hunter、quake。";
                return false;
        }
    }
}
