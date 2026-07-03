using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ZeroFall.Base.AiTools;

public class ToolDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<ToolParameterDefinition> Parameters { get; init; } = [];

    public string ToOpenAiJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"function\",\"function\":{\"name\":\"");
        sb.Append(EscapeJson(Name));
        sb.Append("\",\"description\":\"");
        sb.Append(EscapeJson(Description));
        sb.Append("\",\"parameters\":{\"type\":\"object\",\"properties\":{");

        var first = true;
        var parameters = Parameters
            .OrderBy(param => param.Name, System.StringComparer.Ordinal)
            .ToList();

        foreach (var param in parameters)
        {
            if (!first) sb.Append(',');
            first = false;

            sb.Append('"');
            sb.Append(EscapeJson(param.Name));
            sb.Append("\":{\"type\":\"");
            sb.Append(EscapeJson(param.Type));
            sb.Append('"');

            if (!string.IsNullOrEmpty(param.Description))
            {
                sb.Append(",\"description\":\"");
                sb.Append(EscapeJson(param.Description));
                sb.Append('"');
            }

            if (param.Type == "array")
                sb.Append(",\"items\":{\"type\":\"string\"}");

            if (param.Type == "string" && param.EnumValues is { Count: > 0 })
            {
                sb.Append(",\"enum\":[");
                for (var i = 0; i < param.EnumValues.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"');
                    sb.Append(EscapeJson(param.EnumValues[i]));
                    sb.Append('"');
                }
                sb.Append(']');
            }

            sb.Append('}');
        }

        sb.Append("},\"required\":[");

        var reqFirst = true;
        foreach (var param in parameters)
        {
            if (!param.Required) continue;
            if (!reqFirst) sb.Append(',');
            reqFirst = false;
            sb.Append('"');
            sb.Append(EscapeJson(param.Name));
            sb.Append('"');
        }

        sb.Append("]}}}");

        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}

public class ToolParameterDefinition
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "string";
    public string Description { get; init; } = "";
    public bool Required { get; init; } = true;
    public List<string>? EnumValues { get; init; }
}
