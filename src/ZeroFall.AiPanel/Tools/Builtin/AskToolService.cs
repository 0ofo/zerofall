using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Services;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.Base.AiTools;

namespace ZeroFall.AiPanel.Tools.Builtin;

public class AskToolService
{
    public Func<AskDialogViewModel, Task<AskResult>>? AskDialogHandler { get; set; }

    [AiTool("ask", "向用户提问并等待回答。支持单选和多选两种模式。单选模式下用户从选项中选择一个，也可取消；多选模式下用户可选择多个选项后提交，也可取消。用户还可以提供补充说明。当需要用户确认、选择或补充信息时使用此工具。")]
    public async Task<string> AskAsync(
        [ToolParam("要向用户提出的问题")] string question,
        [ToolParam("供用户选择的选项列表")] List<string> options,
        [ToolParam("是否允许多选。true=多选模式，false=单选模式（默认）", Required = false)] bool multiSelect = false)
    {
        if (AskDialogHandler == null)
            return ToolResultJson.Error("ask 工具未初始化");

        var vm = new AskDialogViewModel();
        if (multiSelect)
            vm.SetupMultiSelect(question, options);
        else
            vm.SetupSingleSelect(question, options);

        var result = await UiThreadBridge.InvokeAsync(() => AskDialogHandler(vm));

        if (result.IsCancelled)
            return ToolResultJson.Data(o => { o["cancelled"] = true; });

        return ToolResultJson.Data(o =>
        {
            o["cancelled"] = false;
            o["selected"] = new JsonArray(result.SelectedOptions.Select(s => JsonValue.Create(s)).ToArray());
            o["supplementary"] = result.SupplementaryInput ?? string.Empty;
        });
    }
}
