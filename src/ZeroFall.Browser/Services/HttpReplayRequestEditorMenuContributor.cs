using System.Collections.Generic;

namespace ZeroFall.Browser.Services;

public sealed class HttpReplayRequestEditorMenuContributor : IHttpRequestEditorMenuContributor
{
    public void Contribute(HttpRequestEditorMenuContext context, IList<HttpRequestEditorMenuDescriptor> items)
    {
        if (context.Scope != HttpRequestEditorMenuScope.Replay || context.IsReadOnly)
            return;

        items.Add(new HttpRequestEditorMenuDescriptor());
        items.Add(new HttpRequestEditorMenuDescriptor
        {
            Header = "更改请求方式",
            Children = BuildMethodItems(context)
        });
        items.Add(new HttpRequestEditorMenuDescriptor
        {
            Header = "转换选中内容",
            IsEnabled = context.HasSelection,
            Children = BuildTransformItems(context)
        });
    }

    private static IReadOnlyList<HttpRequestEditorMenuDescriptor> BuildMethodItems(
        HttpRequestEditorMenuContext context)
    {
        var children = new List<HttpRequestEditorMenuDescriptor>(HttpRequestTextMutator.CommonMethods.Length);
        foreach (var method in HttpRequestTextMutator.CommonMethods)
        {
            children.Add(new HttpRequestEditorMenuDescriptor
            {
                Header = method,
                Execute = ctx => ctx.SetRequestText(HttpRequestTextMutator.ChangeMethod(ctx.RequestText, method))
            });
        }

        return children;
    }

    private static IReadOnlyList<HttpRequestEditorMenuDescriptor> BuildTransformItems(
        HttpRequestEditorMenuContext context)
    {
        return
        [
            EncodeItem("URL 编码", HttpDecodeOperation.UrlEncode, context),
            EncodeItem("URL 解码", HttpDecodeOperation.UrlDecode, context),
            EncodeItem("Base64 编码", HttpDecodeOperation.Base64Encode, context),
            EncodeItem("Base64 解码", HttpDecodeOperation.Base64Decode, context),
            EncodeItem("Hex 编码", HttpDecodeOperation.HexEncode, context),
            EncodeItem("Hex 解码", HttpDecodeOperation.HexDecode, context),
            EncodeItem("HTML 解码", HttpDecodeOperation.HtmlDecode, context),
            EncodeItem("Unicode 解码", HttpDecodeOperation.UnicodeDecode, context),
            EncodeItem("智能解码", HttpDecodeOperation.Smart, context)
        ];
    }

    private static HttpRequestEditorMenuDescriptor EncodeItem(
        string header,
        HttpDecodeOperation operation,
        HttpRequestEditorMenuContext context) =>
        new()
        {
            Header = header,
            IsEnabled = context.HasSelection,
            Execute = ctx =>
            {
                var selected = ctx.GetSelectedText();
                if (string.IsNullOrEmpty(selected))
                    return;

                ctx.ReplaceSelection(HttpDecoder.Transform(selected, operation));
            }
        };
}
