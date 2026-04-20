using SafeSweep.Models;
using SafeSweep.Utils;

namespace SafeSweep.ViewModels;

public sealed class ActionRecordItemViewModel
{
    public ActionRecordItemViewModel(ActionRecord record)
    {
        Record = record;
    }

    public ActionRecord Record { get; }
    public string StartedText => Record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string OperationText => Record.OperationType switch
    {
        ActionPolicy.DirectDelete => "直接清理",
        ActionPolicy.Quarantine => "移入隔离区",
        ActionPolicy.OfficialTool => "官方处理",
        ActionPolicy.Uninstall => "启动卸载",
        ActionPolicy.RecycleBin => "移入回收站",
        _ => "未执行"
    };
    public string StatusText => Record.Status switch
    {
        ActionRecordStatus.Pending => "处理中",
        ActionRecordStatus.Completed => "成功",
        ActionRecordStatus.Failed => "失败",
        ActionRecordStatus.Restored => "已恢复",
        ActionRecordStatus.Skipped => "已跳过",
        _ => Record.Status.ToString()
    };
    public string FreedText => Record.BytesFreed > 0 ? ByteSizeFormatter.Format(Record.BytesFreed) : "-";
    public string Path => Record.Path;
    public string Summary => Record.Summary;
    public bool CanRestore => Record.CanRestore;
}
