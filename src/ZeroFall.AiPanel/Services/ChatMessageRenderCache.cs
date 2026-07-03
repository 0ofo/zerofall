using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZeroFall.AiPanel.Services;

internal static class ChatMessageRenderCache
{
    public static string Serialize(IReadOnlyList<RenderedMarkdownBlock> blocks)
    {
        if (blocks.Count == 0)
            return string.Empty;

        var array = new JsonArray();
        foreach (var block in blocks)
        {
            array.Add(new JsonObject
            {
                ["id"] = block.Id,
                ["html"] = block.Html
            });
        }

        return array.ToJsonString();
    }

    public static List<RenderedMarkdownBlock> Deserialize(string? json)
    {
        var list = new List<RenderedMarkdownBlock>();
        if (string.IsNullOrWhiteSpace(json))
            return list;

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
                return list;

            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                    continue;

                var id = obj["id"]?.GetValue<string>();
                var html = obj["html"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id) || html is null)
                    continue;

                list.Add(new RenderedMarkdownBlock(id, html));
            }
        }
        catch
        {
        }

        return list;
    }

    public static void CopyBlocks(
        IReadOnlyList<RenderedMarkdownBlock> source,
        List<RenderedMarkdownBlock> target,
        HashSet<string> sentIds)
    {
        target.Clear();
        sentIds.Clear();
        foreach (var block in source)
        {
            target.Add(block);
            sentIds.Add(block.Id);
        }
    }
}
