using System;

using System.Collections.Generic;

using System.Net.Http;

using System.Text.Json;

using System.Threading;

using System.Threading.Tasks;

using Datafinder.Platform.Models;



namespace Datafinder.Platform.Services;



/// <summary>拉取 OpenAI 兼容 <c>/models</c> 端点并解析模型 id 与上下文窗口。</summary>

public static class AiModelsApiClient

{

    private static readonly string[] ContextFieldNames =

    [

        "context_length",

        "max_model_len",

        "max_context_tokens",

        "context_window",

        "max_input_tokens",

        "input_token_limit",

        "n_ctx",

        "max_tokens"

    ];



    private static readonly string[] NestedObjectNames = ["meta", "top_provider", "config", "permissions", "details"];



    public static async Task<IReadOnlyList<AiModelEntry>> FetchModelsAsync(

        HttpClient client,

        string apiBaseUrl,

        string apiKey,

        CancellationToken cancellationToken = default)

    {

        var baseUrl = AiEndpointCatalog.NormalizeUrl(apiBaseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey.Trim()}");



        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)

        {

            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();

            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {reason}");

        }



        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);



        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)

            throw new InvalidOperationException("响应格式不符合 OpenAI /models 规范（缺少 data 数组）");



        var list = new List<AiModelEntry>();

        foreach (var item in data.EnumerateArray())

        {

            if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)

                continue;



            var id = idProp.GetString()?.Trim();

            if (string.IsNullOrWhiteSpace(id))

                continue;



            list.Add(new AiModelEntry { Id = id, ContextTokens = TryParseContextTokens(item) });

        }



        if (list.Count == 0)

            throw new InvalidOperationException("未从响应中解析到任何模型 id");



        list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        return list;

    }



    public static async Task<IReadOnlyList<string>> FetchModelIdsAsync(

        HttpClient client,

        string apiBaseUrl,

        string apiKey,

        CancellationToken cancellationToken = default)

    {

        var models = await FetchModelsAsync(client, apiBaseUrl, apiKey, cancellationToken).ConfigureAwait(false);

        var ids = new List<string>(models.Count);

        foreach (var model in models)

            ids.Add(model.Id);

        return ids;

    }



    private static int? TryParseContextTokens(JsonElement item)

    {

        var direct = TryParseContextTokensFromObject(item);

        if (direct.HasValue)

            return direct;



        foreach (var name in NestedObjectNames)

        {

            if (!item.TryGetProperty(name, out var nested) || nested.ValueKind != JsonValueKind.Object)

                continue;



            var fromNested = TryParseContextTokensFromObject(nested);

            if (fromNested.HasValue)

                return fromNested;

        }



        return null;

    }



    private static int? TryParseContextTokensFromObject(JsonElement obj)

    {

        foreach (var name in ContextFieldNames)

        {

            if (!obj.TryGetProperty(name, out var prop))

                continue;



            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n) && n > 0)

                return n;



            if (prop.ValueKind == JsonValueKind.String

                && int.TryParse(prop.GetString(), out var parsed)

                && parsed > 0)

                return parsed;

        }



        return null;

    }

}



