using System;
using System.Collections.Generic;
using System.Linq;
using Datafinder.Platform.Models;
using Fluxzy;
using Fluxzy.Rules.Actions;
using Fluxzy.Rules.Filters;

namespace Datafinder.Platform.Services;

internal static class ProxyReplaceFluxzyConfigurator
{
    public static void ApplyRules(FluxzySetting setting, IReadOnlyList<ProxyReplaceRule> rules)
    {
        if (rules.Count == 0)
            return;

        foreach (var rule in rules.Where(static r => r.Enabled && !string.IsNullOrWhiteSpace(r.Match)))
            RegisterRule(setting, rule);
    }

    private static void RegisterRule(FluxzySetting setting, ProxyReplaceRule rule)
    {
        var captured = rule;

        setting.ConfigureRule()
            .WhenAny()
            .Do(new TransformRequestBodyAction(async (ctx, reader) =>
            {
                if (!HostMatches(captured, ctx))
                    return null;

                var text = await reader.ConsumeAsString().ConfigureAwait(false);
                var replaced = ProxyReplaceApplier.ReplaceAll(text, captured);
                return replaced is null ? null : new BodyContent(replaced);
            }));

        setting.ConfigureRule()
            .WhenAny()
            .Do(new TransformResponseBodyAction(async (ctx, reader) =>
            {
                if (!HostMatches(captured, ctx))
                    return null;

                var text = await reader.ConsumeAsString().ConfigureAwait(false);
                var replaced = ProxyReplaceApplier.ReplaceAll(text, captured);
                return replaced is null ? null : new BodyContent(replaced);
            }));
    }

    private static bool HostMatches(ProxyReplaceRule rule, TransformContext ctx)
    {
        var host = ctx.ExchangeContext.Authority?.HostName ?? string.Empty;
        if (string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(ctx.Exchange.FullUrl))
        {
            if (Uri.TryCreate(ctx.Exchange.FullUrl, UriKind.Absolute, out var uri))
                host = uri.Host;
        }

        return ProxyHostMatcher.Matches(rule.Host, host);
    }
}
