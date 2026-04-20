using SafeSweep.Models;
using SafeSweep.Utils;

namespace SafeSweep.ViewModels;

public sealed class GrowthSummaryItemViewModel
{
    public GrowthSummaryItemViewModel(ScanSnapshotSummary summary)
    {
        Summary = summary;
    }

    public ScanSnapshotSummary Summary { get; }
    public string SourceText => Summary.Source switch
    {
        "SafeCleanup" => "安全清理项",
        "UserFiles" => "用户文件",
        "SystemGuidance" => "系统说明项",
        _ => Summary.Source
    };
    public string CurrentText => ByteSizeFormatter.Format(Summary.CurrentBytes);
    public string PreviousText => ByteSizeFormatter.Format(Summary.PreviousBytes);
    public string DeltaText => Summary.DeltaBytes == 0 ? "0 B" : ByteSizeFormatter.Format(Summary.DeltaBytes);
}
