using System;
using System.Globalization;

namespace ZeroFall.AiPanel.Models;

public static class ChatMessageIds
{
    public static string UiId(ChatMessage message) =>
        message.Id > 0
            ? message.Id.ToString(CultureInfo.InvariantCulture)
            : message.PendingUiKey;

    public static bool MatchesUiId(ChatMessage message, string? uiId) =>
        !string.IsNullOrEmpty(uiId)
        && string.Equals(UiId(message), uiId, StringComparison.Ordinal);
}
