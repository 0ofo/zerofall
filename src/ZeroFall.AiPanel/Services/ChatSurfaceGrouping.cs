using System.Collections.Generic;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

public static class ChatSurfaceGrouping
{
    /// <summary>
    /// 将一段连续消息切成多轮；若开头不是 User，则合并为「仅有 Following」的首块（UserMessage 为空）。
    /// </summary>
    public static List<ChatRoundBlock> GroupIntoRounds(IReadOnlyList<ChatMessage> slice)
    {
        var list = new List<ChatRoundBlock>();
        var visible = new List<ChatMessage>(slice.Count);
        foreach (var message in slice)
        {
            if (message.Visual.IsVisibleInUi())
                visible.Add(message);
        }

        if (visible.Count == 0)
            return list;

        var i = 0;

        if (!visible[0].IsUser)
        {
            var orphan = new ChatRoundBlock { UserMessage = null };
            while (i < visible.Count && !visible[i].IsUser)
                orphan.Following.Add(visible[i++]);
            list.Add(orphan);
        }

        while (i < visible.Count)
        {
            if (!visible[i].IsUser)
            {
                i++;
                continue;
            }

            var user = visible[i++];
            var round = new ChatRoundBlock { UserMessage = user };
            while (i < visible.Count && !visible[i].IsUser)
                round.Following.Add(visible[i++]);

            list.Add(round);
        }

        return list;
    }
}
