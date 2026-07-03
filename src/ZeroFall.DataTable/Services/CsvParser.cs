using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZeroFall.DataTable.Services;

public class CsvParseResult
{
    public List<string> Headers { get; set; } = new();
    public List<string[]> Rows { get; set; } = new();
    public int RowCount => Rows.Count;
    public int ColumnCount => Headers.Count;
}

public static class CsvParser
{
    public static CsvParseResult Parse(string filePath, Encoding? encoding = null)
    {
        var result = new CsvParseResult();
        var enc = encoding ?? Encoding.UTF8;

        using var reader = new StreamReader(filePath, enc, detectEncodingFromByteOrderMarks: true);
        var isFirstLine = true;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseLine(line);

            if (isFirstLine)
            {
                result.Headers = fields;
                isFirstLine = false;
            }
            else
            {
                result.Rows.Add(fields.ToArray());
            }
        }

        return result;
    }

    private static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
