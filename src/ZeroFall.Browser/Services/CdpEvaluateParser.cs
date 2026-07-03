using System.Text.Json;

namespace ZeroFall.Browser.Services;

internal static class CdpEvaluateParser
{
    public static bool TryGetProtocolError(string json, out string message)
    {
        message = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl))
            {
                message = errEl.ValueKind switch
                {
                    JsonValueKind.String => errEl.GetString() ?? "未知错误",
                    JsonValueKind.Object when errEl.TryGetProperty("message", out var msgEl) =>
                        msgEl.GetString() ?? errEl.GetRawText(),
                    _ => errEl.GetRawText()
                };
                return true;
            }

            if (root.TryGetProperty("result", out var resultWrapper)
                && resultWrapper.TryGetProperty("exceptionDetails", out var exc))
            {
                if (exc.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    message = textEl.GetString() ?? "JavaScript 执行错误";
                else
                    message = exc.GetRawText();
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    public static string? ExtractStringValue(string evaluateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(evaluateJson);
            if (TryGetProtocolError(evaluateJson, out _))
                return null;

            if (!doc.RootElement.TryGetProperty("result", out var resultObj))
                return null;

            if (resultObj.TryGetProperty("exceptionDetails", out _))
                return null;

            if (!resultObj.TryGetProperty("result", out var evalResult))
                return null;

            if (evalResult.TryGetProperty("value", out var valueEl))
                return JsonElementToString(valueEl);

            if (evalResult.TryGetProperty("description", out var descEl)
                && descEl.ValueKind == JsonValueKind.String)
                return descEl.GetString();
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static string? JsonElementToString(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            JsonValueKind.Null => null,
            _ => el.GetRawText()
        };
}
