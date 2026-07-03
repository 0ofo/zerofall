using System;
using System.Text.Json;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>将 OpenAI 兼容 API 的 HTTP/JSON 错误转为用户可读文案；并识别仅用于 UI 的助手气泡。</summary>
public static class ChatApiErrorHelper
{
    public static string FormatHttpError(int statusCode, string? responseBody)
    {
        var apiMessage = TryParseApiErrorMessage(responseBody);

        return statusCode switch
        {
            401 => string.IsNullOrEmpty(apiMessage)
                ? "API 密钥无效或未授权，请在「AI 设置」中检查 Api Key。"
                : $"API 未授权（HTTP 401）：{apiMessage}",
            402 => FormatInsufficientBalance(apiMessage),
            403 => string.IsNullOrEmpty(apiMessage)
                ? "API 拒绝访问（HTTP 403），请检查密钥权限或模型是否可用。"
                : $"API 拒绝访问（HTTP 403）：{apiMessage}",
            404 => "API 地址不存在（HTTP 404），请检查「API Base URL」是否正确。",
            429 => string.IsNullOrEmpty(apiMessage)
                ? "请求过于频繁（HTTP 429），请稍后再试。"
                : $"请求过于频繁（HTTP 429）：{apiMessage}",
            500 or 502 or 503 or 504 => string.IsNullOrEmpty(apiMessage)
                ? $"API 服务端异常（HTTP {statusCode}），请稍后重试。"
                : $"API 服务端异常（HTTP {statusCode}）：{apiMessage}",
            _ => string.IsNullOrEmpty(apiMessage)
                ? $"API 返回 HTTP {statusCode}。"
                : $"API 返回 HTTP {statusCode}：{apiMessage}"
        };
    }

    public static string FormatException(Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.StartsWith("API ", StringComparison.Ordinal))
            return ex.Message;
        return ex.Message;
    }

    /// <summary>不应作为真实 assistant 回复发给模型的本地气泡（错误、取消、占位提示）。</summary>
    public static bool IsUiOnlyAssistantMessage(ChatMessage m)
    {
        if (m.Role != ChatRole.Assistant || m.HasToolCall)
            return false;

        var t = m.Content.TrimStart();
        if (t.Length == 0)
            return false;

        return t.StartsWith("[错误]", StringComparison.Ordinal)
               || t.Equals("已取消。", StringComparison.Ordinal)
               || t.StartsWith("（模型未返回正文", StringComparison.Ordinal)
               || t.Equals("操作已完成。", StringComparison.Ordinal);
    }

    /// <summary>仅含思考内容、无正文与 tool_calls 的助手气泡（API 会拒收，应合并到下一条 tool/body）。</summary>
    public static bool IsReasoningOnlyAssistant(ChatMessage m) =>
        m.Role == ChatRole.Assistant
        && !m.HasToolCall
        && string.IsNullOrWhiteSpace(m.Content)
        && !string.IsNullOrWhiteSpace(m.ReasoningContent);

    private static string FormatInsufficientBalance(string? apiMessage)
    {
        if (!string.IsNullOrEmpty(apiMessage)
            && (apiMessage.Contains("balance", StringComparison.OrdinalIgnoreCase)
                || apiMessage.Contains("余额", StringComparison.OrdinalIgnoreCase)
                || apiMessage.Contains("Insufficient", StringComparison.OrdinalIgnoreCase)))
        {
            return $"API 账户余额不足（HTTP 402）：{apiMessage}。请到服务商控制台充值，或在「AI 设置」中更换 API Key / Base URL。";
        }

        return "API 账户余额不足（HTTP 402）。请到服务商控制台充值，或在「AI 设置」中更换 API Key / Base URL。";
    }

    private static string? TryParseApiErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
            }

            if (root.TryGetProperty("message", out var topMsg) && topMsg.ValueKind == JsonValueKind.String)
                return topMsg.GetString();
        }
        catch (JsonException)
        {
            if (responseBody.Length <= 200)
                return responseBody.Trim();
        }

        return null;
    }
}
